using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>One builder command's reply: result 0 is success; the packets go to the actor.</summary>
public sealed record BuilderOutcome(int Result, string Message, List<byte[]> Packets)
{
    public static BuilderOutcome Ok(string message, List<byte[]>? packets = null) => new(0, message, packets ?? []);
    public static BuilderOutcome Fail(string message) => new(1, message, []);
}

/// <summary>
/// The client's builder (GM) commands. A GM types "s &lt;command&gt;" in the client console,
/// which sends 0x362e BuilderCmd_RQ; "//&lt;command&gt;" in chat reaches the same code. Names
/// and arguments follow the client's Content/Data/BuilderCommand.json (656 commands, see
/// research/builder-commands.md); only <see cref="Supported"/> is backed here. Runs on the
/// actor's game loop. GM rights come from <see cref="GmPolicy"/>; every command is audited
/// (name and outcome only).
/// </summary>
public sealed class BuilderCommands(WorldPresence presence, GmPolicy policy, Action<string, string, string> audit, GameData? data = null)
{
    public static readonly IReadOnlyList<string> Supported =
        ["SetInvisible", "SetInvincible", "SetInvincibleHP", "NoCool", "DashVariant", "Hp", "Mp", "Sp", "FixHpPercent",
         "Teleport", "TeleportTo", "MoveMap", "LayerTest", "Dev", "AcquireItem", "SetLevel", "ClassChange", "SetAscension", "ContentsUnlock", "ContentsUnlockAll", "AcquireWingAll",
         "QuestAdd", "QuestComplete", "QuestCompleteStep", "QuestSetStep", "QuestReward", "QuestDelete", "RevealMap", "AcquireTitle", "AcquireSkin", "SetMastery", "Mail", "Gift", "EnterDungeon",
         "GrantShopCurrency", "Membership", "BattlePassExp"];

    /// <summary>usmap EContentsUnlockType, 1..64 (0 None, 65 Max).</summary>
    public static readonly IReadOnlyList<string> UnlockTypes =
        ["None", "Journal", "Skill", "BMShop", "PartyDungeon", "DailyDungeon", "Arcana", "Arena", "Achievement", "Guild",
         "Craft", "Gather", "Wing", "Conversion", "Title", "Skin", "Ride", "Post", "DreamCrossRoad", "Enchant",
         "Daevanion", "Abyss", "PeriodCollection", "Emblem", "BossChallenge", "Collection", "Ranking", "Trade", "SeasonContentsInfo", "SeasonMission",
         "BattlePass", "PartyDungeonChallenge", "StatInfo", "ContentsNavigator", "WorldLevelScale", "AutoBattle", "ShugoFesta", "Suppression", "DailyDungeonLobby", "GrowthDungeon",
         "Raid", "AwakenDungeon", "Community", "Customizing", "Attendance", "Chat", "Snapshot", "PartyDungeon1Tier", "PartyDungeon2Tier", "PartyDungeon3Tier",
         "Party_Force", "Pantheon", "Map", "ItemDelete", "ItemDecompose", "Coupon", "RemoteStorage", "PartyDungeon4Tier", "PartyDungeon5Tier", "PartyDungeon6Tier",
         "Preset", "HudEdit", "DamageAnalyzer", "Onboarding", "ChangeCharacter"];

