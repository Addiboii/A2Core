using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Starter zones (Poeta / Ishalgen): hero through <paramref name="maxLevel"/> plus side list from tools/data/starter-playthrough.json.
/// </summary>
static class StarterPlaythroughHelpers
{
    public sealed record StarterReport(
        int Race,
        int FinalLevel,
        int HeroQuestsPlanned,
        int HeroQuestsRewarded,
        int SideQuestsPlanned,
        int SideQuestsRewarded,
        string? BlockedQuestCode,
        string? BlockReason,
        IReadOnlyList<string> QuestResults,
        IReadOnlyList<string> DataGaps,
        IReadOnlyList<string> PacketFallbacks);

    public static async Task<StarterReport> RunStarterZoneAsync(int race, int maxLevel, long now)
    {
        var heroCodes = HeroCodesThroughLevel(race, maxLevel);
        var sideCodes = SideCodesFromData(race);
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race, level: 1, trainingDummy: false);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now + 2);
        var opening = race == 2 ? DevelopmentDefaults.OpeningQuestDark : DevelopmentDefaults.OpeningQuestLight;
        Assert.Contains(store.Quests, q => q.Code == opening && q.Status == QuestLog.Rewarded);

        var dataGaps = new List<string>();
        var fallbacks = new List<string>();
        var results = new List<string>();
        var t = now;
        var heroRewarded = 0;
        string? blocked = null;
        string? blockReason = null;

        foreach (var code in heroCodes.Skip(1))
        {
            var played = await CampaignPlaythroughHelpers.PlayQuestToRewardForStarterAsync(
                session, store, code, race, t, dataGaps, fallbacks);
            t = played.Time;
            if (!played.Ok)
            {
                blocked = code;
                blockReason = played.Reason;
                results.Add($"FAIL hero {code}: {played.Reason}");
                break;
            }
            heroRewarded++;
            results.Add($"OK hero {code} lvl {session.Level}");
        }

        var sideRewarded = 0;
        if (blocked is null && sideCodes.Length > 0)
        {
            if (session.MapId != 1110 && race == 2)
            {
                session.Teleport(1110, GameSessionTestHelpers.MapStartOrDefault(1110), t);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 5);
            }

            foreach (var code in sideCodes)
            {
                if (session.Quests!.Get(code) is { Status: QuestLog.Rewarded })
                {
                    sideRewarded++;
                    results.Add($"OK side {code} (already rewarded)");
                    continue;
                }

                await CampaignPlaythroughHelpers.NudgeAutoAcquireForStarterAsync(session, race, t);
                await CampaignPlaythroughHelpers.TryAcceptQuestForStarterAsync(session, code, race, t + 10);
                t += 30;

                if (session.Quests!.Get(code) is null && session.Quests!.Book.Find(code) is { } def)
                {
                    foreach (var c in def.Conditions ?? [])
                    {
                        if (c.Type is "BlacksmithingMastery" or "TailoringMastery" or "JewelcraftingMastery"
                            or "AlchemyMastery" or "CookingMastery" or "ExtractForceMastery" or "ExtractOdMastery")
                            await session.SetMasteryAsync("gather", 0, 50, default);
                    }
                    await CampaignPlaythroughHelpers.TryAcceptQuestForStarterAsync(session, code, race, t);
                    t += 20;
                }

                var played = await CampaignPlaythroughHelpers.PlayQuestToRewardForStarterAsync(
                    session, store, code, race, t, dataGaps, fallbacks);
                t = played.Time;
                if (!played.Ok)
                {
                    blocked = code;
                    blockReason = played.Reason;
                    results.Add($"FAIL side {code}: {played.Reason}");
                    break;
                }
                sideRewarded++;
                results.Add($"OK side {code} lvl {session.Level}");
            }
        }

        return new StarterReport(
            race,
            session.Level,
            heroCodes.Count - 1,
            heroRewarded,
            sideCodes.Length,
            sideRewarded,
            blocked,
            blockReason,
            results,
            dataGaps,
            fallbacks);
    }

    static List<string> HeroCodesThroughLevel(int race, int maxLevel)
    {
        var chain = LoadHeroChains();
        var codes = (race == 2 ? chain.Dark : chain.Light).ToList();
        var book = QuestBook.Embedded;
        var outList = new List<string>();
        foreach (var code in codes)
        {
            if (book.Find(code) is not { } q) continue;
            var lvl = q.RecommendedLevel > 0 ? q.RecommendedLevel : q.Level;
            if (outList.Count > 0 && lvl > maxLevel) break;
            outList.Add(code);
        }
        return outList;
    }

    static string[] SideCodesFromData(int race)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "starter-playthrough.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "starter-playthrough.json"));
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var key = race == 2 ? "dark" : "light";
        if (!doc.RootElement.TryGetProperty(key, out var sideEl)) return [];
        if (!sideEl.TryGetProperty("side_quest_order", out var arr)) return [];
        return arr.EnumerateArray().Select(e => e.GetString()!).Where(s => !string.IsNullOrEmpty(s)).ToArray();
    }

    static HeroChains LoadHeroChains()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hero-chains.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "hero-chains.json"));
        return JsonSerializer.Deserialize<HeroChains>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    sealed record HeroChains(string[] Light, string[] Dark);
}
