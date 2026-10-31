using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Drives the main hero quest chain from a level-1 character through client opcodes (talk, combat, objects, map events, rewards).
/// Records data gaps (needs home dat) vs step fallbacks (objective types the server should model via packets).
/// </summary>
static class CampaignPlaythroughHelpers
{
    /// <summary>Map-event scripts use absolute packet times; keep harness slices off the campaign clock.</summary>
    static long _mapEventHarnessSliceMs = 12_000_000L;

    static long NextMapEventHarnessNow()
    {
        _mapEventHarnessSliceMs += 480_000L;
        return _mapEventHarnessSliceMs;
    }

    public sealed record PlaythroughReport(
        int Race,
        int FinalLevel,
        int QuestsRewarded,
        string LastRewardedCode,
        string? BlockedQuestCode,
        string? BlockReason,
        IReadOnlyList<string> DataGaps,
        IReadOnlyList<string> PacketFallbacks,
        int LevelBeforeSideQuests,
        int LevelAfterSideQuests,
        IReadOnlyList<string> SideQuestAttempts);

    /// <summary>One rewarded hero quest in the level-cap campaign segment.</summary>
    public sealed record CampaignQuestStep(
        string Code,
        int UnlockLevel,
        int MapId,
        int LevelAfter,
        long ExpReward,
        IReadOnlyList<string> ObjectiveKinds,
        bool MapEventStep,
        bool InstanceStep,
        bool CelviceOrCutscene,
        bool DevQuestCompleteFallback,
        string? DevCompleteReason);

    public sealed record MainStoryCampaignReport(
        int Race,
        int MinLevel,
        int MaxLevel,
        int FinalLevel,
        int SegmentQuestsRewarded,
        string LastSegmentCode,
        string? CapGateQuestCode,
        IReadOnlyList<CampaignQuestStep> Steps,
        IReadOnlyList<string> SegmentDataGaps,
        IReadOnlyList<string> SegmentFallbacks);

    static readonly IReadOnlyDictionary<int, string> LevelGateHeroQuest = new Dictionary<int, string>
    {
        [1] = "HQ1103010",
        // First hero quest above the live level cap (46); side ascension runs here before accept is attempted.
        [2] = "HQ2103010",
    };

    static readonly IReadOnlyDictionary<int, string[]> SideQuestsBeforeLevelGate = new Dictionary<int, string[]>
    {
        [1] = ["AQ1602020"],
        [2] = ["AQ2602040"],
    };

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly HashSet<string> PacketObjectiveTypes =
    [
        "AskNpc", "CloseNpc", "KillNpc", "CollectFakeItem", "UseEnvObj", "EnterVolumePC", "EnterSubZone",
        "CollectItem", "ClearMapEvent", "PCLevel", "ClearQuest", "ClearQuestType", "ClearQuestExplorationType", "EngraveTeleportArtifact",
    ];

    static readonly HashSet<string> ClientOnlyObjectiveTypes = QuestLog.ClientObjectiveTypes;

    public const string ElyosStarterTerminalQuest = "HQ1102011";
    public const string ElyosMainStoryLevel15Terminal = "HQ1102090";
    public const string ElyosMainStoryLevel24Terminal = "HQ1102223";
    public const string ElyosMainStoryCap45Terminal = "HQ1103620";
    public const int ElyosStarterMaxLevel = 10;
    static readonly string[] ElyosStarterSideQuests = ["AQ1602000"];

    public static string[] LoadLightHeroChain() => LoadHeroChains().Light;

    public static IEnumerable<string> ElyosMainStoryCodesThrough(string terminalCode)
    {
        foreach (var code in LoadLightHeroChain())
        {
            yield return code;
            if (code == terminalCode) yield break;
        }
    }

    /// <summary>Light Poeta / Verteron outskirts hero line through level 10 plus ascension side quest (SharedWorld, packet objectives).</summary>
    public static async Task<PlaythroughReport> RunElyosStarterPlaythroughAsync(long now) =>
        (await RunElyosStarterWithSessionAsync(now)).Report;

