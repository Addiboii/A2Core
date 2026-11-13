using Aion.Application.Gameplay;
using Aion.Protocol.Native;
namespace Aion.World;

/// <summary>
/// A bot's mind (stage 2, USER 2026-11-09: "make them smart, ultra smart", "use all game logic on them"). It only sees what a player
/// sees on screen (its HP/MP, skills and cooldowns, the monsters around with their HP and who they fight, loot on the ground) and
/// only acts through the requests a game client sends - movement 0x3700/0x3701/0x3718, casts 0x3800, loot 0x5620, respawn 0x8d22,
/// map loaded 0x3622, skill level up 0x383f - so the server's rules (cooldowns, MP, damage, aggro, death, experience, levels, drops)
/// apply to bots exactly as to players.
///
/// What it does, like a careful player:
/// - Hunts monsters of its level (a few below up to two above; elites only when clearly stronger; never heroes or bosses), picks the
///   one that is close, reachable, alone (few aggressive monsters near it that would join) and not taken by another player.
/// - Fights with its class's real skills: self-buffs first, chain follow-ups when the server lights them, area skills on packs,
///   big cooldowns kept for full-HP or dangerous fights, a filler otherwise, an MP reserve; melee closes in, ranged keeps its
///   distance and steps back from a melee monster now and then.
/// - Loots its drops, rests when hurt or out of MP, flees when a fight is lost (several attackers or the monster healthier than it),
///   remembers deaths (hunts lower for a while), revives at the respawn point, and spends its skill points.
/// - Spreads out: avoids other players' spots and moves to a new area when nothing of its level is around.
/// - Gear (USER 2026-11-09): puts on better pieces from drops and rewards (item level, then stats; its class's weapon), dismantles
///   what it will not wear, walks to a merchant to sell loot it does not need and buy HP potions, and turns on their auto use.
/// - Quests (USER 2026-11-09): takes the quests NPCs around offer, works on them (Hero first): kills the named monsters, talks to
///   NPCs, uses quest objects, walks into quest areas, finishes client objectives, enters quest instances, hands in and takes the
///   rewards; far or other-map targets by the quest move button. Where it gets stuck it writes a report (logs/bot-reports.jsonl).
/// - A personality per bot (reaction time, caution, pauses) so a hundred bots do not move as one.
/// </summary>
public sealed class BotBrain(GameSession session, Func<byte[], Task> send, Func<Task<(List<GameSession.BotSight> Monsters, List<Vector3d> Players)>> look,
    NavMeshStore? navStore, Random rng, Func<Task<List<(int Key, int NpcId, Vector3d At)>>>? merchants = null,
    Func<double, Task<List<(int Key, int NpcId, Vector3d At, bool Hostile, string Name)>>>? npcs = null, Action<object>? report = null)
{
    readonly Func<Task<List<(int Key, int NpcId, Vector3d At)>>> lookMerchants = merchants ?? (() => Task.FromResult(new List<(int, int, Vector3d)>()));
    readonly Func<double, Task<List<(int Key, int NpcId, Vector3d At, bool Hostile, string Name)>>> peekNpcs = npcs ?? (_ => Task.FromResult(new List<(int, int, Vector3d, bool, string)>()));
    public const int TickMs = 250;
    public const double WalkSpeed = 380, RunSpeed = 600, MeleeRange = 250, ScanRadius = 4500, LootReach = 250, CrowdRange = 900;

    enum Mode { Explore, Approach, Fight, Loot, Rest, Flee, Dead, Pause, Town }

    sealed record Skill(int Id, int CooldownMs, int Mp, double Range, bool Area, double Radius, int CastMs, bool Buff);

    // Personality.
    readonly int reactionMs = 150 + rng.Next(300);
    readonly double restBelow = 0.45 + rng.NextDouble() * 0.25, fleeBelow = 0.15 + rng.NextDouble() * 0.12;
    readonly double pauseChance = 0.02 + rng.NextDouble() * 0.05;

    Mode mode = Mode.Explore;
    long modeSince, now;
    int targetKey;
    readonly Dictionary<int, long> avoid = [];            // monsters to leave alone until the time
    readonly Dictionary<long, int> lootTries = [];
    readonly Dictionary<int, int> uses = [];
    readonly Dictionary<int, long> buffedAt = [];
    long nextCastAt, nextKiteAt, nextLookAt, nextSkillPointAt = long.MinValue, deadSince, lastLoadAt, restUntil, pauseUntil, lootAfter;
    int levelAbove = 2;
    long levelAboveResetAt;
    int failedExplores;
    List<GameSession.BotSight> monsters = [];
    List<Vector3d> players = [];
    List<Skill> skills = [];
    int skillsLearnedCount = -1;

    // Movement.
    List<Vector3d>? path;
    int pathIndex;
    bool moving;
    double speed = RunSpeed, heading;
    long lastStep, progressAt;
    Vector3d progressFrom;

    public async Task RunAsync(Func<long, Task<List<byte[]>>> tick, CancellationToken ct)
    {
        heading = rng.NextDouble() * 360;
        while (!ct.IsCancellationRequested)
        {
            var at = Now();
            await tick(at);
            try { await StepOnceAsync(at); }
            catch (Exception) when (!ct.IsCancellationRequested) { Errors++; await StopAsync(); path = null; SetMode(Mode.Pause); pauseUntil = now + 3000; }
            await Task.Delay(TickMs * Math.Max(1, Slowdown), ct);
        }
    }

    /// <summary>One thought at <paramref name="at"/> (Unix ms): the live loop calls it every <see cref="TickMs"/>, checks with their own clock.</summary>
    public async Task StepOnceAsync(long at)
    {
        now = at;
        if (lastStep == 0) lastStep = progressAt = at;
        await ThinkAsync();
        lastStep = at;
    }

    /// <summary>Counters for the bot monitor (BotService writes them every minute).</summary>
    public int Kills, Deaths, QuestRewards, Casts, Loots, Reports, Teleports, Errors, Unstucks, Gathers, StepsDone;
    /// <summary>Anti-cheat flags on its own requests (reported, but not counted as stuck reports).</summary>
    public int Flags;
    /// <summary>Loot checks after kills, and how many of them found drops on the ground (loot diagnosis).</summary>
    public int LootLooks, LootSeen;
    long lootCountedFor = -1;

    /// <summary>For a hunt goal: what it sees of its quest monsters and why it does not attack them (bot-detail.json).</summary>
    public string? Sight
    {
        get
        {
            if (questGoal is not { Hunts: true } g || session.BotPosition is not { } at) return null;
            var seen = monsters;
            var wanted = seen.Where(m => g.AnyHostile || g.Wants(m.NpcId, m.Name)).Take(3).Select(m =>
                $"{m.Name} {Flat(at, m.Position):0}cm{(m.Air ? " air" : "")}{(m.Engaged ? $" engaged->{m.VictimKey}" : "")}{(avoid.TryGetValue(m.Key, out var u) && u > now ? " avoided" : "")} hp{m.Hp}/{m.HpMax}");
            return $"{seen.Count} monsters seen, {seen.Count(m => g.AnyHostile || g.Wants(m.NpcId, m.Name))} wanted"
                + (g.Place is { } p ? $", goal place {Flat(at, p):0}cm (near {g.Near:0})" : "") + (IsRanged ? ", ranged" : ", melee") + ": " + string.Join("; ", wanted);
        }
    }
    /// <summary>The server's answer to the bot's last requests (opcodes and first bytes), set by the host: reports quote it.</summary>
    public Func<string>? LastReplies { get; set; }
    /// <summary>What it works on now (bot detail file): the quest and its goal.</summary>
    public string? Quest => questGoal?.Quest;
    public string? Goal => questGoal?.What;
    /// <summary>A town idler (a few bots): stays around its town and the merchants, as players who wait for friends or the market.</summary>
    public bool Idler { get; init; }
    /// <summary>Checks only: work on this map event as if a quest asked for it.</summary>
    internal string? EventForChecks { get; set; }
    /// <summary>All bots think this many times slower when the server is busy (set by the monitor: real players come first).</summary>
    public static volatile int Slowdown = 1;

    /// <summary>What it is doing (staff overview, checks).</summary>
    public string Doing => mode.ToString();
    public int TargetKey => targetKey;

    void SetMode(Mode m) { if (mode != m) { mode = m; modeSince = now; } }

    async Task ThinkAsync()
    {
        if (session.Reloading)
        {
            // A revive or teleport loads the map: the client answers "loaded" (0x3622) as at login.
            if (now - lastLoadAt > 3000) { lastLoadAt = now; moving = false; path = null; await send(new PacketWriter().U16(0x3622).I32(1).ToArray()); }
            return;
        }
        if (session.BotPosition is not { } at || session.MapId is not int map) return;
        if (session.Dead)
        {
            if (mode != Mode.Dead) { SetMode(Mode.Dead); deadSince = now + 4000 + rng.Next(8000); moving = false; path = null; OnDeath(); }
            // A player reads the death screen a few seconds, then revives at the respawn point (ERespawnType 2).
            if (now >= deadSince) { deadSince = now + 5000;
                await send(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray()); }
            return;
        }
        if (mode == Mode.Dead) { SetMode(Mode.Explore); targetKey = 0; }
        if (now >= levelAboveResetAt && levelAbove < 2) { levelAbove++; levelAboveResetAt = now + 600_000; }
        RefreshSkills();
        if (now >= nextLookAt || mode is Mode.Fight or Mode.Approach or Mode.Flee)
        {
            (monsters, players) = await look();
            nextLookAt = now + 600;
        }
        var (hp, hpMax, mp, mpMax, _, _) = session.Resource;
        double hpPct = hpMax > 0 ? (double)hp / hpMax : 1, mpPct = mpMax > 0 ? (double)mp / mpMax : 1;
        var me = session.PlayerKey;
        var attackers = monsters.Where(m => m.Engaged && m.VictimKey == me && !m.VictimIsNpc).ToList();
        var target = monsters.FirstOrDefault(m => m.Key == targetKey);

        // Losing: run (monsters chase 10-15 m, then go home) and rest.
        if (attackers.Count > 0 && mode != Mode.Flee && hpPct < fleeBelow
            && (attackers.Count >= 2 || target is { } t0 && (double)t0.Hp / Math.Max(1, t0.HpMax) > hpPct + 0.3))
        {
            foreach (var a in attackers) avoid[a.Key] = now + 90_000;
            if (await FleeAsync(map, at, attackers)) return;
        }
        if (mode == Mode.Flee)
        {
            if (moving) { await StepAsync(map, at); if (now - modeSince < 9000) return; }
            await StopAsync();
            SetMode(Mode.Rest); restUntil = now + 45_000; targetKey = 0;
            return;
        }

        // Attacked: fight back (the weakest attacker first, the current target if it is one of them).
        if (attackers.Count > 0 && (target is null || !attackers.Any(a => a.Key == targetKey)))
        {
            target = attackers.OrderBy(a => a.Hp).First();
            targetKey = target.Key;
            SetMode(Mode.Fight);
        }

        switch (mode)
        {
            case Mode.Pause:
                if (now >= pauseUntil) SetMode(Mode.Explore);
                return;
            case Mode.Rest:
                if (moving) await StopAsync();
                if (hpPct >= 0.95 && mpPct >= 0.7 || now >= restUntil) SetMode(Mode.Explore);
                return;
            case Mode.Fight or Mode.Approach when target is null:
                // The target died or left: loot after a moment, as a player waits for the drops.
                if (mode == Mode.Fight) Kills++;
                targetKey = 0;
                if (moving) await StopAsync();
                lootAfter = now + 500 + rng.Next(700);
                SetMode(Mode.Loot);
                return;
            case Mode.Fight or Mode.Approach:
                await FightAsync(map, at, target!, attackers, hpPct, mpPct);
                return;
            case Mode.Loot:
                if (now < lootAfter) return;
                if (await LootAsync(map, at)) return;
                break;
        }

        // Out of combat.
        if (hpPct < restBelow || mpPct < 0.25)
        {
            if (moving) await StopAsync();
            SetMode(Mode.Rest); restUntil = now + 60_000;
            return;
        }
        WatchServer();
        if (session.SkillPointsLeft > 0 && now >= nextSkillPointAt) await SpendSkillPointAsync();
        if (await GearAsync()) return;
        if (Idler && mode != Mode.Town && !ShouldShop()) { await IdleAsync(map, at); return; }
        if ((mode == Mode.Town || ShouldShop()) && await TownAsync(map, at)) return;
        if (await QuestAsync(map, at)) return;
        if (questGoal is null && mode == Mode.Explore && await GatherAsync(map, at)) return;
        if (Choose(at) is { } next)
        {
            targetKey = next.Key;
            SetMode(Mode.Approach);
            await FightAsync(map, at, next, attackers, hpPct, mpPct);
            return;
        }
        if (mode == Mode.Explore && !moving && rng.NextDouble() < pauseChance / 4)
        {
            SetMode(Mode.Pause); pauseUntil = now + rng.Next(8_000, 40_000);   // a player stops now and then
            return;
        }
        await ExploreAsync(map, at);
    }

    // ---------------- choosing a monster ----------------

    GameSession.BotSight? Choose(Vector3d at)
    {
        var level = session.Level;
        GameSession.BotSight? best = null;
        var bestScore = double.MaxValue;
        foreach (var m in monsters)
        {
            if (avoid.TryGetValue(m.Key, out var until) && until > now) continue;
            // Someone else's fight: never kill-steal from a player. A monster fighting an NPC (a map event's escort or ally, a guard)
            // is fair game - helping the NPC is what the event wants (live 2026-11-10: HQ001 Cherubim on the event NPC, bots idle).
            var othersFight = m.Engaged && m.VictimKey != session.PlayerKey && !m.VictimIsNpc;
            if (othersFight) continue;
            // Totems and other hit-gauge objects: only when the quest or map event step names them (HQ2101030 DraTotemA, 37 idle bots).
            if (m.HitGauge && !(questGoal is { Hunts: true } wantedBy && wantedBy.Wants(m.NpcId, m.Name))) continue;
            if (questGoal is { } g && g.Hunts)
            {
                if (g.AnyHostile) { if (Flat(at, m.Position) > 3000) continue; }
                else if (!g.Wants(m.NpcId, m.Name)) continue;
            }
            else
            {
                if (m.Rank == NpcRank.Hero) continue;
                if (m.Level > level + levelAbove || m.Level < level - 6) continue;
                if (m.Rank == NpcRank.Elite && m.Level > level - 1) continue;
            }
            if (m.Air && !IsRanged) continue;
            if (m.Hp < m.HpMax / 4 && m.Engaged && m.VictimKey != session.PlayerKey && !m.VictimIsNpc) continue;
            var d = Flat(at, m.Position);
            var crowded = players.Any(p => Flat(p, m.Position) < CrowdRange);
            var danger = monsters.Count(o => o.Key != m.Key && o.Aggressive && !o.Engaged && Flat(o.Position, m.Position) < Math.Max(600, o.AggroRange) + 300);
            var score = d / 100 + danger * 15 + Math.Abs(m.Level - level) * 3 + (crowded ? 30 : 0) + (m.Rank == NpcRank.Elite ? 10 : 0);
            if (score < bestScore) { bestScore = score; best = m; }
        }
        return best;
    }

    // ---------------- fighting ----------------

    bool IsRanged => skills.Count(s => !s.Buff && s.Range >= 1200) * 2 > skills.Count(s => !s.Buff);

    async Task FightAsync(int map, Vector3d at, GameSession.BotSight t, List<GameSession.BotSight> attackers, double hpPct, double mpPct)
    {
        var skill = PickSkill(at, t, attackers, mpPct);
        var reach = (skill?.Range ?? (IsRanged ? 1500 : MeleeRange)) + t.Radius;
        var d = Flat(at, t.Position);
        if (d > reach - 40)
        {
            // Close in: to most of the reach, along the floor.
            if (mode == Mode.Approach && now - modeSince > 25_000) { avoid[t.Key] = now + 60_000; targetKey = 0; await StopAsync(); SetMode(Mode.Explore); return; }
            var stand = Toward(t.Position, at, Math.Max(80, reach * 0.7));
            if (path is null || !moving || Flat(path[^1], stand) > 150) await GoAsync(map, at, stand, RunSpeed);
            else await StepAsync(map, at);
            if (Stuck(at)) { avoid[t.Key] = now + 30_000; targetKey = 0; await StopAsync(); SetMode(Mode.Explore); }
            return;
        }
        SetMode(Mode.Fight);
        // Ranged: step back from a melee monster on top of it now and then (not all the time - players do not kite forever).
        if (IsRanged && t.VictimKey == session.PlayerKey && d < 350 && now >= nextKiteAt && hpPct < 0.8)
        {
            nextKiteAt = now + rng.Next(6_000, 12_000);
            await GoAsync(map, at, Toward(at, t.Position, -700), RunSpeed);
            return;
        }
        if (moving) { await StopAsync(); nextCastAt = Math.Max(nextCastAt, now + reactionMs / 2); }
        if (skill is null || now < nextCastAt) return;
        heading = Heading(at, t.Position);
        var aimKey = skill.Buff ? session.PlayerKey : t.Key;
        await send(Cast(skill.Id, aimKey, heading, at));
        uses[skill.Id] = uses.GetValueOrDefault(skill.Id) + 1;
        Casts++;
        if (skill.Buff) buffedAt[skill.Id] = now;
        nextCastAt = now + Math.Max(skill.CastMs, 650) + reactionMs / 2 + rng.Next(150);
    }

    Skill? PickSkill(Vector3d at, GameSession.BotSight t, List<GameSession.BotSight> attackers, double mpPct)
    {
        var (_, _, mp, _, _, _) = session.Resource;
        bool Usable(Skill s) => session.SkillReady(s.Id, now) && s.Mp <= mp && (mpPct > 0.2 || s.Mp == 0 || s.CooldownMs >= 8000);
        // A chain follow-up the server lit up: always worth it.
        foreach (var chain in session.OfferedChains(now))
            if (SkillOf(chain) is { } c && session.SkillReady(c.Id, now) && c.Mp <= mp) return c;
        // Self-buffs at the start of a fight.
        if (skills.FirstOrDefault(s => s.Buff && Usable(s) && now - buffedAt.GetValueOrDefault(s.Id, long.MinValue / 2) > Math.Max(s.CooldownMs, 60_000)) is { } buff)
            return buff;
        var damage = skills.Where(s => !s.Buff && Usable(s)).ToList();
        if (damage.Count == 0) return null;
        // A pack: the area skill that covers most of it.
        var pack = monsters.Count(m => Flat(m.Position, t.Position) < 450 && (m.Engaged && m.VictimKey == session.PlayerKey || m.Key == t.Key));
        if (pack >= 3 && damage.Where(s => s.Area).OrderByDescending(s => s.Radius).FirstOrDefault() is { } area) return area;
        // Big cooldowns for fights that need them: a healthy or strong target, or several attackers.
        var tPct = (double)t.Hp / Math.Max(1, t.HpMax);
        var worth = tPct > 0.4 || attackers.Count >= 2 || t.Rank != NpcRank.Normal || t.Level > session.Level;
        var pick = damage.Where(s => worth || s.CooldownMs < 15_000).OrderByDescending(s => s.CooldownMs).ThenBy(_ => rng.Next()).FirstOrDefault();
        return pick ?? damage.OrderBy(s => s.CooldownMs).First();
    }

    Skill? SkillOf(int id) => skills.FirstOrDefault(s => s.Id == id) ?? Describe(id);

    void RefreshSkills()
    {
        var learned = session.LearnedSkills;
        if (learned.Count == skillsLearnedCount) return;
        skillsLearnedCount = learned.Count;
        skills = learned.Select(Describe).OfType<Skill>().ToList();
    }

    Skill? Describe(int id)
    {
        if (id < 11_000_000 || id == session.BotDodgeSkill || id == session.BotDodgeSkill + 1 || session.SkillInfo(id) is not { } def || def.Type != "Active") return null;
        var guide = SkillGuide.Embedded.Of(id);
        var behaviour = SkillBehaviour.Embedded.Of(id);
        if (behaviour is { Charge: true } || behaviour?.Target is { } tt && (tt.Contains("Ground", StringComparison.OrdinalIgnoreCase) || tt.Contains("Point", StringComparison.OrdinalIgnoreCase)))
            return null;   // charged and ground-aimed skills need the client's extra requests
        if (behaviour?.AutoLoad is not null) return null;
        var damage = def.Damage.Length > 0 || guide?.DamageByLevel.Count > 0;
        var buff = !damage && behaviour?.Disposition == "Positive";
        var debuff = !damage && behaviour?.Disposition == "Negative";
        if (!damage && !buff && !debuff) return null;
        var range = guide?.RangeCm > 0 ? guide.RangeCm : guide?.RadiusCm > 0 && guide.Shape != "target" ? guide.RadiusCm : MeleeRange;
        return new Skill(id, def.CooldownMs, def.Cost.Mp, Math.Clamp(range, 150, 2500), guide?.Targets > 1, guide?.RadiusCm ?? 0, behaviour?.CastingMs ?? 0, buff);
    }

    async Task SpendSkillPointAsync()
    {
        nextSkillPointAt = now + 30_000 + rng.Next(30_000);
        // The skill it uses most (ties: the stronger one).
        var best = skills.Where(s => !s.Buff).OrderByDescending(s => uses.GetValueOrDefault(s.Id)).ThenByDescending(s => s.CooldownMs).FirstOrDefault();
        if (best is not null) await send(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(best.Id).ToArray());
    }

    void OnDeath()
    {
        // A lesson: hunt lower for a while, and leave the killers alone.
        Deaths++;
        levelAbove = Math.Max(0, levelAbove - 1);
        levelAboveResetAt = now + 600_000;
        foreach (var m in monsters.Where(m => m.Engaged && m.VictimKey == session.PlayerKey)) avoid[m.Key] = now + 300_000;
        targetKey = 0;
    }

    // ---------------- gear: wear the best, dismantle the rest ----------------

    /// <summary>Equipment categories and the EEquipSlot(s) they go in (game.equipment_rules, 2026-11-09).</summary>
    static readonly Dictionary<string, int[]> SlotsOf = new()
    {
        ["Greatsword"] = [1], ["Sword"] = [1], ["Dagger"] = [1], ["Bow"] = [1], ["Magicbook"] = [1], ["Orb"] = [1], ["Mace"] = [1], ["Staff"] = [1],
        ["Guarder"] = [2], ["Helmet"] = [3], ["Shoulder"] = [4], ["Torso"] = [5], ["Pants"] = [6], ["Gloves"] = [7], ["Boots"] = [8], ["Cape"] = [19],
        ["Necklace"] = [10], ["Earring"] = [11, 12], ["Ring"] = [13, 14], ["Bracelet"] = [15, 16], ["Belt"] = [17], ["Amulet"] = [22],
    };
    static readonly HashSet<string> Weapons = ["Greatsword", "Sword", "Dagger", "Bow", "Magicbook", "Orb", "Mace", "Staff"];

    readonly HashSet<long> cannotWear = [], dismantleTried = [];
    (long Key, long At)? lastEquip;
    long nextGearAt;

    static double Score(ItemInfo info) => info.Level * 1_000_000.0 + info.Stats.Values.Sum(v => (double)Math.Max(0, v));

    bool Wearable(ItemInfo info) =>
        SlotsOf.ContainsKey(info.Category) && info.PermitLevel <= session.Level && (info.Race == 0 || info.Race == session.BotRace)
        && (!Weapons.Contains(info.Category) || session.BotWeaponCategory is not { } held || held == info.Category);

    /// <summary>One gear action at a time, as a player: put on a better piece (checking the bag after loot), or dismantle what is worse.</summary>
    async Task<bool> GearAsync()
    {
        if (now < nextGearAt) return false;
        nextGearAt = now + 4000 + rng.Next(3000);
        var items = session.BotItems();
        // The last equip did not happen (the class cannot wear it): never again.
        if (lastEquip is { } last && now - last.At > 1500)
        {
            if (items.Any(i => i.Key == last.Key && i.Location == "bag")) cannotWear.Add(last.Key);
            lastEquip = null;
        }
        var worn = new Dictionary<int, GameSession.BotItem>();
        foreach (var i in items.Where(i => i.Location == "equipment" && i.Slot is int)) worn[i.Slot!.Value] = i;
        double WornScore(int slot) => worn.TryGetValue(slot, out var w) && session.BotItemInfo(w.TemplateId) is { } wi ? Score(wi) : -1;
        foreach (var item in items.Where(i => i.Location == "bag" && i.Quantity == 1 && !cannotWear.Contains(i.Key)))
        {
            if (session.BotItemInfo(item.TemplateId) is not { } info || !Wearable(info)) continue;
            var slot = SlotsOf[info.Category].OrderBy(WornScore).First();   // the empty or weaker of a pair
            if (Score(info) <= WornScore(slot)) continue;
            lastEquip = (item.Key, now);
            await send(new PacketWriter().U16(0xe247).I64(item.Key).U8((byte)slot).ToArray());
            nextGearAt = now + 1200 + rng.Next(800);
            return true;
        }
        // Dismantle gear it will not wear: worse than what it has, or not for its class or race. Pieces for the next few levels are kept.
        var junk = new List<long>();
        foreach (var item in items.Where(i => i.Location == "bag" && i.Quantity == 1 && !dismantleTried.Contains(i.Key)))
        {
            if (session.BotItemInfo(item.TemplateId) is not { } info || !SlotsOf.TryGetValue(info.Category, out var slots)) continue;
            var later = info.PermitLevel > session.Level && info.PermitLevel <= session.Level + 3 && (info.Race == 0 || info.Race == session.BotRace)
                && !cannotWear.Contains(item.Key) && Score(info) > slots.Min(WornScore);
            if (later) continue;
            if (cannotWear.Contains(item.Key) || !Wearable(info) || Score(info) <= slots.Min(WornScore)) junk.Add(item.Key);
        }
        if (junk.Count == 0) return false;
        foreach (var key in junk) dismantleTried.Add(key);
        var dw = new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint((uint)junk.Count);
        foreach (var key in junk) dw.U64((ulong)key);
        await send(dw.ToArray());
        return true;
    }

    // ---------------- town: sell junk, buy potions ----------------

    long nextTownAt = long.MinValue, townSince;
    (int Key, int NpcId, Vector3d At)? merchant;

    static bool IsHpPotion(int itemId) =>
        RewardBoxes.Embedded.Cooldown(itemId).Group is var g && g.Contains("potion", StringComparison.OrdinalIgnoreCase) && g.Contains("hp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loot it does not need: not gear (dismantled instead), not Kina or currencies, not its potions; what a merchant buys.</summary>
    List<GameSession.BotItem> Sellable(List<GameSession.BotItem> items) =>
        items.Where(i => i.Location == "bag" && i.TemplateId / 100_000 != 9301 && !IsHpPotion(i.TemplateId)
            && session.BotItemInfo(i.TemplateId) is null && ShopTables.Embedded.SellPrice(i.TemplateId) is > 0).ToList();

    bool ShouldShop()
    {
        if (now < nextTownAt) return false;
        var items = session.BotItems();
        var potions = items.Where(i => i.Location == "bag" && IsHpPotion(i.TemplateId)).Sum(i => i.Quantity);
        return Sellable(items).Count >= 6 || items.Count(i => i.Location == "bag") >= 45 || potions < 5 && session.BotKina >= 300;
    }

    /// <summary>A trip to the nearest merchant: walk there, sell the loot it does not need, buy HP potions and turn on their auto use.</summary>
    async Task<bool> TownAsync(int map, Vector3d at)
    {
        if (mode != Mode.Town)
        {
            nextTownAt = now + 15 * 60_000;
            var found = await lookMerchants();
            merchant = found.Count > 0 ? found[0] : null;
            if (merchant is null) return false;
            SetMode(Mode.Town); townSince = now;
        }
        if (merchant is not { } shop || now - townSince > 240_000) { SetMode(Mode.Explore); return false; }
        if (Flat(at, shop.At) > 350)
        {
            if (path is null || !moving)
            {
                if (!await GoAsync(map, at, Toward(shop.At, at, 200), RunSpeed)) { SetMode(Mode.Explore); return false; }
            }
            else await StepAsync(map, at);
            return true;
        }
        if (moving) await StopAsync();
        heading = Heading(at, shop.At);
        await send(new PacketWriter().U16(GameSession.ShopOpenRq).Varint((uint)shop.Key).ToArray());
        var sell = Sellable(session.BotItems()).Take(40).ToList();
        if (sell.Count > 0)
        {
            var w = new PacketWriter().U16(GameSession.ShopSellRq).Varint((uint)shop.Key).Varint((uint)sell.Count);
            foreach (var item in sell) w.U64((ulong)item.Key).U64((ulong)item.Quantity);
            await send(w.ToArray());
        }
        var kina = session.BotKina;
        var potion = ShopTables.Embedded.AllGoods(shop.NpcId, session.BotRace)
            .Where(g => g.Row.Currency == GameSession.KinaItemId && g.Row.Level <= session.Level && g.Row.Price > 0 && IsHpPotion(g.Row.ItemId))
            .OrderByDescending(g => g.Row.Level).ThenBy(g => g.Row.Price).FirstOrDefault();
        if (potion.Row is { } row)
        {
            var count = (int)Math.Clamp(kina * 4 / 10 / row.Price, 0, 30);
            if (count > 0)
            {
                await send(new PacketWriter().U16(GameSession.ShopBuyRq).Varint((uint)session.PlayerKey).Varint((uint)shop.Key).Varint(1u).I32(potion.GoodsId).I32(count).ToArray());
                if (!session.BotPotionsSet) await send(new PacketWriter().U16(GameSession.PotionAutoUseRq).U8(1).I32(row.ItemId).I32(0).ToArray());
            }
        }
        SetMode(Mode.Pause); pauseUntil = now + rng.Next(3000, 9000);   // a moment at the shop
        return true;
    }

    // ---------------- quests: take them, do them, hand them in; report where it gets stuck ----------------

    /// <summary>
    /// What the quest wants now. Kill: hunt only these monsters. Go: walk to a place (and maybe act there). Teleport: the journal's
    /// move button (0x8d16), as players use it for a far or other-map target. Act: a request to send now.
    /// </summary>
    sealed record QuestGoal(string Quest, string What, int[]? KillIds = null, Vector3d? Place = null, double Near = 300, Func<Task>? Act = null,
        bool Teleport = false, string[]? KillNames = null, bool AnyHostile = false, bool Hold = false)
    {
        /// <summary>A hunt goal: these monsters (ids or spawner names), any hostile near (an event wave), or nothing (an empty list: wait).</summary>
        public bool Hunts => KillIds is not null || KillNames is not null || AnyHostile;
        public bool Wants(int npcId, string name) =>
            KillIds is { } ids && ids.Contains(npcId) || KillNames is { } names && names.Any(n => NameIs(n, name));
    }

    /// <summary>A map event names a spawner ("M_D_Starter_HQ001_SkurvWar_02"); its NPCs carry that name or the name plus "_n".</summary>
    static bool NameIs(string wanted, string name) =>
        string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(wanted + "_", StringComparison.OrdinalIgnoreCase);

    readonly Dictionary<string, long> questBlocked = [];        // quest code -> skip it until
    readonly Dictionary<string, (string Key, long Since, int Goals)> questProgress = [];
    readonly Dictionary<string, long> reported = [];
    long nextQuestLookAt, nextOfferLookAt, questActAt, lastTeleportAt;
    QuestGoal? questGoal;
    public const int QuestStuckMs = 8 * 60_000, QuestBlockMs = 20 * 60_000;

    /// <summary>Writes a stuck report (once per bot, quest and reason an hour) - staff reads them in logs/bot-reports.jsonl.</summary>
    void Report(string quest, string reason, object? detail = null)
    {
        var key = quest + "|" + reason;
        if (reported.TryGetValue(key, out var at) && now - at < 3_600_000) return;
        reported[key] = now;
        if (reason.StartsWith("anti-cheat", StringComparison.Ordinal)) Flags++; else Reports++;
        var record = quest.Length == 0 ? null : session.Quests?.Get(quest);
        var def = quest.Length == 0 ? null : QuestBook.Embedded.Find(quest);
        var step = record is null ? null : session.Quests!.CurrentStep(record);
        report?.Invoke(new
        {
            time = DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("u"), bot = session.CharacterName, level = session.Level, map = session.MapId,
            at = session.BotPosition is { } p ? new[] { Math.Round(p.X), Math.Round(p.Y), Math.Round(p.Z) } : null,
            quest, title = def?.Title, type = def?.Type, status = record?.Status, step = record?.Step,
            objectives = step?.Objectives.Select((o, i) => new { o.Type, o.Npc, o.Npcs, o.Target, o.Envs, o.Count, done = record!.Goals.ElementAtOrDefault(i) }),
            mapEvent = EventState(),
            reason, detail, reply = LastReplies?.Invoke(),
        });
    }

    /// <summary>The quest to work on (Hero first, then the lowest level), skipping blocked ones.</summary>
    QuestRecord? PickQuest()
    {
        if (session.Quests is not { } log) return null;
        return log.All.Where(q => q.Status is QuestLog.Incomplete or QuestLog.Complete && !(questBlocked.TryGetValue(q.Code, out var until) && until > now))
            .Select(q => (Record: q, Def: log.Book.Find(q.Code)))
            .Where(x => x.Def is not null && x.Def.Type != "DutyScroll")
            .OrderBy(x => x.Record.Status == QuestLog.Complete ? 0 : 1)
            .ThenBy(x => x.Def!.Type == "Hero" ? 0 : 1).ThenBy(x => x.Def!.Level)
            .Select(x => x.Record).FirstOrDefault();
    }

    /// <summary>One step of questing; false when there is nothing to do (it grinds instead).</summary>
    async Task<bool> QuestAsync(int map, Vector3d at)
    {
        if (EventForChecks is null && now >= nextOfferLookAt && await TakeOffersAsync(map, at)) return true;
        if (now >= nextQuestLookAt || questGoal is null)
        {
            nextQuestLookAt = now + 3000;
            questGoal = await GoalAsync(map, at);
        }
        if (questGoal is not { } goal) return false;
        lastGoalWhat = goal.What;
        if (goal.Hunts)
        {
            // The hunt (Choose) takes it from here when one of its monsters is in sight; else walk to where they live, or wait there.
            if (Choose(at) is not null) return false;
            if (goal.Place is { } huntAt && Flat(at, huntAt) > goal.Near)
            {
                if (path is null || !moving || Flat(path[^1], huntAt) > goal.Near)
                {
                    if (!await GoAsync(map, at, huntAt, RunSpeed))
                    {
                        if (now - lastTeleportAt > 60_000) { questGoal = goal with { Teleport = true, Place = null }; return true; }
                        Report(goal.Quest, "no path to the quest monsters", new { goal.What, place = new[] { Math.Round(huntAt.X), Math.Round(huntAt.Y), Math.Round(huntAt.Z) } });
                        questBlocked[goal.Quest] = now + QuestBlockMs; questGoal = null;
                        return false;
                    }
                }
                else await StepAsync(map, at);
                await UnstuckAsync(map, at, goal);
                return true;
            }
            if (goal.Hold) { if (moving) await StopAsync(); return true; }
            return false;
        }
        if (goal.Teleport)
        {
            if (now - lastTeleportAt < 20_000) return true;
            lastTeleportAt = now;
            Teleports++;
            if (session.Quests?.Get(goal.Quest) is { } r)
            {
                await StopAsync();
                await send(new PacketWriter().U16(GameSession.QuestTeleportRq).I32((int)r.Uid).ToArray());
                questGoal = null; nextQuestLookAt = now + 4000;
                // Still here after the move button: there is nowhere it leads.
                pendingTeleportCheck = (goal.Quest, map, at, now);
            }
            return true;
        }
        if (goal.Place is { } place && Flat(at, place) > goal.Near)
        {
            if (path is null || !moving || Flat(path[^1], place) > goal.Near)
            {
                if (!await GoAsync(map, at, place, RunSpeed))
                {
                    // No walkable way (a long route or another floor): the quest's move button, as a player does; only then a report.
                    if (now - lastTeleportAt > 60_000) { questGoal = goal with { Teleport = true, Place = null }; return true; }
                    Report(goal.Quest, "no path to the quest place", new { goal.What, place = new[] { place.X, place.Y, place.Z } });
                    questBlocked[goal.Quest] = now + QuestBlockMs; questGoal = null;
                    return false;
                }
            }
            else await StepAsync(map, at);
            await UnstuckAsync(map, at, goal);
            return true;
        }
        if (moving) await StopAsync();
        if (goal.Act is { } act && now >= questActAt)
        {
            var actKey = goal.Quest + "|" + goal.What + "|" + questProgress.GetValueOrDefault(goal.Quest).Key + "|" + questProgress.GetValueOrDefault(goal.Quest).Goals;
            var tries = actTries[actKey] = actTries.GetValueOrDefault(actKey) + 1;
            if (tries > 4)
            {
                Report(goal.Quest, "the quest action did nothing", new { goal.What, place = goal.Place is { } gp ? new[] { Math.Round(gp.X), Math.Round(gp.Y), Math.Round(gp.Z) } : null });
                questBlocked[goal.Quest] = now + QuestBlockMs; questGoal = null; actTries.Remove(actKey);
                return false;
            }
            if (actTries.Count > 300) actTries.Clear();
            questActAt = now + 2500 + rng.Next(1500);
            await act();
            nextQuestLookAt = now + 1500;
        }
        return true;
    }

    (string Quest, int Map, Vector3d At, long When)? pendingTeleportCheck;

    /// <summary>Takes the quests NPCs near it offer (walks to the NPC, talks, accepts), as a player picks up what is around.</summary>
    async Task<bool> TakeOffersAsync(int map, Vector3d at)
    {
        nextOfferLookAt = now + 20_000;
        if (session.Quests is not { } log) return false;
        if (log.All.Count(q => q.Status == QuestLog.Incomplete) >= 8) return false;
        var npcs = await peekNpcs(5000);
        foreach (var npc in npcs.Where(n => !n.Hostile).OrderBy(n => Flat(at, n.At)))
        {
            var offered = log.OfferedBy(npc.NpcId, session.Level).Where(c => log.Book.Find(c)?.Type != "DutyScroll").ToList();
            if (offered.Count == 0) continue;
            questGoal = new QuestGoal(offered[0], "take quests", Place: npc.At, Near: 300, Act: async () =>
            {
                await send(new PacketWriter().U16(0xe240).I32(npc.Key).ToArray());
                foreach (var code in offered)
                    if (log.Book.Find(code) is { } def) await send(new PacketWriter().U16(0x8d0a).I32(def.Id).I32(npc.Key).ToArray());
                await send(new PacketWriter().U16(0xe242).I32(npc.Key).ToArray());
                questGoal = null;
            });
            nextQuestLookAt = now + 30_000;   // keep this goal until it is done
            return true;
        }
        return false;
    }

    async Task<QuestGoal?> GoalAsync(int map, Vector3d at)
    {
        // The move button did nothing: report and leave the quest for a while.
        if (pendingTeleportCheck is { } check && now - check.When > 6000)
        {
            pendingTeleportCheck = null;
            if (session.MapId == check.Map && session.BotPosition is { } p && Flat(p, check.At) < 500)
            {
                Report(check.Quest, "quest teleport led nowhere");
                questBlocked[check.Quest] = now + QuestBlockMs;
            }
        }
        if (EventForChecks is { } pinned) return await EventGoalAsync("CHECK", pinned, map, at);
        // A reward it asked for: paid (counted), or refused (reported with the server's answer).
        if (pendingReward is { } asked)
        {
            if (session.BotQuestRewarded(asked.Quest)) { QuestRewards++; pendingReward = null; }
            else if (now - asked.At > 8000) { Report(asked.Quest, "the reward was refused"); pendingReward = null; }
        }
        if (PickQuest() is not { } record || session.Quests is not { } log || log.Book.Find(record.Code) is not { } def) return null;
        // Stuck: the same objective with no progress for a long time.
        var stepKey = $"{record.Status}:{record.Step}";
        var goals = record.Goals.Sum();
        if (!questProgress.TryGetValue(record.Code, out var prog) || prog.Key != stepKey || prog.Goals != goals)
        {
            if (prog.Key is not null) StepsDone++;
            questProgress[record.Code] = (stepKey, now, goals);
        }
        else if (now - prog.Since > QuestStuckMs)
        {
            Report(record.Code, "no progress for 8 minutes", lastGoalWhat);
            questBlocked[record.Code] = now + QuestBlockMs;
            questProgress.Remove(record.Code);
            return null;
        }
        var uid = (int)record.Uid;
        if (record.Status == QuestLog.Complete)
        {
            if (def.Complete != "Npc" || def.FinishNpc is not int finisher)
                return new QuestGoal(record.Code, "take the reward", Act: () => { pendingReward = (record.Code, now); return send(new PacketWriter().U16(0x8d0e).I32(uid).I32(def.Rewards?.Select is { Length: > 0 } ? 0 : -1).ToArray()); });
            return await NpcGoalAsync(record.Code, "hand in", finisher, map, at, async key =>
            {
                await send(new PacketWriter().U16(0xe240).I32(key).ToArray());
                pendingReward = (record.Code, now); await send(new PacketWriter().U16(0x8d0e).I32(uid).I32(def.Rewards?.Select is { Length: > 0 } ? 0 : -1).ToArray());
                await send(new PacketWriter().U16(0xe242).I32(key).ToArray());
            });
        }
        if (log.CurrentStep(record) is not { } step) { Report(record.Code, "quest step missing in the data"); questBlocked[record.Code] = now + QuestBlockMs; return null; }
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Optional || record.Goals.ElementAtOrDefault(i) >= o.Count) continue;
            var index = i;
            var npcIds = o.Npcs is { Length: > 0 } ids ? ids : o.Npc is int one ? [one] : step.Npc is int sn ? [sn] : [];
            switch (o.Type)
            {
                case "KillNpc" or "CollectItem" or "CollectFakeItem":
                {
                    if (npcIds.Length == 0) { Report(record.Code, $"{o.Type} without monsters in the data"); break; }
                    var living = (await peekNpcs(ScanRadius * 2)).Where(n => npcIds.Contains(n.NpcId) && n.Hostile).OrderBy(n => Flat(at, n.At)).ToList();
                    if (living.Count > 0) return new QuestGoal(record.Code, o.Type, KillIds: npcIds, Place: living[0].At, Near: 1200);
                    if (NearestSpawn(map, at, npcIds) is { } spawn)
                        return Flat(at, spawn) > 30_000 ? new QuestGoal(record.Code, o.Type, Teleport: true) : new QuestGoal(record.Code, o.Type, KillIds: npcIds, Place: spawn, Near: 1500);
                    return new QuestGoal(record.Code, o.Type + " elsewhere", Teleport: true);
                }
                case "AskNpc" or "CloseNpc":
                    if (npcIds.Length == 0) { Report(record.Code, "talk objective without an NPC in the data"); break; }
                    return await NpcGoalAsync(record.Code, o.Type, npcIds[0], map, at, async key =>
                    {
                        await send(new PacketWriter().U16(0xe240).I32(key).ToArray());
                        await send(new PacketWriter().U16(0x8d2b).I32(uid).I32(key).ToArray());
                        await send(new PacketWriter().U16(0xe242).I32(key).ToArray());
                    });
                case "UseEnvObj":
                {
                    var objects = WorldObjects.ObjectsOn(map);
                    var best = -1; var bestD = double.MaxValue;
                    for (var k = 0; k < objects.Count; k++)
                        if ((o.Envs ?? []).Contains(objects[k].Env) && !usedTried.Contains((map, k)) && Flat(at, objects[k].At) is var d && d < bestD) { best = k; bestD = d; }
                    if (best < 0) return new QuestGoal(record.Code, "UseEnvObj elsewhere", Teleport: true);
                    var obj = objects[best]; var objKey = WorldObjects.FirstKey + best; var slot = best;
                    return new QuestGoal(record.Code, "use " + obj.Name, Place: obj.At, Near: Math.Max(150, obj.InteractRange - 50), Act: async () =>
                    {
                        await send(new PacketWriter().U16(0x3636).I32(objKey).ToArray());
                        usedTried.Add((map, slot));
                        questActAt = now + obj.InteractTime + 1500;
                    });
                }
                case "EnterVolumePC":
                {
                    var trigger = WorldObjects.TriggersOn(map).FirstOrDefault(t => string.Equals(t.Name, o.Target, StringComparison.OrdinalIgnoreCase));
                    if (trigger is null) return new QuestGoal(record.Code, "EnterVolumePC elsewhere", Teleport: true);
                    return new QuestGoal(record.Code, "enter " + trigger.Name, Place: trigger.At, Near: 100);
                }
                case "EnterSubZone":
                    if (o.Target is { } sub && Subzones.Locate(sub) is { } loc)
                        return loc.Map == map ? new QuestGoal(record.Code, "enter " + sub, Place: loc.Center, Near: 200) : new QuestGoal(record.Code, "enter " + sub, Teleport: true);
                    Report(record.Code, "subzone not found", o.Target);
                    break;
                case "CompleteCelviceTalk" or "CloseNote" or "UseFakeItem":
                    return new QuestGoal(record.Code, o.Type, Act: () => send(new PacketWriter().U16(0x9230).U8(1).I32(uid).I32(index).ToArray()));
                case "ClearMapEvent":
                    if (o.Target is not { Length: > 0 } eventName) { Report(record.Code, "ClearMapEvent without an event name in the data"); break; }
                    if (await EventGoalAsync(record.Code, eventName, map, at) is { } eventGoal) return eventGoal;
                    break;
                case "EngraveTeleportArtifact":
                {
                    var placed = Kibelisks.OnMap(map);
                    var k = -1;
                    for (var n = 0; n < placed.Count; n++) if (placed[n].Artifact == o.Target) { k = n; break; }
                    if (k < 0) { Report(record.Code, "Kibelisk not on this map", o.Target); break; }
                    var stone = placed[k]; var stoneKey = Kibelisks.FirstKey + k;
                    var stoneAt = new Vector3d(stone.Position[0], stone.Position[1], stone.Position[2]);
                    return new QuestGoal(record.Code, "bind " + stone.Name, Place: Toward(stoneAt, at, 250), Near: 150,
                        Act: () => send(new PacketWriter().U16(0x3636).I32(stoneKey).ToArray()));
                }
                case "ClearQuest":
                    // Waits for another quest (HQ2101060 -> the Ascension quest AQ2602000): work on that one meanwhile, as a player follows
                    // the tracker. Only a quest it waits for that is not in the log is reported.
                    if (o.Target is { } waitsFor && log.Get(waitsFor) is { } other && other.Status < QuestLog.Rewarded)
                    {
                        questBlocked[record.Code] = now + 60_000;
                        return null;
                    }
                    Report(record.Code, "ClearQuest: the quest it waits for is not in the quest log", o.Target);
                    break;
                case "PCLevel":
                    return null;   // levels come from hunting
                default:
                    Report(record.Code, "objective type bots do not do yet: " + o.Type);
                    break;
            }
            questBlocked[record.Code] = now + QuestBlockMs;
            return null;
        }
        return null;
    }

    readonly HashSet<(int Map, int Index)> usedTried = [];
    readonly Dictionary<string, int> actTries = [];

    /// <summary>Walk to the NPC (on this map, else the move button) and act with its key.</summary>
    async Task<QuestGoal?> NpcGoalAsync(string quest, string what, int npcId, int map, Vector3d at, Func<int, Task> act)
    {
        var npc = (await peekNpcs(1_000_000)).Where(n => n.NpcId == npcId).OrderBy(n => Flat(at, n.At)).FirstOrDefault();
        if (npc.Key == 0 || Flat(at, npc.At) > 30_000) return new QuestGoal(quest, what + " (far)", Teleport: true);
        return new QuestGoal(quest, what, Place: npc.At, Near: 300, Act: () => act(npc.Key));
    }

    Vector3d? NearestSpawn(int map, Vector3d at, int[] npcIds)
    {
        Vector3d? best = null; var bestD = double.MaxValue;
        foreach (var s in GameData.Embedded.Spawns(map))
            if (npcIds.Contains(s.NpcId) && s.Position.Length >= 3 && new Vector3d(s.Position[0], s.Position[1], s.Position[2]) is var p && Flat(at, p) is var d && d < bestD)
            { best = p; bestD = d; }
        return best;
    }

    // ---------------- map events: follow the instance script step by step, as the quest tracker shows it ----------------

    (string Quest, long At)? pendingReward;
    string? lastGoalWhat;
    long eventMissingSince, eventTeleportAt;
    string? eventMissingName;
    (string Key, long Since)? eventProgress;
    public const int EventStuckMs = 5 * 60_000;

    /// <summary>The map event the bot is in (name, stage, step, goals, open objectives): reports quote it.</summary>
    object? EventState()
    {
        var run = session.BotMapEventRuns.FirstOrDefault(r => !r.Finished);
        if (run is null) return null;
        return new
        {
            name = run.Name, id = run.Id, stage = run.StageOrder, step = run.StepOrder, stepId = run.StepId, goals = run.Goals,
            open = run.Open.Select(x => $"{x.Index}:{x.Objective.Type}{(x.Objective.Optional ? "?" : "")} {string.Join(",", x.Objective.Values)}").ToArray(),
        };
    }

    /// <summary>
    /// ClearMapEvent: the quest is the instance's script (map-events.json). Inside, the bot does the current step's objectives in
    /// order: kills the named spawners, uses the wanted objects, walks into the trigger areas, talks to the named NPCs, finishes the
    /// client-side ones (notes, cutscenes, talks) and waits for escorts/timed steps while defending itself. Outside, the quest's
    /// move button takes it in. A step that never advances is reported with the event's state.
    /// </summary>
    async Task<QuestGoal?> EventGoalAsync(string quest, string eventName, int map, Vector3d at)
    {
        var run = session.BotMapEventRuns.FirstOrDefault(r => string.Equals(r.Name, eventName, StringComparison.OrdinalIgnoreCase));
        if (run is null || run.Finished)
        {
            if (run is { Finished: true }) return new QuestGoal(quest, "event cleared, waiting for the quest", KillIds: [], Hold: true);
            // Only an event that is not running yet needs the table (its map); a running one carries its own script.
            if (MapEventTables.Embedded.Event(eventName) is not { } def) { Report(quest, "map event not in the data", eventName); return null; }
            if (eventMissingName != eventName) { eventMissingName = eventName; eventMissingSince = now; }
            // Not inside (or the script did not start): the quest's move button, at most every 45 s.
            if (now - eventTeleportAt > 45_000)
            {
                eventTeleportAt = now;
                return new QuestGoal(quest, "enter " + eventName, Teleport: true);
            }
            if (now - eventMissingSince > 120_000)
            {
                Report(quest, session.MapId == def.Map ? "map event did not start on its map" : "quest move button does not lead into the map event",
                    new { mapEvent = eventName, eventMap = def.Map, map, inInstance = session.InInstance });
                eventMissingName = null;
                return null;
            }
            return new QuestGoal(quest, "waiting for " + eventName, KillIds: [], Hold: true);
        }
        eventMissingName = null;
        // A step that does not move on.
        var key = $"{run.Id}:{run.StageIndex}:{run.StepIndex}:{run.Goals.Sum()}";
        if (eventProgress is not { } ep || ep.Key != key) { if (eventProgress is not null) StepsDone++; eventProgress = (key, now); }
        else if (now - ep.Since > EventStuckMs)
        {
            Report(quest, "map event step does not advance", new { mapEvent = eventName, lastGoal = lastGoalWhat });
            eventProgress = null;
            return null;
        }
        var open = run.Open.ToList();
        // The objectives the step needs (the optional ones finish by themselves or move the script along: entering areas helps).
        var pick = open.Where(x => !x.Objective.Optional).Select(x => (x.Index, x.Objective, Order: Rank(x.Objective.Type))).OrderBy(x => x.Order).FirstOrDefault();
        if (pick.Objective is null)
            pick = open.Where(x => x.Objective.Type is "EnterVolumePC" or "UseEnvObj" or "AskNpc").Select(x => (x.Index, x.Objective, Order: 0)).FirstOrDefault();
        if (pick.Objective is not { } o) return new QuestGoal(quest, "waiting for the next step of " + eventName, AnyHostile: true, Hold: true);
        var index = pick.Index;
        var what = $"{eventName} {o.Type} {o.Value(0)}";
        switch (o.Type)
        {
            case "KillNpc" or "KillSpawner" or "HpRemaining":
            {
                var npcs = await peekNpcs(ScanRadius * 3);
                var living = npcs.Where(n => MapEventRun.Targets(o, n.NpcId, n.Name)).OrderBy(n => Flat(at, n.At)).ToList();
                if (living.Count == 0) return new QuestGoal(quest, what + " (not spawned yet)", AnyHostile: true, Hold: true);
                if (!living.Any(n => n.Hostile))
                {
                    // Named but not attackable (a friendly NPC that turns later): stay near it.
                    return new QuestGoal(quest, what + " (not attackable yet)", Place: living[0].At, Near: 600, AnyHostile: true, Hold: true);
                }
                return new QuestGoal(quest, what, KillIds: o.Npcs ?? [], KillNames: o.Names, Place: living.First(n => n.Hostile).At, Near: 1200);
            }
            case "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" or "PCCombatStart" or "ClearWave":
                return new QuestGoal(quest, what, AnyHostile: true, Hold: true);
            case "UseEnvObj":
            {
                var wanted = session.BotWantedObjects(run).OrderBy(x => Flat(at, x.At)).ToList();
                if (wanted.Count == 0) return new QuestGoal(quest, what + " (object not there)", AnyHostile: true, Hold: true);
                var obj = wanted[0];
                return new QuestGoal(quest, what, Place: obj.At, Near: Math.Max(150, obj.Reach - 60), Act: async () =>
                {
                    await send(new PacketWriter().U16(0x3636).I32(obj.Key).ToArray());
                    questActAt = now + 3500;   // the interaction time
                });
            }
            case "EnterVolumePC":
            {
                var areas = session.BotTriggers(o.Value(0));
                if (areas.Count == 0) { Report(quest, "map event trigger area not on this map", new { mapEvent = eventName, trigger = o.Value(0), map }); return null; }
                var area = areas.OrderBy(x => Flat(at, x.At)).First();
                return new QuestGoal(quest, what, Place: area.At, Near: 120, Act: () => send(new PacketWriter().U16(0x3636).I32(area.Key).ToArray()));
            }
            case "EnterSubZone":
                if (Subzones.Locate(o.Value(0)) is { } zone && zone.Map == map) return new QuestGoal(quest, what, Place: zone.Center, Near: 200, Hold: true, KillIds: []);
                Report(quest, "map event subzone not on this map", new { mapEvent = eventName, subzone = o.Value(0), map });
                return null;
            case "AskNpc" or "CloseNpc":
            {
                var npcs = await peekNpcs(ScanRadius * 4);
                var npc = npcs.Where(n => MapEventRun.Targets(o, n.NpcId, n.Name)).OrderBy(n => Flat(at, n.At)).FirstOrDefault();
                if (npc.Key == 0) return new QuestGoal(quest, what + " (NPC not there)", AnyHostile: true, Hold: true);
                var runId = run.Id;
                return new QuestGoal(quest, what, Place: npc.At, Near: 300, Act: async () =>
                {
                    await send(new PacketWriter().U16(0xe240).I32(npc.Key).ToArray());
                    if (o.Type == "AskNpc") await send(new PacketWriter().U16(0x8d2b).U8(2).I32(runId).I32(npc.Key).I32(index).ToArray());
                    await send(new PacketWriter().U16(0xe242).I32(npc.Key).ToArray());
                });
            }
            case "CompleteNpcTalk" or "CloseNote" or "CompleteCutscene" or "CloseGuide":
            {
                var runId = run.Id;
                // A player reads/watches it for a moment first.
                return new QuestGoal(quest, what, Act: () => send(new PacketWriter().U16(0x9230).U8(2).I32(runId).I32(index).ToArray()));
            }
            default:
                // NpcArriveWaypoint (an escort walks), UnableComplete (a timer), scores, puzzles: stay with the event NPC and defend.
                var named = (await peekNpcs(ScanRadius * 2)).Where(n => !n.Hostile && MapEventRun.Targets(o, n.NpcId, n.Name)).OrderBy(n => Flat(at, n.At)).FirstOrDefault();
                return named.Key != 0
                    ? new QuestGoal(quest, what, Place: named.At, Near: 500, AnyHostile: true, Hold: true)
                    : new QuestGoal(quest, what, AnyHostile: true, Hold: true);
        }

        static int Rank(string type) => type switch
        {
            "CompleteNpcTalk" or "CloseNote" or "CompleteCutscene" or "CloseGuide" => 0,   // the client finishes these first (dialogs on screen)
            "AskNpc" or "CloseNpc" => 1,
            "KillNpc" or "KillSpawner" or "HpRemaining" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" or "PCCombatStart" or "ClearWave" => 2,
            "UseEnvObj" => 3,
            "EnterVolumePC" or "EnterSubZone" => 4,
            _ => 5,
        };
    }

    // ---------------- unstuck: detect, go round, report ----------------

    int stuckTries;
    string? stuckGoal;

    /// <summary>
    /// Walking but not getting anywhere (3 s without 1.2 m of progress): first a new route, then a side step to go round what is in
    /// the way, then a jump back a few metres; after four tries the goal is given up and reported (where, where to, why).
    /// </summary>
    async Task UnstuckAsync(int map, Vector3d at, QuestGoal goal)
    {
        if (!Stuck(at)) { if (!moving && stuckTries > 0 && stuckGoal != goal.What) stuckTries = 0; return; }
        if (stuckGoal != goal.What) { stuckGoal = goal.What; stuckTries = 0; }
        stuckTries++;
        Unstucks++;
        await StopAsync();
        path = null;
        if (stuckTries >= 4)
        {
            Report(goal.Quest, "stuck on the way", new { goal.What, to = goal.Place is { } p ? new[] { Math.Round(p.X), Math.Round(p.Y), Math.Round(p.Z) } : null });
            questBlocked[goal.Quest] = now + QuestBlockMs / 2; questGoal = null; stuckTries = 0;
            return;
        }
        var angle = rng.NextDouble() * Math.PI * 2;
        var side = new Vector3d(at.X + Math.Cos(angle) * (300 + 200 * stuckTries), at.Y + Math.Sin(angle) * (300 + 200 * stuckTries), at.Z);
        await GoAsync(map, at, side, RunSpeed);
    }

    // ---------------- side activities: gathering, a town idler's day ----------------

    long nextGatherAt;
    readonly HashSet<int> gatherTried = [];

    /// <summary>A gather node on the way (its mastery allows it): walk over and collect, as players pick herbs and ore between fights.</summary>
    async Task<bool> GatherAsync(int map, Vector3d at)
    {
        if (now < nextGatherAt) return false;
        nextGatherAt = now + 20_000 + rng.Next(20_000);
        var node = session.BotGatherNodes(2500).Where(n => !gatherTried.Contains(n.Key)).OrderBy(n => Flat(at, n.At)).FirstOrDefault();
        if (node.Key == 0) return false;
        gatherTried.Add(node.Key);
        if (gatherTried.Count > 200) gatherTried.Clear();
        questGoal = new QuestGoal("", "gather", Place: node.At, Near: Math.Max(150, node.Reach - 60), Act: async () =>
        {
            await send(new PacketWriter().U16(0x3636).I32(node.Key).ToArray());
            Gathers++;
            questGoal = null;
        });
        nextQuestLookAt = now + 30_000;
        return true;
    }

    Vector3d? home;

    /// <summary>A town idler: short walks around the place it logged in, long pauses, a shop visit now and then.</summary>
    async Task IdleAsync(int map, Vector3d at)
    {
        home ??= at;
        if (moving) { await StepAsync(map, at); if (Stuck(at)) await StopAsync(); return; }
        if (rng.NextDouble() < 0.6) { SetMode(Mode.Pause); pauseUntil = now + rng.Next(10_000, 60_000); return; }
        var angle = rng.NextDouble() * Math.PI * 2;
        var spot = new Vector3d(home.Value.X + Math.Cos(angle) * rng.Next(200, 1500), home.Value.Y + Math.Sin(angle) * rng.Next(200, 1500), home.Value.Z);
        if (!await GoAsync(map, at, spot, WalkSpeed)) { SetMode(Mode.Pause); pauseUntil = now + 10_000; }
    }

    // ---------------- the server's view: refusals and errors are findings ----------------

    int seenErrors;
    readonly Dictionary<string, int> seenSuspicions = [];

    /// <summary>A request that failed on the server, or an anti-cheat suspicion against a bot that only sends client requests: report it.</summary>
    void WatchServer()
    {
        var errors = session.RequestErrors;
        while (seenErrors < errors.Count) { var error = errors[seenErrors++]; Report(questGoal?.Quest ?? "", "server error on " + error.Split(' ')[0], error); }
        foreach (var (kind, count) in session.Suspicions)
            if (count > seenSuspicions.GetValueOrDefault(kind)) { seenSuspicions[kind] = count; Report(questGoal?.Quest ?? "", "anti-cheat flagged a bot request: " + kind, new { count, goal = questGoal?.What, mode = mode.ToString() }); }
    }

    // ---------------- loot, flee, explore ----------------

    async Task<bool> LootAsync(int map, Vector3d at)
    {
        var drops = session.BotDrops.Where(d => Flat(at, d.At) < 2000 && lootTries.GetValueOrDefault(d.Uid) < 3).OrderBy(d => Flat(at, d.At)).ToList();
        if (mode == Mode.Loot && lootCountedFor != modeSince) { lootCountedFor = modeSince; LootLooks++; if (session.BotDrops.Any()) LootSeen++; }
        if (drops.Count == 0) { SetMode(Mode.Explore); return false; }
        if (now - modeSince > 20_000) { foreach (var d in drops) lootTries[d.Uid] = 3; SetMode(Mode.Explore); return false; }
        var first = drops[0];
        if (Flat(at, first.At) > LootReach)
        {
            if (path is null || !moving) await GoAsync(map, at, first.At, WalkSpeed + 120);
            else await StepAsync(map, at);
            return true;
        }
        if (moving) await StopAsync();
        var here = drops.Where(d => Flat(at, d.At) < 800).ToList();
        foreach (var d in here) lootTries[d.Uid] = lootTries.GetValueOrDefault(d.Uid) + 1;
        Loots++;
        var w = new PacketWriter().U16(LootPackets.LootRq).Varint((uint)here.Count);
        foreach (var d in here) w.U64((ulong)d.Uid);
        await send(w.U8(0).ToArray());
        if (lootTries.Count > 500) lootTries.Clear();
        return true;
    }

    async Task<bool> FleeAsync(int map, Vector3d at, List<GameSession.BotSight> attackers)
    {
        double cx = attackers.Average(a => a.Position.X), cy = attackers.Average(a => a.Position.Y);
        double dx = at.X - cx, dy = at.Y - cy, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        for (var tries = 0; tries < 5; tries++)
        {
            var turn = (rng.NextDouble() - 0.5) * (0.6 + tries * 0.5);
            double ux = dx / len * Math.Cos(turn) - dy / len * Math.Sin(turn), uy = dx / len * Math.Sin(turn) + dy / len * Math.Cos(turn);
            var goal = new Vector3d(at.X + ux * 2800, at.Y + uy * 2800, at.Z);
            if (await GoAsync(map, at, goal, RunSpeed)) { SetMode(Mode.Flee); targetKey = 0; return true; }
        }
        return false;
    }

    async Task ExploreAsync(int map, Vector3d at)
    {
        SetMode(Mode.Explore);
        if (moving)
        {
            await StepAsync(map, at);
            if (Stuck(at)) { await StopAsync(); path = null; }
            return;
        }
        // Nothing of its level here: farther each time (a new area), away from crowds.
        failedExplores++;
        var far = failedExplores > 3;
        double ax = 0, ay = 0;
        if (players.Count >= 3) { ax = at.X - players.Average(p => p.X); ay = at.Y - players.Average(p => p.Y); }
        for (var tries = 0; tries < 8; tries++)
        {
            var angle = ax != 0 || ay != 0 ? Math.Atan2(ay, ax) + (rng.NextDouble() - 0.5) * 1.6 : rng.NextDouble() * Math.PI * 2;
            var distance = far ? 5000 + rng.NextDouble() * 9000 : 1800 + rng.NextDouble() * 3500;
            var goal = new Vector3d(at.X + Math.Cos(angle) * distance, at.Y + Math.Sin(angle) * distance, at.Z);
            if (await GoAsync(map, at, goal, rng.Next(4) == 0 ? WalkSpeed : RunSpeed)) { if (far) failedExplores = 0; return; }
        }
        SetMode(Mode.Pause); pauseUntil = now + 5000;
    }

    // ---------------- movement on the navmesh ----------------

    NavMesh? mesh;
    int meshMap = -1;

    async Task<NavMesh?> MeshAsync(int map)
    {
        if (navStore is null) return null;
        if (meshMap != map) { mesh = await navStore.LoadAsync(map); meshMap = map; }
        return mesh;
    }

    /// <summary>Starts a walk to <paramref name="goal"/> along the floor (as far as it reaches). False when there is no way.</summary>
    async Task<bool> GoAsync(int map, Vector3d at, Vector3d goal, double moveSpeed)
    {
        var nav = await MeshAsync(map);
        List<Vector3d>? route;
        if (nav is null) route = [goal];
        else
        {
            if (nav.FloorAt(goal.X, goal.Y, goal.Z, 600) is { } gz) goal = goal with { Z = gz };
            // Standing off the mesh (after a teleport, on a ledge, mid-jump): route from the floor under it.
            var from = at;
            if (nav.Locate(at.X, at.Y, at.Z) is null && nav.FloorAt(at.X, at.Y, at.Z, 2500) is { } fz) from = at with { Z = fz };
            route = nav.FindPathPartial(from, goal, 8192);
            // A partial route that ends short of the goal: the last few metres straight (a spot on an object, a gap in the mesh).
            if (route is { Count: > 0 } && Flat(route[^1], goal) is var rest && rest > 250 && rest < 2500) route.Add(goal);
            if (route is null || route.Count == 0 || Flat(route[^1], at) < 60)
            {
                // No route on the floor mesh (a spot inside an object, a gap in the mesh): a short distance is walked straight, as a player does.
                if (Flat(at, goal) > 4000) return false;
                route = [goal];
            }
        }
        path = route;
        pathIndex = 0;
        speed = moveSpeed;
        heading = Heading(at, path[0]);
        progressAt = now; progressFrom = at;
        if (!moving) { moving = true; await send(Move(0x3700, at, heading, now)); }
        return true;
    }

    async Task StepAsync(int map, Vector3d at)
    {
        if (path is null || !moving) return;
        var budget = speed * Math.Clamp((now - lastStep) / 1000.0, 0, 0.5);
        var pos = at;
        while (budget > 0 && pathIndex < path.Count)
        {
            var next = path[pathIndex];
            var d = Flat(pos, next);
            if (d <= budget) { pos = next; budget -= d; pathIndex++; continue; }
            var f = budget / d;
            pos = new Vector3d(pos.X + (next.X - pos.X) * f, pos.Y + (next.Y - pos.Y) * f, pos.Z + (next.Z - pos.Z) * f);
            budget = 0;
        }
        if (await MeshAsync(map) is { } nav && nav.FloorAt(pos.X, pos.Y, pos.Z, 300) is { } z) pos = pos with { Z = z };
        if (pathIndex < path.Count) heading = Heading(pos, path[pathIndex]);
        if (pathIndex >= path.Count) { await send(Stop(pos, heading, now)); moving = false; path = null; }
        else await send(Move(0x3701, pos, heading, now));
    }

    async Task StopAsync()
    {
        if (!moving) return;
        moving = false;
        path = null;
        if (session.BotPosition is { } at) await send(Stop(at, heading, now));
    }

    /// <summary>No progress for 3 s while walking.</summary>
    bool Stuck(Vector3d at)
    {
        if (!moving) return false;
        if (Flat(at, progressFrom) > 120) { progressFrom = at; progressAt = now; return false; }
        return now - progressAt > 3000;
    }

    // ---------------- helpers and the client's requests ----------------

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Heading(Vector3d from, Vector3d to) => (Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI + 360) % 360;

    /// <summary>The point <paramref name="distance"/> from <paramref name="from"/> towards <paramref name="toward"/> (negative: away).</summary>
    static Vector3d Toward(Vector3d from, Vector3d toward, double distance)
    {
        double dx = toward.X - from.X, dy = toward.Y - from.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        return new Vector3d(from.X + dx / len * distance, from.Y + dy / len * distance, from.Z);
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>CharMoveStart_RQ 0x3700 / CharMoveUpdate_RQ 0x3701: mask, position, move direction (u16 of 360), facing, dead reckoning, client time.</summary>
    static byte[] Move(ushort op, Vector3d at, double heading, long now) =>
        new PacketWriter().U16(op).U8(2).Vec3(at).U16((ushort)(heading * 65536 / 360 % 65536)).F32((float)heading).U8(1).I64(now).ToArray();

    /// <summary>CharMoveStop_RQ 0x3718: two flag bytes, position, facing, dead reckoning, client time.</summary>
    static byte[] Stop(Vector3d at, double heading, long now) =>
        new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(at).F32((float)heading).U8(1).I64(now).ToArray();

    /// <summary>0x3800 cast: no optional fields, the skill, byte 2, the target, the facing and the caster's position.</summary>
    static byte[] Cast(int skill, int target, double yaw, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint((uint)target).F32((float)yaw).F32((float)at.X).F32((float)at.Y).F32((float)at.Z).ToArray();
}