    static readonly Dictionary<string, string> Usage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SetInvisible"] = "SetInvisible [true|false]",
        ["Mail"] = "Mail <character> <item id|0> <count> <text...> (the text is the mail's title and body)",
        ["Gift"] = "Gift <item> <count> <days> <sender>|<title>|<body> (a push reward every character can take once)",
        ["SetInvincible"] = "SetInvincible [true|false] or HP:true MP:true SP:true",
        ["SetInvincibleHP"] = "SetInvincibleHP [true|false]",
        ["NoCool"] = "NoCool [true|false]",
        ["DashVariant"] = "DashVariant <0-10> (the sprint dash's reply shape, for live tests)",
        ["Hp"] = "Hp [value]", ["Mp"] = "Mp [value]", ["Sp"] = "Sp [value]",
        ["FixHpPercent"] = "FixHpPercent [percent]",
        ["Teleport"] = "Teleport [MapID] [X] [Y] (Z), or Teleport [destination name]",
        ["MoveMap"] = "MoveMap [MapID]",
        ["EnterDungeon"] = "EnterDungeon <dungeon id> (Dungeon.dat; a party shares the copy)",
        ["LayerTest"] = "LayerTest <mask> - Robin's layer with only these pieces of the seamless move: 1 self disappear, 2 move reason/effect, 4 short load, 8 environment, 16 appearance bytes (31 all, 0 none)",
        ["Dev"] = "Dev [dev menu button], or Dev [text] to list buttons",
        ["TeleportTo"] = "TeleportTo [nickname]",
        ["AcquireItem"] = "AcquireItem [item id or name] (count)",
        ["SetLevel"] = "SetLevel [value]",
        ["ClassChange"] = "ClassChange [class, e.g. Gladiator]",
        ["SetAscension"] = "SetAscension [grade 1..6, e.g. 3 or AscensionGrade_3]",
        ["ContentsUnlock"] = "ContentsUnlock [type name or number, e.g. Wing]",
        ["ContentsUnlockAll"] = "ContentsUnlockAll",
        ["AcquireWingAll"] = "AcquireWingAll",
        ["QuestAdd"] = "QuestAdd [quest code or id]", ["QuestComplete"] = "QuestComplete [quest code or id]",
        ["QuestCompleteStep"] = "QuestCompleteStep [quest code or id]", ["QuestSetStep"] = "QuestSetStep [quest code or id] [step order]",
        ["QuestReward"] = "QuestReward [quest code or id]",
        ["QuestDelete"] = "QuestDelete [quest code or id]",
        ["RevealMap"] = "RevealMap (uncovers every world-map area for this character)",
        ["GrantShopCurrency"] = "GrantShopCurrency kina|quna|<currency item id> [count] (dev BM shop wallet)",
        ["Membership"] = "Membership on|off (the account's Special Quai Membership until the host restarts)",
        ["BattlePassExp"] = "BattlePassExp <exp> (Daeva Pass exp to every active pass)",
    };

    static string UnknownMap(int mapId) => $"Map {mapId} is not one the client knows (not in Map.dat); loading it would log you out.";

    /// <summary>Whether the actor may use builder commands (a staff-flagged character of an account still holding a role, or a development name).</summary>
    public async Task<bool> AllowedAsync(GameSession actor, CancellationToken ct)
    {
        if (actor.Store is { } store)
        {
            if (!actor.IsAdmin) return false;
            try { return await policy.StaffRole(store.AccountId, ct) is not null; }
            catch (Exception error) when (error is not OperationCanceledException) { return false; }
        }
        return actor.CharacterName is { } name && policy.DevelopmentNames.Contains(name);
    }

    public async Task<BuilderOutcome> RunAsync(GameSession actor, string line, long now, CancellationToken ct)
    {
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = words.Length == 0 ? "" : words[0];
        var command = Supported.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        var label = command ?? "unsupported";
        var who = actor.Store?.AccountId.ToString() ?? "dev:" + actor.CharacterName;
        if (!actor.InWorld) return BuilderOutcome.Fail("Enter the world first.");
        if (!await AllowedAsync(actor, ct))
        {
            audit(who, label, "denied");
            return BuilderOutcome.Fail("GM permission required.");
        }
        audit(who, label, "requested");  // before any change
        BuilderOutcome outcome;
        try
        {
            outcome = command is null
                ? BuilderOutcome.Fail(name.Length == 0 ? "Empty command." : $"{name}: not available on this server.")
                : await ExecuteAsync(actor, command, words[1..], now, ct) ?? BuilderOutcome.Fail("Usage: " + Usage[command]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcome = BuilderOutcome.Fail($"{command} failed.");
            label += ":" + error.GetType().Name;
        }
        audit(who, label, outcome.Result == 0 ? "applied" : "rejected");
        return outcome;
    }

    /// <summary>Null means the arguments do not match the usage.</summary>
    async Task<BuilderOutcome?> ExecuteAsync(GameSession actor, string command, string[] args, long now, CancellationToken ct)
    {
        switch (command)
        {
            case "SetInvisible" when args is [var value] && Flag(value) is { } on:
                return BuilderOutcome.Ok(on ? "Invisible." : "Visible.", actor.SetInvisible(on));
            case "SetInvincible" when Invincibility(args) is { } flags:
            {
                var packets = actor.SetInvincible(flags.Hp, flags.Mp, flags.Sp);
                var (hp, mp, sp) = actor.Invincible;
                return BuilderOutcome.Ok($"Invincible: HP {OnOff(hp)}, MP {OnOff(mp)}, SP {OnOff(sp)}.", packets);
            }
            case "SetInvincibleHP" when args is [var value] && Flag(value) is { } on:
                return BuilderOutcome.Ok($"Invincible HP {OnOff(on)}.", actor.SetInvincible(on, null, null));
            case "NoCool" when args is [var value] && Flag(value) is { } on:
                actor.NoCool = on;
                return BuilderOutcome.Ok(on ? "Cooldowns off." : "Cooldowns on.");
            case "DashVariant" when args is [var value] && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var variant)
                                    && variant >= 0 && variant <= GameSession.DashVariantMax:
                actor.DashVariant = variant;
                return BuilderOutcome.Ok($"Dash variant {variant}.");
            case "Hp" or "Mp" or "Sp" when args is [var value] && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) && amount >= 0:
                var updated = actor.SetResource(command[0], amount);
                return BuilderOutcome.Ok($"{command.ToUpperInvariant()} set.", updated);
            case "FixHpPercent" when args is [var value] && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and <= 100:
                return BuilderOutcome.Ok($"HP set to {percent}%.", actor.SetResource('H', (long)actor.Resource.HpMax * percent / 100));
            case "Teleport" when args.Length is 3 or 4 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId) && mapId > 0
                                 && Coordinates(args[1..]) is { } xyz:
            {
                // Without Z: keep the current height on the same map; the client places the character.
                if (data is not null && !data.KnownMap(mapId)) return BuilderOutcome.Fail(UnknownMap(mapId));
                var z = xyz.Length == 3 ? xyz[2] : mapId == actor.MapId ? actor.Position?.Z ?? 0 : 0;
                return BuilderOutcome.Ok($"Teleporting to map {mapId}.", actor.Teleport(mapId, new Vector3d(xyz[0], xyz[1], z), now));
            }
            case "Teleport" when args is [var place] && data is not null:
            {
                var (found, matches) = Destination(place);
                if (found is not { } d)
                    return BuilderOutcome.Fail(matches.Count == 0 ? $"No destination named {place}."
                        : $"{matches.Count} destinations match: " + string.Join(", ", matches.Take(8)) + (matches.Count > 8 ? ", ..." : ""));
                if (!data.KnownMap(d.MapId)) return BuilderOutcome.Fail(UnknownMap(d.MapId));
                return BuilderOutcome.Ok($"Teleporting to {d.Name} (map {d.MapId}).", actor.Teleport(d.MapId, d.Position, now));
            }
            case "MoveMap" when args is [var value] && data is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moveTo):
                if (!data.KnownMap(moveTo)) return BuilderOutcome.Fail(UnknownMap(moveTo));
                return data.MapStart(moveTo) is { } start
                    ? BuilderOutcome.Ok($"Moving to map {moveTo} ({start.Name}).", actor.Teleport(moveTo, start.Position, now))
                    : BuilderOutcome.Fail($"Map {moveTo} has no start point in its MapData.");
            case "EnterDungeon" when args is [var dungeonText] && int.TryParse(dungeonText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dungeonId):
            {
                var moved = actor.EnterDungeonById(dungeonId, now);
                return moved.Count == 0 ? BuilderOutcome.Fail($"EnterDungeon: {dungeonId} is unknown, unserved, or you are inside one already.") : BuilderOutcome.Ok($"Entering dungeon {dungeonId}.", moved);
            }
            case "LayerTest" when args is [var maskText] && int.TryParse(maskText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask) && mask is >= 0 and <= 31:
            {
                var moved = actor.LayerTest(mask, now);
                return moved.Count == 0 ? BuilderOutcome.Fail("LayerTest: leave the layer first (relog), then try again from Poeta.") : BuilderOutcome.Ok($"Layer move with pieces {mask}.", moved);
            }
            case "Dev" when args.Length > 0 && data is not null:
                return await DevAsync(actor, string.Join(' ', args), now, ct);
            case "TeleportTo" when args.Length is 1 or 2:
            {
                var target = presence.Find(args[0]);
                if (target is null || target.Session == actor) return BuilderOutcome.Fail($"{args[0]} is not in the world here.");
                if (target.Session.MapId is not { } map || target.Session.Position is not { } at) return BuilderOutcome.Fail($"{args[0]} is still loading.");
                return BuilderOutcome.Ok($"Teleporting to {args[0]}.", actor.Teleport(map, at, now));
            }
            // 0xe258 ContentsUnlockUpdate: the packet that unlocks chat (45) at world entry.
            case "ContentsUnlock" when args is [var value] && UnlockType(value) is { } type:
                return BuilderOutcome.Ok($"Unlocked {UnlockTypes[type]}.", [GamePackets.ContentsUnlockUpdate([(byte)type])]);
            case "QuestSetStep" when args is [var quest, var stepText] && int.TryParse(stepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stepOrder) && stepOrder > 0:
            {
                var (message, packets) = await actor.QuestCommandAsync("QuestSetStep", quest, ct, stepOrder);
                return message.Contains("not in journal", StringComparison.Ordinal) ? BuilderOutcome.Fail(message) : BuilderOutcome.Ok(message, packets);
            }
            case "QuestAdd" or "QuestComplete" or "QuestCompleteStep" or "QuestReward" or "QuestDelete" when args is [var quest]:
            {
                var (message, packets) = await actor.QuestCommandAsync(command, quest, ct);
                return message.Contains("not in journal", StringComparison.Ordinal) || message.StartsWith("No quest", StringComparison.Ordinal)
                    ? BuilderOutcome.Fail(message) : BuilderOutcome.Ok(message, packets);
            }
            case "RevealMap" when args.Length == 0:
                return BuilderOutcome.Ok($"Map revealed ({Subzones.AllGroups.Count} areas).", await actor.RevealMapAsync(ct));
            case "Mail" when args.Length >= 4 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mailItem)
                && long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mailCount) && mailItem >= 0 && mailCount >= 0:
            {
                var text = string.Join(' ', args.Skip(3));
                var attachments = mailItem > 0 && mailCount > 0 ? new List<(int, long)> { (mailItem, mailCount) } : [];
                var sent = await actor.SendMailAsync(args[0], text, text, attachments, ct);
                return sent ? BuilderOutcome.Ok($"Mail sent to {args[0]}.", []) : BuilderOutcome.Fail($"Mail: no character named {args[0]}.");
            }
            case "Gift" when args.Length >= 4 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var giftItem) && giftItem > 0
                && long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var giftCount) && giftCount > 0
                && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var giftDays) && giftDays is > 0 and <= 365:
            {
                var meta = string.Join(' ', args.Skip(3));
                var parts = meta.Split('|', 3);
                if (parts.Length < 3) return BuilderOutcome.Fail("Usage: " + Usage["Gift"]);
                var (sender, title, body) = (parts[0], parts[1], parts[2]);
                if (actor.Store is not { }) return BuilderOutcome.Fail("Gift requires a stored account.");
                var expireMs = now + giftDays * 86_400_000L;
                var added = await actor.AddPushRewardAsync(1, sender, title, body, giftItem, giftCount, expireMs, ct);
                if (added is null) return BuilderOutcome.Fail("Gift could not be saved.");
                actor.NotifyPushRewardChanged();
                return BuilderOutcome.Ok($"Push reward {added.Id} added ({giftDays} days).", [GameSession.PushRewardChangedPacket()]);
            }
            case "AcquireTitle" when args is [var titleText] && int.TryParse(titleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var titleId):
            {
                var acquired = await actor.AcquireTitleAsync(titleId, ct);
                return acquired.Count == 0 ? BuilderOutcome.Fail($"AcquireTitle: {titleId} is unknown, of the other race, or owned already.")
                    : BuilderOutcome.Ok($"Title {titleId} acquired.", acquired);
            }
            case "AcquireSkin" when args is [var skinText] && int.TryParse(skinText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var skinId):
            {
                var acquired = await actor.AcquireSkinsAsync([skinId], ct);
                return acquired.Count == 0 ? BuilderOutcome.Fail($"AcquireSkin: {skinId} is not a Skin.dat id.") : BuilderOutcome.Ok($"Skin {skinId} added.", acquired);
            }
            case "SetMastery" when args is [var kind, var categoryText, var levelText] && (kind is "craft" or "gather")
                && int.TryParse(categoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var category)
                && int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var masteryLevel):
            {
                var set = await actor.SetMasteryAsync(kind, category, masteryLevel, ct);
                return set.Count == 0 ? BuilderOutcome.Fail("SetMastery <craft|gather> <category 1-5 | 0> <level>.") : BuilderOutcome.Ok($"{kind} mastery {category} set to {masteryLevel}.", set);
            }
            case "AcquireWingAll" when args.Length == 0:
            {
                var wings = actor.AcquireAllWings();
                if (wings.Count == 0) return BuilderOutcome.Fail("AcquireWingAll: enter the world first.");
                // Saved for account characters: they come back at the next character selection.
                if (actor.Store is { } store && actor.CharacterId is { } character && policy.AcquireWings is { } save)
                {
                    await save(store.AccountId, character, actor.RegisteredWings.ToArray(), ct);
                    return BuilderOutcome.Ok("All wings of your race added and saved.", wings);
                }
                return BuilderOutcome.Ok("All wings of your race added (development login: not saved).", wings);
            }
            case "ContentsUnlockAll" when args.Length == 0:
                return BuilderOutcome.Ok("Unlocked all contents.",
                    [GamePackets.ContentsUnlockUpdate(Enumerable.Range(1, UnlockTypes.Count - 1).Select(i => (byte)i).ToArray())]);
            case "Membership" when args is [var value] && Flag(value) is { } on:
                return BuilderOutcome.Ok(on ? "Membership active for 30 days." : "Membership off.", actor.SetMembership(on, now));
            case "BattlePassExp" when args is [var value] && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var passExp) && passExp is > 0 and <= 1_000_000:
                return BuilderOutcome.Ok($"{passExp} pass exp.", actor.GrantBattlePassExp(passExp, now));
            case "GrantShopCurrency" when args.Length is >= 1 and <= 2:
                return await GrantShopCurrencyAsync(actor, args, ct);
            case "AcquireItem" when args.Length is >= 1 and <= 3:
                return await AcquireItemAsync(actor, args, ct);
            case "SetLevel" when args is [var value] && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level >= 1 && level <= (data ?? GameData.Embedded).MaxLevel:
            {
                if (actor.Store is not { } store || actor.CharacterId is not { } character || policy.SetLevel is not { } setLevel)
                    return BuilderOutcome.Fail("SetLevel needs an account login.");
                await setLevel(store.AccountId, character, level, ct);
                return BuilderOutcome.Ok($"Level {level} saved; it applies after re-entering the world.");
            }
            case "SetAscension" when args is [var value] && AscensionGrade(value) is { } grade:
            {
                if (actor.Store is not { } store || actor.CharacterId is not { } character || policy.SetAscension is not { } setAscension)
                    return BuilderOutcome.Fail("SetAscension needs an account login.");
                await setAscension(store.AccountId, character, grade, ct);
                return BuilderOutcome.Ok($"Ascension grade {grade} saved; it applies after re-entering the world.");
            }
            case "ClassChange" when args is [var className]:
            {
                if (actor.Store is not { } store || actor.CharacterId is not { } character || policy.ChangeClass is not { } change)
                    return BuilderOutcome.Fail("ClassChange needs an account login.");
                var (changed, name, available) = await change(store.AccountId, character, className, ct);
                return changed
                    ? BuilderOutcome.Ok($"Class {name} saved; skills follow it. It applies after re-entering the world.")
                    : BuilderOutcome.Fail($"Unknown class {className}. Available: {string.Join(", ", available)}.");
            }
        }
        return null;
    }

    IReadOnlyList<(string Name, int MapId, Vector3d Position)>? destinations;

    /// <summary>A destination by exact name, else the only one containing the text; otherwise the candidates.</summary>
    ((string Name, int MapId, Vector3d Position)? Found, List<string> Matches) Destination(string text)
    {
        destinations ??= data!.Destinations().ToList();
        var exact = destinations.Where(d => d.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return (exact[0], []);
        var partial = destinations.Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? (partial[0], []) : (null, partial.Select(d => d.Name).Distinct().ToList());
    }

    /// <summary>
    /// OURS (not in BuilderCommand.json): runs a client dev menu button's server lines
    /// (DevTeleportDataTable, the c.devhelper buttons) as builder commands; client-side "c." lines
    /// cannot run from the server and are not in the data. Unknown text lists matching buttons.
    /// </summary>
    async Task<BuilderOutcome> DevAsync(GameSession actor, string name, long now, CancellationToken ct)
    {
        if (data!.DevButton(name) is not { } lines)
        {
            var matches = data.DevButtonNames.Where(n => n.Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
            return matches.Count == 0 ? BuilderOutcome.Fail($"No dev menu button matches {name}.")
                : BuilderOutcome.Ok($"{matches.Count} buttons: " + string.Join(", ", matches.Take(12)) + (matches.Count > 12 ? ", ..." : ""));
        }
        var packets = new List<byte[]>();
        var ran = 0;
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var outcome = await RunAsync(actor, line, now, ct);
            packets.AddRange(outcome.Packets);
            if (outcome.Result == 0) ran++;
            else
            {
                var verb = line.Split(' ', 2)[0];
                skipped[verb] = skipped.GetValueOrDefault(verb) + 1;
            }
        }
        var detail = skipped.Count == 0 ? "" : "; not available here: " + string.Join(", ", skipped.Select(s => s.Value > 1 ? $"{s.Key} x{s.Value}" : s.Key));
        return new BuilderOutcome(ran > 0 ? 0 : 1, $"{name}: ran {ran} of {lines.Count} commands{detail}.", packets);
    }

    async Task<BuilderOutcome?> GrantShopCurrencyAsync(GameSession actor, string[] args, CancellationToken ct)
    {
        if (actor.Store is not { } store || actor.CharacterId is not { } character || policy.GrantItem is not { } grant)
            return BuilderOutcome.Fail("GrantShopCurrency needs an account login.");
        long count = 100_000;
        if (args.Length == 2 && (!long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count is < 1 or > 1_000_000_000))
            return BuilderOutcome.Fail("Count must be 1..1000000000.");
        var token = args[0].ToLowerInvariant();
        var template = token switch
        {
            "kina" => GameSession.KinaItemId,
            "quna" => PlatformGoodsTables.Embedded.QunaItemId,
            _ when int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) => id,
            _ => 0,
        };
        if (template <= 0) return BuilderOutcome.Fail("Use kina, quna, or a currency template id.");
        await grant(store.AccountId, character, template, count, ct);
        return BuilderOutcome.Ok($"Granted {count} of currency item {template}.", await actor.InventoryChangesAsync(ct));
    }

    async Task<BuilderOutcome?> AcquireItemAsync(GameSession actor, string[] args, CancellationToken ct)
    {
        if (actor.Store is not { } store || actor.CharacterId is not { } character || policy.GrantItem is not { } grant || policy.SearchItems is not { } search)
            return BuilderOutcome.Fail("AcquireItem needs an account login and the item catalogue.");
        long count = 1;
        if (args.Length >= 2 && (!long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count is < 1 or > 1000000)) return null;
        if (args.Length == 3 && !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return null;  // enchant level: not stored yet
        var rows = await search(store.AccountId, args[0], ct);
        var item = rows.Count == 1 ? rows[0]
            : rows.FirstOrDefault(r => r.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase) || r.Id.ToString(CultureInfo.InvariantCulture) == args[0]);
        if (item.Name is null)
            return BuilderOutcome.Fail(rows.Count == 0 ? $"No item matches {args[0]}." : "Several items match: " + string.Join(", ", rows.Select(r => $"{r.Id} {r.Name}")));
        await grant(store.AccountId, character, item.Id, count, ct);
        // Stored first, then the client gets the difference (0x561b) so the item shows at once.
        return BuilderOutcome.Ok($"{count} x {item.Name} ({item.Id}) added.", await actor.InventoryChangesAsync(ct));
    }

    static bool? Flag(string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "on" => true,
        "false" or "0" or "off" => false,
        _ => null,
    };

    static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>EAscensionGrade 1..6: "3", "AscensionGrade_3", "AscensionGrade" (1), optionally "EAscensionGrade::" prefixed.</summary>
    static int? AscensionGrade(string value)
    {
        var name = value.Split("::")[^1];
        if (name.Equals("AscensionGrade", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith("AscensionGrade_", StringComparison.OrdinalIgnoreCase)) name = name["AscensionGrade_".Length..];
        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade) && grade is >= 1 and <= 6 ? grade : null;
    }

    /// <summary>An EContentsUnlockType by name ("Wing", "EContentsUnlockType::Wing") or number, 1..64.</summary>
    static int? UnlockType(string value)
    {
        var name = value.Split("::")[^1];
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number >= 1 && number < UnlockTypes.Count ? number : null;
        for (var i = 1; i < UnlockTypes.Count; i++)
            if (UnlockTypes[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>"true"/"false" for all, or HP:/MP:/SP: pairs (the other official kinds are not tracked here).</summary>
    static (bool? Hp, bool? Mp, bool? Sp)? Invincibility(string[] args)
    {
        if (args is [var all] && Flag(all) is { } every) return (every, every, every);
        if (args.Length == 0) return null;
        (bool? Hp, bool? Mp, bool? Sp) flags = (null, null, null);
        foreach (var arg in args)
        {
            var pair = arg.TrimEnd(',').Split(':', 2);
            if (pair.Length != 2 || Flag(pair[1]) is not { } on) return null;
            switch (pair[0].ToUpperInvariant())
            {
                case "HP": flags.Hp = on; break;
                case "MP": flags.Mp = on; break;
                case "SP": flags.Sp = on; break;
                case "DP" or "OP" or "FP" or "AP": break;
                default: return null;
            }
        }
        return flags;
    }

    static double[]? Coordinates(string[] values)
    {
        var parsed = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
            if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) || !double.IsFinite(parsed[i]) || Math.Abs(parsed[i]) > 1e7)
                return null;
        return parsed;
    }
}
