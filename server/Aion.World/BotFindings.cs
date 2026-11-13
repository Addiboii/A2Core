using System.Text;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// The bots' findings as a page for the development session (logs/bot-findings.md): the stuck/refusal/error reports of
/// logs/bot-reports.jsonl grouped by what failed - the reason, the quest and its objective (or the map event's step) - with how often,
/// how many bots, the maps, a sample position and the server's reply. Newest group first, so fresh content bugs are on top.
/// </summary>
public static class BotFindings
{
    sealed class Group
    {
        public required string Reason, Quest, Title, What;
        public int Count;
        public readonly HashSet<string> Bots = [];
        public readonly HashSet<string> Maps = [];
        public string First = "", Last = "", Sample = "", Reply = "", Event = "";
    }

    public static string Summarise(IEnumerable<string> lines, DateTime nowUtc)
    {
        var groups = new Dictionary<string, Group>();
        var total = 0;
        foreach (var line in lines)
        {
            if (line.Length < 2) continue;
            JsonElement r;
            try { r = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }
            total++;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText() : "";
            var reason = S("reason");
            var quest = S("quest");
            var what = "";
            if (r.TryGetProperty("mapEvent", out var ev) && ev.ValueKind == JsonValueKind.Object)
                what = $"event {Str(ev, "name")} stage {Str(ev, "stage")} step {Str(ev, "step")}: {string.Join("; ", Arr(ev, "open").Take(3))}";
            else if (r.TryGetProperty("objectives", out var objs) && objs.ValueKind == JsonValueKind.Array && objs.GetArrayLength() > 0)
                what = string.Join("; ", objs.EnumerateArray().Take(2).Select(o => $"{Str(o, "Type")} {Str(o, "Target")}{(Str(o, "Npc") is { Length: > 0 } n ? " npc " + n : "")}".Trim()));
            var key = reason + "|" + quest + "|" + what;
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = new Group { Reason = reason, Quest = quest, Title = S("title"), What = what, First = S("time") };
            g.Count++;
            g.Bots.Add(S("bot"));
            g.Maps.Add(S("map"));
            g.Last = S("time");
            g.Sample = $"{S("bot")} lv {S("level")} map {S("map")} at {S("at")}" + (S("detail") is { Length: > 0 } d ? " - " + Cut(d, 300) : "");
            if (S("reply") is { Length: > 0 } reply) g.Reply = Cut(reply, 300);
        }
        var page = new StringBuilder();
        page.AppendLine("# Bot findings").AppendLine();
        page.AppendLine($"Rebuilt {nowUtc:yyyy-MM-dd HH:mm} UTC from logs/bot-reports.jsonl: {total} reports in {groups.Count} groups. Newest first. ")
            .AppendLine("Each group is one thing the bots could not get past (a content or server bug, or a bot limit); fix the top ones first.").AppendLine();
        page.AppendLine("| last seen | count | bots | reason | quest | objective / event step |").AppendLine("|---|---|---|---|---|---|");
        var ordered = groups.Values.OrderByDescending(g => g.Last, StringComparer.Ordinal).ThenByDescending(g => g.Count).ToList();
        foreach (var g in ordered.Take(80))
            page.AppendLine($"| {g.Last} | {g.Count} | {g.Bots.Count} | {Md(g.Reason)} | {Md(g.Quest)} {Md(g.Title)} | {Md(Cut(g.What, 160))} |");
        page.AppendLine().AppendLine("## Details (most reported first)").AppendLine();
        foreach (var g in groups.Values.OrderByDescending(g => g.Count).Take(40))
        {
            page.AppendLine($"### {g.Count}x {g.Reason} - {g.Quest} {g.Title}".TrimEnd());
            if (g.What.Length > 0) page.AppendLine($"- objective: {g.What}");
            page.AppendLine($"- bots: {g.Bots.Count}, maps: {string.Join(", ", g.Maps.Where(m => m.Length > 0))}, first {g.First}, last {g.Last}");
            page.AppendLine($"- sample: {g.Sample}");
            if (g.Reply.Length > 0) page.AppendLine($"- server reply: `{g.Reply}`");
            page.AppendLine();
        }
        return page.ToString();
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null => "", _ => v.GetRawText() } : "";

    static IEnumerable<string> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.ToString()) : [];

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    static string Md(string s) => s.Replace("|", "/").Replace("\n", " ");
}