    /// <summary>Strict packet playthrough for one quest (integration tests).</summary>
    public static Task<PlayResult> PlayQuestStrictAsync(
        GameSession session, FakeCharacterStore store, string code, int race, long t, List<string> dataGaps, List<string> fallbacks) =>
        PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks, allowDevShortcuts: false);

    public static Task<PlaythroughReport> RunElyosMainStoryThroughAsync(string terminalCode, long now) =>
        RunElyosMainStoryWithSessionAsync(now, terminalCode).ContinueWith(t => t.Result.Report);

    public static async Task<(PlaythroughReport Report, GameSession Session, FakeCharacterStore Store)> RunElyosMainStoryWithSessionAsync(
        long now, string terminalCode, string? stopBeforeCode = null)
    {
        const int race = 1;
        var codes = ElyosMainStoryCodesThrough(terminalCode).ToArray();
        return await RunElyosPlaythroughCoreAsync(now, race, codes, terminalCode, stopBeforeCode, includeStarterSideQuests: false);
    }

    public static async Task<(PlaythroughReport Report, GameSession Session, FakeCharacterStore Store)> RunElyosStarterWithSessionAsync(long now, string? stopBeforeCode = null)
    {
        const int race = 1;
        var codes = ElyosStarterHeroCodes().ToArray();
        return await RunElyosPlaythroughCoreAsync(now, race, codes, ElyosStarterTerminalQuest, stopBeforeCode, includeStarterSideQuests: true);
    }

    static async Task<(PlaythroughReport Report, GameSession Session, FakeCharacterStore Store)> RunElyosPlaythroughCoreAsync(
        long now, int race, string[] codes, string terminalCode, string? stopBeforeCode, bool includeStarterSideQuests)
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race, level: 1);
        var opening = DevelopmentDefaults.OpeningQuestLight;
        Assert.Contains(store.Quests, q => q.Code == opening && q.Status == QuestLog.Rewarded);

        var dataGaps = new List<string>();
        var fallbacks = new List<string>();
        var sideAttempts = new List<string>();
        var t = now;
        var rewarded = 0;
        string last = opening;
        string? blocked = null;
        string? blockReason = null;

        foreach (var code in codes.Skip(1))
        {
            if (code == stopBeforeCode) break;

            if (includeStarterSideQuests && code == "HQ1101021")
                sideAttempts.AddRange(await TryElyosSideQuestsAsync(session, store, t, dataGaps, fallbacks));

            var played = await PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks, allowDevShortcuts: false);
            t = played.Time;
            if (!played.Ok)
            {
                blocked = code;
                blockReason = played.Reason;
                break;
            }
            rewarded++;
            last = code;
            if (code == terminalCode) break;
        }

        var report = new PlaythroughReport(race, session.Level, rewarded, last, blocked, blockReason, dataGaps, fallbacks,
            session.Level, session.Level, sideAttempts);
        return (report, session, store);
    }

    static IEnumerable<string> ElyosStarterHeroCodes()
    {
        foreach (var code in LoadHeroChains().Light)
        {
            yield return code;
            if (code == ElyosStarterTerminalQuest) yield break;
        }
    }

    static async Task<List<string>> TryElyosSideQuestsAsync(
        GameSession session,
        FakeCharacterStore store,
        long t,
        List<string> dataGaps,
        List<string> fallbacks)
    {
        var notes = new List<string>();
        foreach (var side in ElyosStarterSideQuests)
        {
            if (session.Quests!.Get(side) is { Status: QuestLog.Rewarded })
            {
                notes.Add($"{side}: already rewarded");
                continue;
            }

            if (session.Quests!.Get(side) is null && session.CanAcceptQuest(side))
                await EnsureQuestActiveAsync(session, side, t);
            if (session.Quests!.Get(side) is null)
            {
                notes.Add($"{side}: not in journal");
                continue;
            }

            var played = await PlayQuestToRewardAsync(session, store, side, 1, t, dataGaps, fallbacks, allowDevShortcuts: false);
            t = played.Time;
            notes.Add(played.Ok ? $"{side}: rewarded" : $"{side}: failed — {played.Reason}");
        }
        return notes;
    }

    public static async Task<PlaythroughReport> RunHeroCampaignAsync(int race, long now)
    {
        var (report, _, _) = await RunHeroCampaignWithSessionAsync(race, now);
        return report;
    }

    /// <summary>Starter-zone playthrough (strict packet path, no parity dev shortcuts).</summary>
    public static async Task<(bool Ok, string? Reason, long Time)> PlayQuestToRewardForStarterAsync(
        GameSession session,
        FakeCharacterStore store,
        string code,
        int race,
        long t,
        List<string> dataGaps,
        List<string> fallbacks)
    {
        var r = await PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks, allowDevShortcuts: false);
        return (r.Ok, r.Reason, r.Time);
    }

    public static Task NudgeAutoAcquireForStarterAsync(GameSession session, int race, long now) =>
        NudgeAutoAcquireAsync(session, race, now);

    public static Task TryAcceptQuestForStarterAsync(GameSession session, string code, int race, long now) =>
        TryAcceptQuestForParityAsync(session, code, race, now);

    public static async Task<MainStoryCampaignReport> RunMainStoryCampaignAsync(int race, long now, int minLevel = 10, int maxLevel = CapLevel, bool allowDevShortcuts = true)
    {
        var segmentSteps = new List<CampaignQuestStep>();
        var (report, session, store) = await RunHeroCampaignWithSessionAsync(race, now, segmentSteps, minLevel, maxLevel, allowDevShortcuts);
        var segmentCodes = new HashSet<string>(segmentSteps.Select(s => s.Code), StringComparer.Ordinal);
        var segmentGaps = report.DataGaps.Where(g => segmentCodes.Any(c => g.StartsWith(c, StringComparison.Ordinal))).ToList();
        var segmentFallbacks = report.PacketFallbacks.Where(f => segmentCodes.Any(c => f.StartsWith(c, StringComparison.Ordinal))).ToList();
        var lastSegment = segmentSteps.LastOrDefault()?.Code ?? "";
        var capGate = report.BlockedQuestCode;
        if (capGate is not null)
        {
            if (QuestBook.Embedded.Find(capGate) is not { } gateDef
                || gateDef.Unavailable
                || gateDef.Level > CapLevel)
                capGate = null;
        }
        return new MainStoryCampaignReport(race, minLevel, maxLevel, report.FinalLevel, segmentSteps.Count, lastSegment,
            capGate, segmentSteps, segmentGaps, segmentFallbacks);
    }

    public static Task<MainStoryCampaignReport> RunMainStoryCampaignStrictAsync(int race, long now, int minLevel = 10, int maxLevel = CapLevel) =>
        RunMainStoryCampaignAsync(race, now, minLevel, maxLevel, allowDevShortcuts: false);

    public static async Task<(PlaythroughReport Report, GameSession Session, FakeCharacterStore Store)> RunHeroCampaignWithSessionAsync(
        int race, long now, List<CampaignQuestStep>? segmentLog = null, int segmentMin = 10, int segmentMax = CapLevel, bool allowDevShortcuts = true)
    {
        var chain = LoadHeroChains();
        var codes = (race == 2 ? chain.Dark : chain.Light).ToArray();
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race, level: 1);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now + 2);
        var opening = race == 2 ? DevelopmentDefaults.OpeningQuestDark : DevelopmentDefaults.OpeningQuestLight;
        Assert.Contains(store.Quests, q => q.Code == opening && q.Status == QuestLog.Rewarded);
        Assert.Equal(1, session.Level);

        var dataGaps = new List<string>();
        var fallbacks = new List<string>();
        var sideAttempts = new List<string>();
        var t = now;
        var rewarded = 0;
        string last = opening;
        string? blocked = null;
        string? blockReason = null;
        var levelBeforeSide = session.Level;
        var levelAfterSide = session.Level;

        foreach (var code in codes.Skip(1))
        {
            if (segmentLog is not null && segmentMax < CapLevel && HeroQuestRecommendedLevel(code) > segmentMax)
                break;

            if (LevelGateHeroQuest.TryGetValue(race, out var gate) && code == gate)
            {
                levelBeforeSide = session.Level;
                sideAttempts.AddRange(await TrySideQuestsBeforeLevelGateAsync(session, store, race, t, dataGaps, fallbacks));
                levelAfterSide = session.Level;
                sideAttempts.Add($"level {levelBeforeSide} -> {levelAfterSide} after side quests (gate {gate} needs quest level {session.Quests!.Book.Find(gate)!.Level})");
            }

            var fbBefore = fallbacks.Count;
            var levelBefore = session.Level;
            var expBefore = store.Progress.Exp;
            var played = await PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks, allowDevShortcuts);
            t = played.Time;
            var devReason = played.DevCompleteReason;
            if (!played.Ok)
            {
                blocked = code;
                blockReason = played.Reason;
                break;
            }
            rewarded++;
            last = code;
            if (segmentLog is not null && QuestBook.Embedded.Find(code) is { Level: var ql } && ql >= segmentMin && ql <= segmentMax)
            {
                var questFallbacks = fallbacks.Skip(fbBefore).ToList();
                segmentLog.Add(BuildCampaignStep(code, session, store, levelBefore, expBefore, questFallbacks, devReason));
            }
        }

        var report = new PlaythroughReport(race, session.Level, rewarded, last, blocked, blockReason, dataGaps, fallbacks,
            levelBeforeSide, levelAfterSide, sideAttempts);
        return (report, session, store);
    }

    static async Task<List<string>> TrySideQuestsBeforeLevelGateAsync(
        GameSession session,
        FakeCharacterStore store,
        int race,
        long t,
        List<string> dataGaps,
        List<string> fallbacks)
    {
        var notes = new List<string>();
        if (!SideQuestsBeforeLevelGate.TryGetValue(race, out var sideCodes)) return notes;

        await NudgeAutoAcquireAsync(session, race, t);

        foreach (var side in sideCodes)
        {
            if (session.Quests!.Get(side) is { Status: QuestLog.Rewarded })
            {
                notes.Add($"{side}: already rewarded");
                continue;
            }

            if (session.Quests!.Book.Find(side) is not { } def)
            {
                notes.Add($"{side}: missing from quest book");
                continue;
            }

            var prereqs = (def.Prerequisites ?? []).Concat(def.Previous ?? []).ToArray();
            var missing = prereqs.Where(p => session.Quests!.Get(p) is not { Status: QuestLog.Rewarded }).ToArray();
            if (missing.Length > 0)
                notes.Add($"{side}: missing prereqs {string.Join(", ", missing)}");

            if (session.Quests!.Get(side) is null)
            {
                if (!session.Quests!.CanAccept(side, session.Level))
                    await session.QuestCommandAsync("QuestAdd", side, default);
                else
                    await EnsureQuestActiveAsync(session, side, t);
            }

            if (session.Quests!.Get(side) is null)
            {
                notes.Add($"{side}: not in journal (UnifiedObjectiveScore / acquire conditions?)");
                continue;
            }

            var played = await PlayQuestToRewardAsync(session, store, side, race, t, dataGaps, fallbacks);
            t = played.Time;
            notes.Add(played.Ok
                ? $"{side}: rewarded at level {session.Level}"
                : $"{side}: failed — {played.Reason}");
        }

        return notes;
    }

    public sealed record PlayResult(bool Ok, string? Reason, long Time, string? DevCompleteReason = null);

    /// <summary>Parity sweep: accept gates are exercised above; reward via dev commands to keep ~900 quests in CI time.</summary>
    static async Task<PlayResult> PlayQuestToRewardForParityAsync(
        GameSession session,
        FakeCharacterStore store,
        string code,
        int race,
        long t,
        List<string> dataGaps,
        List<string> fallbacks)
    {
        t += 20;
        if (session.Quests!.Get(code) is null)
            return new PlayResult(false, "not_in_journal", t);
        if (session.Quests!.Get(code)!.Status == QuestLog.Incomplete)
            await session.QuestCommandAsync("QuestComplete", code, default);
        await RewardQuestViaPacketsAsync(session, code, t);
        fallbacks.Add($"{code}: parity QuestComplete/Reward");
        return session.Quests!.Get(code)!.Status == QuestLog.Rewarded
            ? new PlayResult(true, null, t + 20)
            : new PlayResult(false, "reward_after_complete_failed", t);
    }

    static async Task<PlayResult> PlayQuestToRewardAsync(
        GameSession session,
        FakeCharacterStore store,
        string code,
        int race,
        long t,
        List<string> dataGaps,
        List<string> fallbacks,
        bool allowDevShortcuts = true)
    {
        t += 20;
        if (session.Quests!.Book.Find(code) is { } def)
            await PrepareQuestAcceptEnvironmentAsync(session, def, t + 10);
        await EnsureOnQuestMapAsync(session, code, t);
        await EnsureQuestActiveAsync(session, code, t + 30);
        if (session.Quests!.Get(code) is null)
            return new PlayResult(false, "quest not in journal and cannot accept", t);

        // Map-event scripts advance packet time by minutes; field kills reuse a lower t and stay on cooldown.
        if (!allowDevShortcuts)
            session.NoCool = true;

        var mapEventsAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guardMax = allowDevShortcuts ? 100 : 480;
        for (var guard = 0; guard < guardMax && session.Quests!.Get(code) is { Status: QuestLog.Incomplete }; guard++)
        {
            var advanced = false;
            for (var pass = 0; pass < 4; pass++)
                advanced |= await TryAdvanceQuestPacketsAsync(session, store, code, race, t + pass * 5, dataGaps, mapEventsAttempted);
            if (advanced)
            {
                t += 40;
                _ = session.QuestWorldGatingForTests(t);
                continue;
            }

            var record = session.Quests!.Get(code)!;
            if (session.Quests!.CurrentStep(record) is not { } step)
                break;

            var unmodeled = step.Objectives.Take(6)
                .Where(o => !PacketObjectiveTypes.Contains(o.Type) && !ClientOnlyObjectiveTypes.Contains(o.Type))
                .Select(o => $"{code} step {record.Step}: {o.Type}")
                .ToList();
            if (unmodeled.Count > 0)
            {
                foreach (var g in unmodeled.Where(g => !dataGaps.Contains(g))) dataGaps.Add(g);
                if (!allowDevShortcuts)
                    return new PlayResult(false, $"unmodeled objectives: {unmodeled[0]}", t);
                var done = await session.QuestCommandAsync("QuestComplete", code, default);
                if (session.Quests!.Get(code)!.Status >= QuestLog.Complete) break;
                return new PlayResult(false, $"unmodeled objectives: {unmodeled[0]}", t, ClassifyFallback(session, code, "dat_gap"));
            }

            if (!allowDevShortcuts)
            {
                t += 120;
                continue;
            }

            var stepped = await session.QuestCommandAsync("QuestCompleteStep", code, default);
            if (stepped.Message.Contains("step done", StringComparison.OrdinalIgnoreCase))
            {
                t += 10;
                _ = session.QuestWorldGatingForTests(t);
                continue;
            }
            break;
        }

        string? devReason = null;
        if (session.Quests!.Get(code)!.Status == QuestLog.Incomplete)
        {
            if (!allowDevShortcuts)
            {
                var stuck = session.Quests!.Get(code)!;
                var stuckStep = session.Quests!.CurrentStep(stuck);
                var kinds = stuckStep is null ? "?" : string.Join(", ", stuckStep.Objectives.Take(3).Select(o => o.Type));
                return new PlayResult(false, $"stuck incomplete (strict) step {stuck.Step}: {kinds}", t);
            }
            devReason = ClassifyFallback(session, code, "harness_stuck");
            var complete = await session.QuestCommandAsync("QuestComplete", code, default);
            if (session.Quests!.Get(code)!.Status < QuestLog.Complete)
                return new PlayResult(false, $"stuck incomplete: {complete.Message}", t, devReason);
            fallbacks.Add($"{code}: QuestComplete");
        }

        await RewardQuestViaPacketsAsync(session, code, t);
        if (session.Quests!.Get(code)!.Status != QuestLog.Rewarded)
            return new PlayResult(false, "reward packet failed", t, devReason);
        await GameSessionTestHelpers.LoadInventoryAsync(session, t + 5);
        t += 50;
        return new PlayResult(true, null, t, devReason);
    }

    static async Task EnsureOnQuestMapAsync(GameSession session, string code, long now)
    {
        if (session.Quests!.Book.Find(code) is not { Map: var mapId } || mapId <= 0) return;
        var onFieldCopy = session.InstanceKey != 0 && MapEventTables.Embedded.DungeonOfMap(session.MapId ?? 0) is null;
        if (session.MapId == mapId && !onFieldCopy) return;
        session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
    }

    static async Task<bool> EngraveArtifactAsync(GameSession session, string artifact, long now)
    {
        var mapId = session.MapId ?? 1000;
        var placed = Kibelisks.OnMap(mapId).ToList();
        var index = placed.FindIndex(k => string.Equals(k.Artifact, artifact, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            foreach (var tryMap in new[] { 1000, 1010, 1100 })
            {
                if (tryMap == mapId) continue;
                placed = Kibelisks.OnMap(tryMap).ToList();
                index = placed.FindIndex(k => string.Equals(k.Artifact, artifact, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                session.Teleport(tryMap, GameSessionTestHelpers.MapStartOrDefault(tryMap), now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
                mapId = tryMap;
                break;
            }
        }
        if (index < 0) return false;
        var wireKey = Kibelisks.FirstKey + index;
        var at = new Vector3d(placed[index].Position[0], placed[index].Position[1], placed[index].Position.Length > 2 ? placed[index].Position[2] : 0);
        await MapEventScenarioHelpers.SyncPositionAsync(session, at, now + 20);
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), now + 30);
        return use.Any(p => GameSessionTestHelpers.HasOpcode(p, 0x8d10))
               || session.Quests!.All.Any(q => q.Goals.Any(g => g > 0));
    }

    static async Task EnsureQuestActiveAsync(GameSession session, string code, long now)
    {
        if (session.Quests!.Get(code) is not null) return;
        if (!session.CanAcceptQuest(code))
            return;
        var questId = session.Quests!.Book.Find(code)!.Id;
        var npcKey = FindOfferNpcKey(session, code) ?? 0;
        await session.HandleAsync(new PacketWriter().U16(0x8d0a).I32(questId).I32(npcKey).ToArray(), now);
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
    }

    static readonly Lazy<Dictionary<string, int>> SubzoneToMap = new(() =>
    {
        using var stream = typeof(Subzones).Assembly.GetManifestResourceStream("subzones.json")
            ?? throw new InvalidOperationException("subzones.json");
        using var doc = JsonDocument.Parse(stream);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapProp in doc.RootElement.GetProperty("maps").EnumerateObject())
        {
            var mapId = int.Parse(mapProp.Name, CultureInfo.InvariantCulture);
            foreach (var area in mapProp.Value.EnumerateArray())
            {
                if (area.TryGetProperty("name", out var nameEl))
                    dict[nameEl.GetString()!] = mapId;
            }
        }
        return dict;
    });

    const ushort CloseEnvObjAchievementRq = 0xe2ca;

    static async Task PrepareQuestAcceptEnvironmentAsync(GameSession session, QuestDefinition def, long now)
    {
        var mapHint = def.Map > 0 ? def.Map : 0;
        foreach (var c in def.Conditions ?? [])
        {
            switch (c.Type)
            {
                case "SubZone":
                    var subMap = SubzoneToMap.Value.GetValueOrDefault(c.Value);
                    if (subMap > 0) mapHint = subMap;
                    if (subMap > 0)
                    {
                        var at = MapEventScenarioHelpers.TriggerOrSubzonePosition(subMap, c.Value);
                        session.Teleport(subMap, at, now);
                        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
                        await MapEventScenarioHelpers.SyncPositionAsync(session, at, now + 10);
                    }
                    break;
                case "CloseEnvObj":
                    await CloseEnvObjectByNameAsync(session, c.Value, mapHint, now + 20);
                    break;
            }
        }

        if (def.Map > 0 && session.MapId != def.Map)
        {
            session.Teleport(def.Map, GameSessionTestHelpers.MapStartOrDefault(def.Map), now + 30);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 35);
        }
    }

    static async Task CloseEnvObjectByNameAsync(GameSession session, string envName, int mapHint, long now)
    {
        var maps = new List<int>();
        if (mapHint > 0) maps.Add(mapHint);
        maps.AddRange(SubzoneToMap.Value.Values.Distinct());
        maps.AddRange(GameData.Embedded.SpawnMaps);
        foreach (var mapId in maps.Distinct())
        {
            var objects = WorldObjects.ObjectsOn(mapId).ToList();
            var index = objects.FindIndex(o => string.Equals(o.Name, envName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            if (session.MapId != mapId)
            {
                session.Teleport(mapId, objects[index].At, now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
            }
            await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, now + 10);
            var wireKey = WorldObjects.FirstKey + index;
            await session.HandleAsync(new PacketWriter().U16(CloseEnvObjAchievementRq).I32(wireKey).ToArray(), now + 15);
            return;
        }
    }

    static async Task TryAcceptQuestForParityAsync(GameSession session, string code, int race, long now)
    {
        if (session.Quests!.Get(code) is not null) return;
        if (session.Quests!.Book.Find(code) is not { } def) return;

        await EnsureOnQuestMapAsync(session, code, now + 20);
        await PrepareQuestAcceptEnvironmentAsync(session, def, now + 40);
        await NudgeAutoAcquireAsync(session, race, now + 60);

        if (session.Quests!.Get(code) is null && session.CanAcceptQuest(code))
        {
            if (def.Acquire is "Npc" or "Auto")
                await EnsureQuestActiveAsync(session, code, now + 80);
            else if (def.Acquire is "None" or "Item")
                await session.QuestCommandAsync("QuestAdd", code, default);
        }

        if (session.Quests!.Get(code) is null && def.Acquire == "Auto")
            await session.QuestCommandAsync("QuestAdd", code, default);
    }

    static string ParityBlockReason(GameSession session, string code, QuestDefinition def, int race)
    {
        if (!QuestBook.RaceMatches(def, race)) return "wrong_race";
        if (session.Level < def.Level) return "level";
        var detail = session.QuestAcceptBlockReason(code);
        if (detail is not null && detail.StartsWith("condition:UnifiedObjectiveScore", StringComparison.Ordinal))
            return "unified_score";
        if (detail is not null && detail.StartsWith("condition:", StringComparison.Ordinal))
            return detail;
        if (detail is not null) return detail;
        return def.Acquire switch
        {
            "Item" => "acquire_item_scroll",
            "None" => "acquire_ui",
            _ => "cannot_accept",
        };
    }

    static int? FindOfferNpcKey(GameSession session, string code)
    {
        foreach (var npc in session.Targets.Values)
        {
            if (session.Quests!.OfferedBy(npc.NpcId, session.Level).Contains(code))
                return npc.Key;
        }
        return session.Targets.Values.FirstOrDefault()?.Key;
    }

    static async Task<bool> TryAdvanceQuestPacketsAsync(
        GameSession session,
        FakeCharacterStore store,
        string code,
        int race,
        long now,
        List<string> dataGaps,
        HashSet<string>? mapEventsAttempted = null)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } record) return false;
        if (session.Quests!.CurrentStep(record) is not { } step) return false;
        var touched = false;

        foreach (var o in step.Objectives.Take(6))
        {
            switch (o.Type)
            {
                case "AskNpc" or "CloseNpc" when o.Npc is { } npc:
                    touched |= await TalkQuestNpcAsync(session, code, npc, now);
                    break;
                case "KillNpc" when o.Npcs is { } npcs:
                    touched |= await KillQuestNpcsAsync(session, code, npcs, o.Count, QuestMap(session, code), now);
                    break;
                case "CollectFakeItem" when o.Npcs is { } fakeNpcs:
                    touched |= await CollectFakeItemNpcsAsync(session, code, fakeNpcs, o.Count, QuestMap(session, code), now);
                    break;
                case "UseEnvObj" when o.Envs is { Length: > 0 } envs:
                    touched |= await UseQuestEnvUntilProgressAsync(session, code, o, envs, now);
                    break;
                case "EnterVolumePC" or "EnterSubZone":
                    touched |= await EnterQuestVolumeAsync(session, code, o, now);
                    break;
                case "CollectItem" when o.Item is { } item:
                    touched |= await SyncCollectItemAsync(session, store, item, o.Count, now);
                    break;
                case "ClearMapEvent" when o.Target is { } ev:
                    if (mapEventsAttempted is not null && mapEventsAttempted.Contains(ev)) break;
                    if (await TryClearMapEventAsync(session, code, ev, now, race))
                    {
                        touched = true;
                        mapEventsAttempted?.Add(ev);
                    }
                    break;
                case "PCLevel":
                    touched = session.Level >= (o.Count > 0 ? o.Count : 1);
                    break;
                case "ClearQuest" when o.Target is { } dep:
                    if (session.Quests!.Get(dep) is null || session.Quests!.Get(dep)!.Status < QuestLog.Rewarded)
                        await session.QuestCommandAsync("QuestAdd", dep, default);
                    if (session.Quests!.Get(dep)!.Status < QuestLog.Rewarded)
                        await RewardQuestViaPacketsAsync(session, dep, now);
                    touched = session.Quests!.Get(dep)!.Status >= QuestLog.Rewarded;
                    break;
                case "ClearQuestType" when o.Target is { } qtype:
                    touched = session.Quests!.All.Count(q => q.Status >= QuestLog.Complete
                        && session.Quests!.Book.Find(q.Code)?.Type == qtype) >= o.Count;
                    break;
                case "ClearQuestExplorationType" when o.Target is { } subtype:
                    touched = session.Quests!.All.Count(q => q.Status >= QuestLog.Complete
                        && session.Quests!.Book.Find(q.Code) is { } eq && eq.Type == "Exploration"
                        && QuestBook.ExplorationSubtype(eq) == subtype) >= o.Count;
                    break;
                case "EngraveTeleportArtifact" when o.Target is { } artifact:
                    touched |= await EngraveArtifactAsync(session, artifact, now);
                    break;
                case var t when ClientOnlyObjectiveTypes.Contains(t):
                    for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
                    {
                        if (!ReferenceEquals(step.Objectives[i], o) || record.Uid == 0) continue;
                        await session.HandleAsync(new PacketWriter().U16(0x9230).U8(1).I32((int)record.Uid).I32(i).ToArray(), now);
                    }
                    touched = true;
                    break;
                default:
                    if (!dataGaps.Contains($"{code} step {record.Step}: {o.Type}"))
                        dataGaps.Add($"{code} step {record.Step}: {o.Type}");
                    break;
            }
        }

        if (touched)
            await session.TickAsync(now + 100);
        return touched;
    }

    static async Task RefreshSpawnsOnMapAsync(GameSession session, int mapId, long now)
    {
        session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await session.TickAsync(now + 20);
    }

    static async Task<bool> TalkQuestNpcAsync(GameSession session, string code, int npcId, long now)
    {
        var npc = session.Targets.Values.FirstOrDefault(n => n.NpcId == npcId);
        if (npc is null)
        {
            var questMap = session.Quests!.Book.Find(code) is { Map: var m } ? m : 0;
            var maps = new List<int>();
            if (questMap > 0) maps.Add(questMap);
            if (session.MapId is { } cur) maps.Add(cur);
            maps.AddRange([200001, 200002, 200005, 200006, 1010, 1000]);
            foreach (var tryMap in maps.Distinct())
            {
                if (NpcSpawnAt(tryMap, npcId) is not { } at) continue;
                var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(tryMap);
                if (dungeonId > 0 && session.MapId != tryMap)
                {
                    session.EnterDungeonById(dungeonId, now);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 8);
                }
                else
                {
                    session.Teleport(tryMap, at, now);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
                }
                await session.TickAsync(now + 20);
                npc = session.Targets.Values.FirstOrDefault(n => n.NpcId == npcId);
                if (npc is not null) break;
            }
        }
        if (npc is null) return false;
        var before = session.Quests!.Get(code)!;
        var stepBefore = before.Step;
        var goalsBefore = before.Goals.ToArray();
        var mapId = session.MapId ?? GamePackets.SpawnMapId;
        session.Teleport(mapId, npc.Position with { X = npc.Position.X + 80 }, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await session.HandleAsync(new PacketWriter().U16(0xe240).I32(npc.Key).ToArray(), now + 10);
        var record = before;
        if (session.Quests!.CurrentStep(record) is { } step)
        {
            var index = Array.FindIndex(step.Objectives, o => o is { Type: "AskNpc" or "CloseNpc", Npc: var id } && id == npcId);
            if (index >= 0 && record.Uid != 0)
                await session.HandleAsync(new PacketWriter().U16(0x8d2b).U8(1).I32((int)record.Uid).I32(npc.Key).ToArray(), now + 15);
        }
        await session.HandleAsync(new PacketWriter().U16(0xe242).I32(npc.Key).ToArray(), now + 20);
        var after = session.Quests!.Get(code)!;
        if (after.Step > stepBefore) return true;
        for (var i = 0; i < after.Goals.Length && i < goalsBefore.Length; i++)
            if (after.Goals[i] > goalsBefore[i]) return true;
        return false;
    }

    static Vector3d? NpcSpawnAt(int mapId, int npcId)
    {
        if (GameData.Embedded.NpcPlaced(npcId, mapId) is { } placed)
            return new Vector3d(placed.Position[0], placed.Position[1], placed.Position[2]);
        foreach (var s in GameData.Embedded.Spawns(mapId))
            if (s.NpcId == npcId)
                return new Vector3d(s.Position[0], s.Position[1], s.Position[2]);
        return null;
    }

    public static Task<bool> KillQuestNpcsForTestAsync(GameSession session, string questCode, long now, int[] npcIds, int count, int mapHint) =>
        KillQuestNpcsAsync(session, questCode, npcIds, count, mapHint, now);

    public static Task<bool> UseQuestEnvForTestAsync(GameSession session, string questCode, QuestObjective objective, long now) =>
        UseQuestEnvUntilProgressAsync(session, questCode, objective, objective.Envs ?? [], now);

    public static Task<bool> EnterQuestVolumeForTestAsync(GameSession session, string questCode, QuestObjective objective, long now) =>
        EnterQuestVolumeAsync(session, questCode, objective, now);

    public static Task<bool> ClearQuestMapEventForTestAsync(GameSession session, string questCode, string eventName, long now, int race = 1) =>
        TryClearMapEventAsync(session, questCode, eventName, now, race);

    static async Task<bool> KillQuestNpcsAsync(GameSession session, string? questCode, int[] npcIds, int count, int mapHint, long now)
    {
        if (mapHint > 0 && session.MapId != mapHint)
            await RefreshSpawnsOnMapAsync(session, mapHint, now);
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        var swingPeriod = GameData.Embedded.Skill(skillId) is { } sk ? SkillCooldown.LockoutMs(sk) : 900;
        var record = questCode is not null ? session.Quests!.Get(questCode) : null;
        var stepBefore = record?.Step ?? 0;
        var goalsBefore = record?.Goals.ToArray();
        _ = session.QuestWorldGatingForTests(now);
        var attemptsMax = Math.Max(count * 6, count + npcIds.Length * 8);
        for (var attempt = 0; attempt < attemptsMax; attempt++)
        {
            if (questCode is not null && QuestKillStepDone(session, questCode)) break;
            var candidates = session.Targets.Values.Where(t => !t.Dead && npcIds.Contains(t.NpcId)).Take(32).ToList();
            if (candidates.Count == 0)
            {
                foreach (var id in npcIds)
                {
                    if (mapHint > 0 && NpcSpawnAt(mapHint, id) is { } at)
                    {
                        await MapEventScenarioHelpers.SyncPositionAsync(session, at, now);
                        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 8);
                        await session.TickAsync(now + 25);
                        _ = session.QuestWorldGatingForTests(now + 30);
                        break;
                    }
                }
                candidates = session.Targets.Values.Where(t => !t.Dead && npcIds.Contains(t.NpcId)).Take(32).ToList();
                if (candidates.Count == 0)
                {
                    if (mapHint > 0) await RefreshSpawnsOnMapAsync(session, mapHint, now + 40);
                    now += 50;
                    continue;
                }
            }
            var prey = candidates[attempt % candidates.Count];
            await MapEventScenarioHelpers.SyncPositionAsync(session, prey.Position with { X = prey.Position.X + 100 }, now);
            for (var swing = 0; swing < 24 && prey is { Dead: false }; swing++)
            {
                prey = session.Targets.Values.FirstOrDefault(t => t.Key == prey.Key) ?? prey;
                prey.Immune = false;
                if (prey.Relationship is not (6 or 11)) prey.Relationship = 6;
                if (prey.Hp > 1) prey.Hp = 1;
                var castAt = now + 30 + swing * Math.Max(swingPeriod, MapEventScenarioHelpers.SkillHitsLandMs(skillId) + 80);
                var cast = await session.HandleAsync(MapEventScenarioHelpers.CombatCast(skillId, prey.Key, prey.Position), castAt);
                await session.TickAsync(castAt + MapEventScenarioHelpers.SkillHitsLandMs(skillId) + 200);
                _ = cast;
                now = castAt + Math.Max(swingPeriod, MapEventScenarioHelpers.SkillHitsLandMs(skillId) + 200);
                prey = session.Targets.Values.FirstOrDefault(t => t.Key == prey.Key && !t.Dead) ?? prey;
            }
            await session.TickAsync(now + 20);
            _ = session.QuestWorldGatingForTests(now + 25);
        }
        if (questCode is null) return false;
        if (session.Quests!.Get(questCode) is not { } after) return false;
        if (after.Step > stepBefore || after.Status >= QuestLog.Complete) return true;
        if (goalsBefore is not null)
        {
            for (var i = 0; i < after.Goals.Length && i < goalsBefore.Length; i++)
                if (after.Goals[i] > goalsBefore[i]) return true;
        }
        return false;
    }

    static bool QuestKillStepDone(GameSession session, string code)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } record) return true;
        if (session.Quests!.CurrentStep(record) is not { } step) return true;
        for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
        {
            var o = step.Objectives[i];
            if (o.Type is not ("KillNpc" or "CollectFakeItem")) continue;
            if (record.Goals[i] < o.Count) return false;
        }
        return true;
    }

    static async Task<bool> UseQuestEnvUntilProgressAsync(GameSession session, string code, QuestObjective o, int[] envIds, long now)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } record) return false;
        if (session.Quests!.CurrentStep(record) is not { } step) return false;
        var idx = Array.FindIndex(step.Objectives, obj => ReferenceEquals(obj, o));
        if (idx < 0) idx = Array.FindIndex(step.Objectives, obj => obj.Type == "UseEnvObj" && obj.Envs?.SequenceEqual(envIds) == true);
        if (idx < 0) return false;
        var before = record.Goals[idx];
        for (var attempt = 0; attempt < Math.Max(4, o.Count * 3) && session.Quests!.Get(code)!.Goals[idx] < o.Count; attempt++)
        {
            await UseQuestEnvAsync(session, envIds, o.Count, QuestMap(session, code), now + attempt * 40);
            _ = session.QuestWorldGatingForTests(now + attempt * 40 + 20);
            if (session.Quests!.Get(code)!.Goals[idx] > before) return true;
            if (session.Quests!.Get(code)!.Status >= QuestLog.Complete) return true;
        }
        return session.Quests!.Get(code)!.Goals[idx] > before;
    }

    static async Task<bool> UseQuestEnvAsync(GameSession session, int[] envIds, int count, int mapHint, long now)
    {
        var mapId = mapHint > 0 ? mapHint : session.MapId ?? GamePackets.SpawnMapId;
        if (session.MapId != mapId)
            await RefreshSpawnsOnMapAsync(session, mapId, now);
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        var used = false;
        foreach (var envId in envIds)
        {
            if (count <= 0) break;
            var index = objects.FindIndex(o => o.Env == envId);
            if (index < 0) continue;
            await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, now);
            var wireKey = WorldObjects.FirstKey + index;
            var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), now + 10);
            if (objects[index].InteractTime > 0)
                await session.TickAsync(now + objects[index].InteractTime + 60);
            used = use.Any(p => GameSessionTestHelpers.HasOpcode(p, 0x8d10))
                   || session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
            now += 80;
        }
        return used;
    }

    static async Task<bool> CollectFakeItemNpcsAsync(GameSession session, string code, int[] npcIds, int count, int mapHint, long now)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } record) return false;
        var step = session.Quests!.CurrentStep(record);
        if (step is null) return false;
        var idx = Array.FindIndex(step.Objectives, o => o.Type == "CollectFakeItem");
        if (idx < 0) return false;
        var before = record.Goals[idx];
        var attempts = 0;
        while (record.Goals[idx] < count && attempts < count * 12)
        {
            attempts++;
            if (!await KillQuestNpcsAsync(session, code, npcIds, 1, mapHint, now))
                break;
            now += 120;
            record = session.Quests!.Get(code)!;
            if (record.Goals[idx] > before) return true;
        }
        return session.Quests!.Get(code)!.Goals[idx] > before;
    }

    static int QuestMap(GameSession session, string code) =>
        session.Quests!.Book.Find(code) is { Map: var m } && m > 0 ? m : session.MapId ?? GamePackets.SpawnMapId;

    static async Task<bool> EnterQuestVolumeAsync(GameSession session, string code, QuestObjective o, long now)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } before) return false;
        var stepBefore = session.Quests!.CurrentStep(before);
        var idx = stepBefore is null ? -1 : Array.FindIndex(stepBefore.Objectives, obj => ReferenceEquals(obj, o));
        var goalBefore = idx >= 0 ? before.Goals[idx] : 0;
        var name = o.Target ?? o.Type;
        var mapId = o.Value is > 0 ? o.Value.Value
            : SubzoneToMap.Value.TryGetValue(name, out var subMap) ? subMap
            : QuestMap(session, code);
        if (session.MapId != mapId)
        {
            if (mapId == 200005 && MapEventScenarioHelpers.TeleportPoint("Dungeon_0005_StartPoint") is { } ascAt)
            {
                session.Teleport(200005, ascAt, now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
            }
            else
                await RefreshSpawnsOnMapAsync(session, mapId, now);
        }
        var at = MapEventScenarioHelpers.TriggerOrSubzonePosition(mapId, name);
        await MapEventScenarioHelpers.SyncPositionAsync(session, at, now);
        await session.TickAsync(now + 50);
        _ = session.QuestWorldGatingForTests(now + 60);
        var after = session.Quests!.Get(code);
        if (after is null || after.Status >= QuestLog.Complete) return true;
        if (idx >= 0 && after.Goals[idx] > goalBefore) return true;
        return after.Step > before.Step;
    }

    static async Task<bool> SyncCollectItemAsync(GameSession session, FakeCharacterStore store, int itemId, int count, long now)
    {
        var held = store.Items.Where(i => i.TemplateId == itemId).Sum(i => i.Quantity);
        if (held < count)
        {
            var empty = JsonSerializer.SerializeToElement(new { });
            store.Items.Add(new ItemState(Guid.NewGuid(), "b", itemId, count - held, "bag", null, 1, empty, empty, 8000 + store.Items.Count));
        }
        await session.InventoryChangesAsync(default);
        return session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
    }

    static async Task<bool> TryEnterMapEventDungeonAsync(GameSession session, string eventName, int race, long now)
    {
        if (MapEventTables.Embedded.Event(eventName) is not { Map: var eventMap }) return false;
        if (session.MapId == eventMap && session.InstanceKey != 0) return true;

        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(eventMap);
        var fieldMaps = new[] { session.MapId ?? 0, race == 2 ? 1100 : 1000, 1010, 1110, race == 2 ? 1100 : 1000 };
        foreach (var baseMap in fieldMaps.Where(m => m > 0).Distinct())
        {
            foreach (var layer in MapEventTables.Embedded.WalkInOn(baseMap).ToArray())
            {
                if (layer.LayerMap != eventMap) continue;
                if (session.MapId != baseMap)
                {
                    session.Teleport(baseMap, GameSessionTestHelpers.MapStartOrDefault(baseMap), now);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
                }
                var cx = layer.Volume.Points.Average(p => p[0]);
                var cy = layer.Volume.Points.Average(p => p[1]);
                var z = (layer.Volume.ZMin + layer.Volume.ZMax) / 2;
                var outside = new Vector3d(layer.XMin - 800, cy, z);
                var inside = new Vector3d(cx, cy, z);
                await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), now + 20);
                await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(inside), now + 400);
                if (session.MapId != eventMap && session.InstanceKey == 0)
                {
                    _ = session.EnterDungeonById(layer.DungeonId, now + 410);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 420);
                }
                if (session.MapId == eventMap || session.InstanceKey != 0) return true;
            }
        }
        if (dungeonId > 0)
        {
            foreach (var mapId in fieldMaps.Where(m => m > 0).Distinct())
            {
                var objects = WorldObjects.ObjectsOn(mapId).ToList();
                var idx = objects.FindIndex(o => o.Portal?.Contains(dungeonId) == true);
                if (idx < 0) continue;
                await MapEventScenarioHelpers.SyncPositionAsync(session, objects[idx].At, now);
                var wire = WorldObjects.FirstKey + idx;
                await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wire).ToArray(), now + 10);
                if (objects[idx].InteractTime > 0)
                    await session.TickAsync(now + objects[idx].InteractTime + 250);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 300);
                if (session.MapId == eventMap || session.InstanceKey != 0) return true;
            }
        }

        foreach (var mapId in fieldMaps.Where(m => m > 0).Distinct())
        {
            foreach (var entry in MapEventTables.Embedded.EntriesOn(mapId))
            {
                var dMap = MapEventTables.Embedded.Dungeon(entry.Dungeon)?.Map ?? 0;
                if (!MapEventTables.Embedded.EventsOn(dMap).Any(e => string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase))) continue;
                var at = new Vector3d(entry.Center[0], entry.Center[1], entry.Center[2]);
                await MapEventScenarioHelpers.SyncPositionAsync(session, at, now + 20);
                await session.TickAsync(now + 120);
                if (session.MapId == eventMap || session.InstanceKey != 0) return true;
            }
        }

        if (MapEventTables.Embedded.Volume(eventMap) is { } vol)
        {
            var baseMap = vol.Base;
            var cx = vol.Points.Average(p => p[0]);
            var cy = vol.Points.Average(p => p[1]);
            var z = (vol.ZMin + vol.ZMax) / 2;
            var at = new Vector3d(cx, cy, z);
            if (session.MapId != baseMap || session.InstanceKey != 0)
            {
                session.Teleport(baseMap, GameSessionTestHelpers.MapStartOrDefault(baseMap), now + 40);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 45);
            }
            var outside = new Vector3d(cx - 4000, cy, z);
            session.Teleport(baseMap, outside, now + 50);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 55);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), now + 60);
            session.Teleport(baseMap, at, now + 65);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 70);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at), now + 400);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 410);
            await session.TickAsync(now + 500);
            return session.MapId == eventMap || session.InstanceKey != 0;
        }

        return false;
    }

    static async Task<bool> TryClearMapEventAsync(GameSession session, string questCode, string eventName, long now, int race)
    {
        now = NextMapEventHarnessNow();
        if (MapEventTables.Embedded.Event(eventName) is not { Map: var mapId }) return false;
        var questBefore = session.Quests!.Get(questCode);
        var goalsBefore = questBefore?.Goals.ToArray();
        var stepBefore = questBefore?.Step ?? 0;
        if (session.Quests!.Get(questCode) is { Status: QuestLog.Incomplete } qr
            && session.Quests!.CurrentStep(qr) is { } st)
        {
            var idx = Array.FindIndex(st.Objectives, o => o.Type == "ClearMapEvent" && o.Target == eventName);
            if (idx >= 0 && qr.Goals[idx] >= st.Objectives[idx].Count) return true;
        }

        if (eventName == "World_L_Starter_InstanceLayer_2_MapEvent")
        {
            if (session.InstanceKey != 0)
            {
                var home = race == 2 ? 1100 : 1000;
                session.Teleport(home, GameSessionTestHelpers.MapStartOrDefault(home), now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
                await session.TickAsync(now + 50);
            }
            var ok = await MapEventScenarioHelpers.PlayStarterInstanceLayer2Async(session, now);
            await MapEventScenarioHelpers.FlushQuestMapEventClearsAsync(session, now + 8000);
            if (ok) return true;
            if (session.Quests!.Get(questCode) is { Status: >= QuestLog.Complete }) return true;
            if (session.Quests!.Get(questCode) is { } layerQuest
                && session.Quests!.CurrentStep(layerQuest) is { } layerStep)
            {
                var idx = Array.FindIndex(layerStep.Objectives, o => o.Type == "ClearMapEvent" && o.Target == eventName);
                if (idx >= 0 && layerQuest.Goals[idx] >= layerStep.Objectives[idx].Count) return true;
            }
            return false;
        }

        if (eventName is "World_L_Starter_HQ004_MapEvent" or "World_D_Starter_HQ004_MapEvent"
            && session.MapId != mapId && MapEventTables.Embedded.Volume(mapId) is { } hq004Vol)
        {
            var cx = hq004Vol.Points.Average(p => p[0]);
            var cy = hq004Vol.Points.Average(p => p[1]);
            var z = (hq004Vol.ZMin + hq004Vol.ZMax) / 2;
            var outside = new Vector3d(cx - 4000, cy, z);
            var inside = new Vector3d(cx, cy, z);
            session.Teleport(hq004Vol.Base, outside, now + 5);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), now + 400);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(inside), now + 800);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 810);
            if (session.MapId != mapId)
            {
                var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(mapId);
                if (dungeonId > 0)
                {
                    session.EnterDungeonById(dungeonId, now + 820);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 830);
                }
            }
        }
        else if (eventName is "World_D_Starter_Layer_2_MapEvent" or "World_D_Starter_Layer_3_MapEvent"
                 && session.MapId != mapId && MapEventTables.Embedded.Volume(mapId) is { } layerVol)
        {
            var cx = layerVol.Points.Average(p => p[0]);
            var cy = layerVol.Points.Average(p => p[1]);
            var z = (layerVol.ZMin + layerVol.ZMax) / 2;
            var outside = new Vector3d(cx - 4000, cy, z);
            var inside = new Vector3d(cx, cy, z);
            session.Teleport(layerVol.Base, outside, now + 5);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), now + 400);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(inside), now + 800);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 810);
            if (session.MapId != mapId)
            {
                var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(mapId);
                if (dungeonId > 0)
                {
                    session.EnterDungeonById(dungeonId, now + 820);
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 830);
                }
            }
        }
        else if (eventName == "World_D_Starter_HQ001_MapEvent" && session.MapId != mapId && FindEntryNpcForEvent(eventName, session) is { } hq001Npc)
        {
            session.Teleport(1100, new Vector3d(18694, -10822, 6843), now + 5);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
            await RefreshSpawnsOnMapAsync(session, 1100, now + 12);
            _ = session.QuestWorldGatingForTests(now + 15);
            await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, hq001Npc, now + 20);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 30);
        }
        else if (eventName.StartsWith("World_D_A_Layer_HQ", StringComparison.Ordinal)
                 && session.MapId != mapId && FindEntryNpcForEvent(eventName, session) is { } altLayerNpc)
        {
            const int altgard = 1110;
            if (session.MapId != altgard)
            {
                session.Teleport(altgard, GameSessionTestHelpers.MapStartOrDefault(altgard), now + 5);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
            }
            await RefreshSpawnsOnMapAsync(session, altgard, now + 12);
            _ = session.QuestWorldGatingForTests(now + 15);
            await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, altLayerNpc, now + 20);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 30);
        }
        else if (session.MapId != mapId || session.InstanceKey == 0)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(mapId);
            if (dungeonId > 0)
            {
                await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, dungeonId, now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
                await session.TickAsync(now + 30);
            }
            if (session.MapId != mapId)
                await TryEnterMapEventDungeonAsync(session, eventName, race, now + 40);
        }

        if (session.MapId != mapId)
        {
            var starterMap = race == 2 ? 1100 : 1000;
            session.Teleport(starterMap, GameSessionTestHelpers.MapStartOrDefault(starterMap), now + 5);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
            await TryEnterMapEventDungeonAsync(session, eventName, race, now + 15);
        }

        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, eventName, now + 30, attempts: 16);
        if (run is null && FindEntryNpcForEvent(eventName, session) is { } entryNpc)
        {
            if (NpcSpawnAt(session.MapId ?? GamePackets.SpawnMapId, entryNpc) is { } robinAt)
            {
                await MapEventScenarioHelpers.SyncPositionAsync(session, robinAt, now + 35);
                await RefreshSpawnsOnMapAsync(session, session.MapId ?? GamePackets.SpawnMapId, now + 38);
                _ = session.QuestWorldGatingForTests(now + 40);
            }
            if (session.Targets.Values.Any(t => t.NpcId == entryNpc))
            {
                await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, entryNpc, now + 45);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 50);
                run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, eventName, now + 60, attempts: 16);
            }
        }

        if (run is null)
        {
            session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now + 70);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 80);
            run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, eventName, now + 90, attempts: 16);
        }

        if (run is null) return false;

        const int scriptSliceSeconds = 18;
        var resumeOnly = run.Status != MapEventRun.Complete && (run.StepOrder > 0 || run.StageIndex > 0);
        var scriptSeconds = eventName.Contains("Crematorium", StringComparison.Ordinal) ? 90 : Math.Max(scriptSliceSeconds, 60);
        var finished = eventName switch
        {
            "World_L_Starter_HQ001_MapEvent" when !resumeOnly => await MapEventScenarioHelpers.PlayLightHq001ScriptAsync(session, run, now + 100, null),
            "World_D_Starter_HQ001_MapEvent" when !resumeOnly => await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run, now + 100, null),
            "World_L_Starter_HQ004_MapEvent" => await MapEventScenarioHelpers.PlayLightHq004ScriptAsync(session, run, now + 100, null),
            "World_D_Starter_HQ004_MapEvent" => await MapEventScenarioHelpers.PlayDarkHq004ScriptAsync(session, run, now + 100, null),
            "World_D_Starter_HQ003_MapEvent" or "World_D_Starter_HQ005_MapEvent"
                or "World_D_Starter_Layer_2_MapEvent" or "World_D_Starter_Layer_3_MapEvent"
                or "World_L_A_Layer_TF_TentOfCochirun_MapEvent"
                => await MapEventScenarioHelpers.PlayStarterInstanceMapEventAsync(session, run, now + 100, null),
            _ when eventName.StartsWith("World_D_A_Layer_HQ", StringComparison.Ordinal)
                => await MapEventScenarioHelpers.PlayStarterInstanceMapEventAsync(session, run, now + 100, null),
            "Dungeon_D_0018_MapEvent" => await MapEventScenarioHelpers.PlayDungeonMapEventAsync(session, run, now + 100, null),
            _ when mapId >= 101030 && mapId < 102000
                     || eventName.Contains("Layer_HQ", StringComparison.OrdinalIgnoreCase)
                     || mapId >= 141000 || eventName.Contains("Garrision", StringComparison.OrdinalIgnoreCase)
                => await MapEventScenarioHelpers.PlayStarterInstanceMapEventAsync(session, run, now + 100, null),
            _ => await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, now + 100, maxSeconds: scriptSeconds, capture: null),
        };
        for (var flush = 0; flush < 24 && session.InstanceKey != 0; flush++)
            await session.TickAsync(now + 4000 + flush * 500);
        await MapEventScenarioHelpers.FlushQuestMapEventClearsAsync(session, now + 16_000);
        if (finished || run.Status == MapEventRun.Complete) return true;
        var questAfter = session.Quests!.Get(questCode);
        if (questAfter is null) return false;
        if (questAfter.Step > stepBefore || questAfter.Status >= QuestLog.Complete) return true;
        if (goalsBefore is not null)
        {
            for (var i = 0; i < questAfter.Goals.Length && i < goalsBefore.Length; i++)
                if (questAfter.Goals[i] > goalsBefore[i]) return true;
        }
        return false;
    }

    static int? FindEntryNpcForEvent(string eventName, GameSession session)
    {
        if (MapEventTables.Embedded.Event(eventName) is not { Map: var eventMap }) return null;
        foreach (var npc in session.Targets.Values)
        {
            if (MapEventTables.Embedded.EntryNpc(npc.NpcId) is not { } entry) continue;
            if (MapEventTables.Embedded.Dungeon(entry.Dungeon) is not { Map: var dMap }) continue;
            if (dMap == eventMap || MapEventTables.Embedded.EventsOn(dMap).Any(e => e.Name == eventName))
                return npc.NpcId;
        }
        return eventName switch
        {
            "World_L_Starter_HQ001_MapEvent" => 2930710,
            "World_D_Starter_HQ001_MapEvent" => 2951078,
            "World_D_A_Layer_HQ001_MapEvent" => 2950033,
            "World_D_A_Layer_HQ002_MapEvent" => 2950104,
            "World_D_A_Layer_HQ004_MapEvent" => 2950040,
            _ => null,
        };
    }

    static async Task RewardQuestViaPacketsAsync(GameSession session, string code, long now)
    {
        if (session.Quests!.Get(code) is { Status: QuestLog.Rewarded }) return;
        if (session.Quests!.Get(code)!.Status == QuestLog.Incomplete)
            await session.QuestCommandAsync("QuestComplete", code, default);
        var record = session.Quests!.Get(code)!;
        if (record.Status == QuestLog.Complete)
        {
            var select = session.Quests!.Book.Find(code)?.Rewards?.Select is { Length: > 0 } ? 0 : -1;
            await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(select).ToArray(), now);
        }
        if (session.Quests!.Get(code)!.Status != QuestLog.Rewarded)
            await session.QuestCommandAsync("QuestReward", code, default);
    }

    static int HeroQuestRecommendedLevel(string code)
    {
        if (QuestBook.Embedded.Find(code) is not { } q) return 0;
        return q.RecommendedLevel > 0 ? q.RecommendedLevel : q.Level;
    }

    static HeroChains LoadHeroChains()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hero-chains.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "hero-chains.json"));
        return JsonSerializer.Deserialize<HeroChains>(File.ReadAllText(path), JsonOptions)!;
    }

    sealed record HeroChains(string[] Light, string[] Dark);

    public sealed record ParityRow(string Category, int Attempted, int Completed, int Blocked, string TopBlockReason);

    public sealed record CapQuestParitySummary(
        int Race,
        int FinalLevel,
        int HeroRewarded,
        int UnifiedObjectiveScore,
        IReadOnlyList<ParityRow> Rows);

    const int CapLevel = 45;

    public const int SideCompletableAtCapPerFaction = 131;

    public const int SideUnavailableHeroGatePerFaction = 28;

    public static (int Completable, int UnavailableHeroGate) SideQuestCapCounts(int race)
    {
        var completable = 0;
        var unavailable = 0;
        foreach (var (code, quest) in QuestBook.Embedded.Quests)
        {
            if (quest.Type is not ("District" or "DutyMission") || quest.Level > CapLevel || quest.Steps.Length == 0)
                continue;
            if (!QuestBook.RaceMatches(quest, race))
                continue;
            if (quest.Unavailable)
                unavailable++;
            else
                completable++;
        }
        return (completable, unavailable);
    }

    public static async Task<CapQuestParitySummary> RunCapQuestParityAsync(int race, long now)
    {
        var (hero, session, store) = await RunHeroCampaignWithSessionAsync(race, now);
        var dataGaps = hero.DataGaps.ToList();
        var fallbacks = hero.PacketFallbacks.ToList();
        var t = now + 3600_000;
        await NudgeAutoAcquireAsync(session, race, t);

        var stats = new Dictionary<string, (int Attempted, int Completed, int Blocked, Dictionary<string, int> Reasons)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in new[] { "campaign", "side", "ascension", "daily_weekly", "region", "other" })
            stats[cat] = (0, 0, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        foreach (var code in CapQuestCodes(race).OrderBy(c => QuestBook.Embedded.Find(c)!.Level).ThenBy(c => c, StringComparer.Ordinal))
        {
            if (session.Quests!.Get(code) is { Status: QuestLog.Rewarded })
            {
                var cat0 = ParityCategory(session.Quests!.Book.Find(code)!);
                var s0 = stats[cat0];
                stats[cat0] = (s0.Attempted + 1, s0.Completed + 1, s0.Blocked, s0.Reasons);
                continue;
            }

            var def = session.Quests!.Book.Find(code)!;
            var cat = ParityCategory(def);
            var s = stats[cat];
            stats[cat] = (s.Attempted + 1, s.Completed, s.Blocked, s.Reasons);

            await TryAcceptQuestForParityAsync(session, code, race, t);
            t += 100;

            if (session.Quests!.Get(code) is null)
            {
                BumpReason(stats, cat, ParityBlockReason(session, code, def, race));
                continue;
            }

            var played = await PlayQuestToRewardForParityAsync(session, store, code, race, t, dataGaps, fallbacks);
            t = played.Time;
            if (played.Ok)
            {
                var s2 = stats[cat];
                stats[cat] = (s2.Attempted, s2.Completed + 1, s2.Blocked, s2.Reasons);
                await NudgeAutoAcquireAsync(session, race, t);
            }
            else
            {
                BumpReason(stats, cat, played.Reason ?? "play_failed");
            }
        }

        var rows = stats.Select(kv => new ParityRow(
            kv.Key,
            kv.Value.Attempted,
            kv.Value.Completed,
            kv.Value.Blocked,
            kv.Value.Reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} ({r.Value})").FirstOrDefault() ?? "—")).ToList();

        return new CapQuestParitySummary(race, session.Level, hero.QuestsRewarded, session.Quests!.UnifiedObjectiveScore(), rows);

        static void BumpReason(Dictionary<string, (int Attempted, int Completed, int Blocked, Dictionary<string, int> Reasons)> stats, string cat, string reason)
        {
            var s = stats[cat];
            s.Reasons.TryGetValue(reason, out var n);
            s.Reasons[reason] = n + 1;
            stats[cat] = (s.Attempted, s.Completed, s.Blocked + 1, s.Reasons);
        }
    }

    static string ParityCategory(QuestDefinition q) => q.Type switch
    {
        "Hero" => "campaign",
        "Ascension" => "ascension",
        "District" or "DutyMission" => "side",
        "DutyScroll" => "daily_weekly",
        "Exploration" => "region",
        _ => "other",
    };

    static IEnumerable<string> CapQuestCodes(int race)
    {
        var book = QuestBook.Embedded;
        foreach (var (code, quest) in book.Quests)
        {
            if (quest.Unavailable || quest.Level > CapLevel || quest.Steps.Length == 0) continue;
            if (!QuestBook.RaceMatches(quest, race)) continue;
            yield return code;
        }
    }

    static async Task NudgeAutoAcquireAsync(GameSession session, int race, long now)
    {
        var mapId = session.MapId ?? (race == 2 ? 1100 : 1000);
        session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await session.TickAsync(now + 15);
    }

    static string ClassifyFallback(GameSession session, string code, string bucket)
    {
        if (session.Quests!.Get(code) is not { Status: QuestLog.Incomplete } record)
            return $"{bucket}:complete_or_missing";
        if (session.Quests!.CurrentStep(record) is not { } step)
            return $"{bucket}:no_current_step";
        var parts = new List<string>();
        for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
        {
            var o = step.Objectives[i];
            if (o.Optional) continue;
            if (o.Type is "PCLevel") { if (session.Level < (o.Level ?? o.Count)) parts.Add($"PCLevel need {o.Level ?? o.Count}"); continue; }
            if (o.Type is "ClearQuest" or "ClearQuestType") { parts.Add($"{o.Type}:{o.Target}"); continue; }
            if (record.Goals[i] >= o.Count) continue;
            parts.Add($"{o.Type} {record.Goals[i]}/{o.Count} map {QuestMap(session, code)}");
        }
        return parts.Count == 0 ? $"{bucket}:unknown" : $"{bucket}:" + string.Join("; ", parts);
    }

    static CampaignQuestStep BuildCampaignStep(
        string code,
        GameSession session,
        FakeCharacterStore store,
        int levelBefore,
        long expBefore,
        IReadOnlyList<string> questFallbacks,
        string? devCompleteReason)
    {
        var def = QuestBook.Embedded.Find(code)!;
        var kinds = def.Steps.SelectMany(s => s.Objectives.Take(6).Select(o => o.Type)).Distinct(StringComparer.Ordinal).ToList();
        var mapEvent = kinds.Contains("ClearMapEvent", StringComparer.Ordinal);
        var celvice = kinds.Any(k => ClientOnlyObjectiveTypes.Contains(k));
        var instance = mapEvent || def.Steps.Any(s => s.Objectives.Any(o => o.Type == "ClearMapEvent"
            && MapEventTables.Embedded.Event(o.Target ?? "") is { Map: var em } && em != def.Map));
        var devFallback = devCompleteReason is not null || questFallbacks.Any(f => f.Equals($"{code}: QuestComplete", StringComparison.Ordinal));
        var expGain = Math.Max(0, store.Progress.Exp - expBefore);
        if (expGain == 0 && store.Rewards.LastOrDefault(r => r.Code == code) is { Exp: var rew })
            expGain = rew;
        return new CampaignQuestStep(code, def.Level, def.Map, session.Level, expGain, kinds, mapEvent, instance, celvice, devFallback, devCompleteReason);
    }

    public sealed record CategoryPlaythroughSummary(
        int Race,
        string Category,
        int Attempted,
        int Rewarded,
        int Failed,
        IReadOnlyList<string> Failures);

    public sealed record MultiCategoryPlaythroughSummary(
        int Race,
        IReadOnlyList<CategoryPlaythroughSummary> Categories);

    /// <summary>Hero to cap once, then packet playthrough for each listed category (side / region / ascension).</summary>
    public static async Task<MultiCategoryPlaythroughSummary> RunCapCategoriesPlaythroughAsync(int race, IReadOnlyList<string> categories, long now)
    {
        var (hero, session, store) = await RunHeroCampaignWithSessionAsync(race, now);
        var dataGaps = hero.DataGaps.ToList();
        var fallbacks = hero.PacketFallbacks.ToList();
        var t = now + 3600_000;
        var want = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        var tallies = categories.ToDictionary(c => c, _ => (Attempted: 0, Rewarded: 0, Failures: new List<string>()), StringComparer.OrdinalIgnoreCase);

        foreach (var code in CapQuestCodes(race).OrderBy(c => QuestBook.Embedded.Find(c)!.Level).ThenBy(c => c, StringComparer.Ordinal))
        {
            var def = session.Quests!.Book.Find(code)!;
            var cat = ParityCategory(def);
            if (!want.Contains(cat)) continue;

            var tally = tallies[cat];
            tally.Attempted++;
            if (session.Quests.Get(code) is { Status: QuestLog.Rewarded })
            {
                tally.Rewarded++;
                tallies[cat] = tally;
                continue;
            }

            await TryAcceptQuestForParityAsync(session, code, race, t);
            t += 100;
            if (session.Quests.Get(code) is null)
            {
                tally.Failures.Add($"{code}: {ParityBlockReason(session, code, def, race)}");
                tallies[cat] = tally;
                continue;
            }

            var played = await PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks);
            t = played.Time;
            if (played.Ok && session.Quests.Get(code)!.Status == QuestLog.Rewarded)
                tally.Rewarded++;
            else
                tally.Failures.Add($"{code}: {played.Reason ?? "not rewarded"}");
            tallies[cat] = tally;
        }

        var rows = categories.Select(c =>
        {
            var (attempted, rewarded, failures) = tallies[c];
            return new CategoryPlaythroughSummary(race, c, attempted, rewarded, attempted - rewarded, failures);
        }).ToList();
        return new MultiCategoryPlaythroughSummary(race, rows);
    }

    /// <summary>Hero to cap, then every quest in one parity category through packet objectives and reward.</summary>
    public static async Task<CategoryPlaythroughSummary> RunCapCategoryPlaythroughAsync(int race, string category, long now) =>
        (await RunCapCategoriesPlaythroughAsync(race, [category], now)).Categories[0];

    /// <summary>Hero to cap, then packet playthrough for an explicit quest code list (completable at cap only).</summary>
    public static async Task<(int Rewarded, IReadOnlyList<string> Failures)> RunQuestCodesPlaythroughAsync(
        int race, IReadOnlyList<string> codes, long now)
    {
        var (_, session, store) = await RunHeroCampaignWithSessionAsync(race, now);
        var dataGaps = new List<string>();
        var fallbacks = new List<string>();
        var t = now + 3600_000;
        var failures = new List<string>();
        var rewarded = 0;

        foreach (var code in codes)
        {
            if (session.Quests!.Book.Find(code) is not { } def || def.Unavailable)
            {
                failures.Add($"{code}: skipped (missing or unavailable)");
                continue;
            }
            if (session.Quests.Get(code) is { Status: QuestLog.Rewarded })
            {
                rewarded++;
                continue;
            }
            await TryAcceptQuestForParityAsync(session, code, race, t);
            t += 100;
            if (session.Quests.Get(code) is null)
            {
                failures.Add($"{code}: {ParityBlockReason(session, code, def, race)}");
                continue;
            }
            var played = await PlayQuestToRewardAsync(session, store, code, race, t, dataGaps, fallbacks);
            t = played.Time;
            if (played.Ok && session.Quests.Get(code)!.Status == QuestLog.Rewarded)
                rewarded++;
            else
                failures.Add($"{code}: {played.Reason ?? "not rewarded"}");
        }

        return (rewarded, failures);
    }
}
