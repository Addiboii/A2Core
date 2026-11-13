// Stage 1 mask learner (C# port of tools/wire_probe.py MaskLearner; the Python one segfaulted under CPython 3.14.4 here).
//   python3 tools/wire_export.py export.json
//   dotnet run --project tools/WireLearn -- export.json tools/data/wire-probe.json [--budget N] [--recheck]
// For every official opcode the plain probe did not fit, each candidate struct (dev opcode = live - offset) is searched:
// the super class's fields, then a u8 or u16 presence mask, then the struct's own fields, each mandatory or optional (the next
// mask bit) and, for IntProperty, i32 or varint. A plan is accepted only when EVERY sample decodes to its last byte.
// Results are merged into wire-probe.json as "mask": {"mask_width", "fields": [[name, "mandatory"|"optional", enc|null]]}.
// --recheck also searches opcodes the plain probe fitted and reports where a mask reading also fits (a possible misreading,
// like TargetUpdate 0x3835: key | 00 | target).
using System.Text.Json;
using System.Text.Json.Nodes;

var export = JsonNode.Parse(File.ReadAllText(args[0]))!;
var probePath = args[1];
var probe = JsonNode.Parse(File.ReadAllText(probePath))!.AsObject();
var budget = args.Contains("--budget") ? int.Parse(args[Array.IndexOf(args, "--budget") + 1]) : 200_000;
var recheck = args.Contains("--recheck");
var showPlans = args.Contains("--plans") ? args[Array.IndexOf(args, "--plans") + 1] : null;
var structs = export["structs"]!.AsObject();
var opcodes = export["opcodes"]!.AsObject().ToDictionary(p => int.Parse(p.Key), p => (string)p.Value!);
var offsets = export["offsets"]!.AsArray().Select(o => (int)o!).ToArray();
var rules = new Rules(structs);
int learned = 0, alternatives = 0;
foreach (var (op, bodiesNode) in export["bodies"]!.AsObject())
{
    var entry = probe[op]?.AsObject();
    var fitted = entry?["struct"] is not null && entry["struct"]!.GetValueKind() == JsonValueKind.String;
    if (fitted && !recheck) continue;
    var blobs = bodiesNode!.AsArray().Select(b => Convert.FromHexString((string)b!)).ToList();
    var ourBlobs = export["ours"]?[op]?.AsArray().Select(b => Convert.FromHexString((string)b!)).ToList() ?? [];
    var live = Convert.ToInt32(op, 16);
    foreach (var off in offsets)
    {
        if (!opcodes.TryGetValue(live - off, out var name)) continue;
        if (name.EndsWith("_RQ", StringComparison.Ordinal)) continue;   // a client request is never sent by the server
        if (showPlans == op)
        {
            foreach (var alt in new MaskLearner(rules, budget).All(name, blobs, 8)) Console.WriteLine($"plan {op} {name}: {alt}");
            continue;
        }
        var plan = new MaskLearner(rules, budget).Learn(name, blobs, ourBlobs);
        if (plan is null) continue;
        if (fitted)
        {
            if (plan.Fields.Any(f => f.How == "optional") || plan.Width > 0)
            {
                alternatives++;
                Console.WriteLine($"recheck {op}: plain fit {entry!["struct"]}, also fits with a mask: {name} {plan}");
            }
            break;
        }
        learned++;
        Console.WriteLine($"learned {op}: {name.Replace("DevPacketData_GameServer_", "")} {plan}" + (plan.Ambiguous ? "  AMBIGUOUS (another plan also fits; not generated)" : ""));
        entry ??= new JsonObject();
        entry["struct"] = name.Replace("DevPacketData_GameServer_", "");
        entry["offset"] = "0x" + off.ToString("x");
        entry["mask"] = new JsonObject
        {
            ["mask_width"] = plan.Width,
            ["ambiguous"] = plan.Ambiguous,
            ["fields"] = new JsonArray(plan.Fields.Select(f => (JsonNode)new JsonArray(f.Name, f.How, f.Enc)).ToArray()),
        };
        probe[op] = entry;
        break;
    }
}
if (!recheck) File.WriteAllText(probePath, probe.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
var total = probe.Count(p => p.Value?["struct"] is JsonValue);
Console.WriteLine($"{learned} message types learned with presence masks; {total} of {probe.Count} fit" + (recheck ? $"; {alternatives} plain fits also read with a mask" : ""));

sealed class Fail : Exception { }

sealed class Reader(byte[] b, int i = 2)
{
    public readonly byte[] B = b;
    public int I = i, BoolAt = -1, Bits = 8;
    public Reader Copy() => new(B, I) { BoolAt = BoolAt, Bits = Bits };
    public bool End => I == B.Length;
    public void Take(int n) { if (n < 0 || I + n > B.Length) throw new Fail(); I += n; }
    public byte U8() { Take(1); return B[I - 1]; }
    public int Uint(int width) { Take(width); var v = 0; for (var k = 0; k < width; k++) v |= B[I - width + k] << (8 * k); return v; }
    public ulong Varint()
    {
        ulong n = 0; var s = 0;
        while (true)
        {
            var x = U8();
            n |= (ulong)(x & 0x7f) << s;
            s += 7;
            if ((x & 0x80) == 0) { if (s > 7 && x == 0) throw new Fail(); return n; }
            if (s > 63) throw new Fail();
        }
    }
    // A packed bool byte may not carry bits no bool explains (re-encoding would lose them: 0x3726's 04/05, caught by the golden test).
    public bool BoolClean => BoolAt < 0 || Bits >= 8 || (B[BoolAt] >> Bits) == 0;
    public void Bool()
    {
        if (Bits == 8 || BoolAt < 0) { if (!BoolClean) throw new Fail(); BoolAt = I; Take(1); Bits = 0; }
        Bits++;
    }
}

sealed class Rules(JsonObject structs)
{
    readonly Dictionary<string, List<(string Name, JsonNode Type)>> props = [];

    public bool Has(string? name) => name is not null && structs.ContainsKey(name);

    public JsonNode? Struct(string name) => structs[name];

    public List<(string Name, JsonNode Type)> Props(string name)
    {
        if (props.TryGetValue(name, out var cached)) return cached;
        var st = structs[name] ?? throw new Fail();
        var list = new List<(string, JsonNode)>();
        var sup = (string?)st["super"];
        if (Has(sup) && sup != name) list.AddRange(Props(sup!));
        list.AddRange(st["fields"]!.AsArray().Select(f => ((string)f!["name"]!, f["type"]!)));
        return props[name] = list;
    }

    public static bool VarintName(string name) => name != "_result" && name.EndsWith("key", StringComparison.OrdinalIgnoreCase);

    public void Value(Reader r, JsonNode t, string name, int depth)
    {
        if (depth > 12) throw new Fail();
        var kind = (string)t["type"]!;
        var inner = t["inner"];
        switch (kind)
        {
            case "IntProperty":
                if (name == "_result") r.Take(2);
                else if (VarintName(name)) r.Varint();
                else r.Take(4);
                return;
            case "UInt32Property": r.Take(4); return;
            case "UInt16Property": r.Take(2); return;
            case "Int64Property": r.Take(8); return;
            case "FloatProperty": r.Take(4); return;
            case "ByteProperty": r.Take(1); return;
            case "BoolProperty": r.Bool(); return;
            case "EnumProperty":
                r.Take((string?)inner?["type"] switch { "UInt16Property" => 2, "IntProperty" or "Int32Property" => 4, _ => 1 });
                return;
            case "StrProperty":
            {
                var n = r.Varint();
                if (n > 4096) throw new Fail();
                r.Take((int)n);
                return;
            }
            case "StructProperty": Struct(r, (string)t["struct"]!, depth + 1); return;
            case "ArrayProperty":
            {
                var n = r.Varint();
                if (n > 20000) throw new Fail();
                for (var k = 0ul; k < n; k++)
                {
                    if ((string?)inner?["type"] == "StructProperty") Struct(r, (string)inner!["struct"]!, depth + 1);
                    else Value(r, inner!, name, depth + 1);
                }
                return;
            }
            case "MapProperty":
            {
                var n = r.Varint();
                if (n > 20000) throw new Fail();
                for (var k = 0ul; k < n; k++) { Value(r, inner!, name, depth + 1); Value(r, t["value"]!, name, depth + 1); }
                return;
            }
            default: throw new Fail();
        }
    }

    public void Struct(Reader r, string name, int depth) { foreach (var (n, t) in Props(name)) Value(r, t, n, depth); }
}

sealed record Plan(int Width, List<(string Name, string How, string? Enc)> Fields, bool Ambiguous = false)
{
    public override string ToString() => $"u{Width * 8} mask, " + string.Join(", ", Fields.Select(f => f.Name + (f.How == "optional" ? "?" : "") + (f.Enc is null ? "" : ":" + f.Enc)));
}

sealed class MaskLearner(Rules rules, int budget)
{
    const int MaxPlans = 64;
    int steps;

    public Plan? Learn(string name, List<byte[]> all, List<byte[]>? ours = null)
    {
        if (!rules.Has(name)) return null;
        var st = rules.Struct(name)!;
        var own = st["fields"]!.AsArray().Select(f => ((string)f!["name"]!, f["type"]!)).ToList();
        var sup = (string?)st["super"];
        var supProps = rules.Has(sup) ? rules.Props(sup!) : [];
        var search = all.OrderBy(b => b.Length).Take(12).ToList();
        foreach (var width in new[] { 1, 2 })
        {
            var start = Start(search, supProps, width);
            if (start is null) continue;
            steps = 0;
            if (Start(all, supProps, width) is not { } everyone) continue;
            var found = new List<List<(string, string, string?)>>();
            foreach (var plan in Walk(own, start))
            {
                if (!Replay(own, plan, Start(all, supProps, width)!)) continue;
                found.Add(plan);
                if (found.Count == MaxPlans) break;
            }
            if (found.Count == 0) continue;
            var cut = steps > budget || found.Count == MaxPlans;   // the search did not see every plan
            // Our own accepted bodies break ties: keep the plans that also decode them (none does: keep all).
            if (found.Count > 1 && ours is { Count: > 0 } && Start(ours, supProps, width) is { } mine)
            {
                var agree = found.Where(p => Replay(own, p, Start(ours, supProps, width)!)).ToList();
                if (agree.Count > 0) found = agree;
            }
            if (found.Count == 1) return new Plan(width, found[0], cut);
            // Tie-break: a server does not mark a field optional that it sends every time, so the plan with the fewest optional
            // fields whose bit is set in EVERY sample wins, then the one with the fewest optional fields. Still tied: ambiguous.
            var masks = everyone.Select(x => x.Mask).ToList();
            (int AlwaysSet, int Optional) Score(List<(string, string, string?)> plan)
            {
                int bit = 0, always = 0, optional = 0;
                foreach (var (_, how, _) in plan)
                {
                    if (how != "optional") continue;
                    optional++;
                    if (masks.All(m => ((m >> bit) & 1) != 0)) always++;
                    bit++;
                }
                return (always, optional);
            }
            var ranked = found.Select(p => (Plan: p, Score: Score(p))).OrderBy(x => x.Score.AlwaysSet).ThenBy(x => x.Score.Optional).ToList();
            var tied = ranked.Count > 1 && ranked[0].Score == ranked[1].Score;
            if (Environment.GetEnvironmentVariable("WIRELEARN_WHY") is not null)
                Console.WriteLine($"  why {name}: plans {found.Count}{(cut ? " (cut)" : "")}, best {ranked[0].Score}, second {ranked[1].Score}");
            return new Plan(width, ranked[0].Plan, tied || cut);
        }
        return null;
    }

    public IEnumerable<Plan> All(string name, List<byte[]> all, int max)
    {
        if (!rules.Has(name)) yield break;
        var st = rules.Struct(name)!;
        var own = st["fields"]!.AsArray().Select(f => ((string)f!["name"]!, f["type"]!)).ToList();
        var sup = (string?)st["super"];
        var supProps = rules.Has(sup) ? rules.Props(sup!) : [];
        var n = 0;
        foreach (var width in new[] { 1, 2 })
        {
            if (Start(all, supProps, width) is not { } start) continue;
            steps = 0;
            foreach (var plan in Walk(own, start))
            {
                yield return new Plan(width, plan);
                if (++n >= max) yield break;
            }
        }
    }

    List<(Reader R, int Mask)>? Start(List<byte[]> blobs, List<(string Name, JsonNode Type)> sup, int width)
    {
        var list = new List<(Reader, int)>();
        try
        {
            foreach (var b in blobs)
            {
                var r = new Reader(b);
                foreach (var (n, t) in sup) rules.Value(r, t, n, 0);
                list.Add((r, r.Uint(width)));
            }
        }
        catch (Fail) { return null; }
        return list;
    }

    static List<(bool Optional, string? Enc)> Choices(string name, JsonNode t) =>
        (string)t["type"]! == "IntProperty" && name != "_result" && !Rules.VarintName(name)
            ? [(false, "i32"), (false, "varint"), (true, "i32"), (true, "varint")]
            : [(false, null), (true, null)];

    List<(Reader R, int Mask)>? Step(string name, JsonNode t, bool optional, string? enc, List<(Reader R, int Mask)> readers, int bit)
    {
        var next = new List<(Reader, int)>(readers.Count);
        foreach (var (rd, mask) in readers)
        {
            var r = rd.Copy();
            if (!optional || ((mask >> bit) & 1) != 0)
            {
                try
                {
                    if (enc == "varint") r.Varint();
                    else if (enc == "i32") r.Take(4);
                    else rules.Value(r, t, name, 1);
                }
                catch (Fail) { return null; }
            }
            next.Add((r, mask));
        }
        return next;
    }

    IEnumerable<List<(string, string, string?)>> Walk(List<(string Name, JsonNode Type)> fields, List<(Reader R, int Mask)> start)
    {
        var stack = new Stack<(int I, List<(Reader R, int Mask)> Readers, int Bit, List<(string, string, string?)> Plan, int Choice)>();
        stack.Push((0, start, 0, [], 0));
        while (stack.Count > 0)
        {
            if (++steps > budget) yield break;
            var (i, readers, bit, plan, c) = stack.Pop();
            if (i == fields.Count)
            {
                // every sample consumed, and no mask bit set beyond the optional fields
                if (readers.All(x => x.R.End && x.R.BoolClean && (x.Mask >> bit) == 0)) yield return plan;
                continue;
            }
            var (name, type) = fields[i];
            var options = Choices(name, type);
            if (c >= options.Count) continue;
            stack.Push((i, readers, bit, plan, c + 1));
            var (optional, enc) = options[c];
            if (optional && bit >= (readers[0].Mask >= 0 ? 16 : 0)) continue;
            var next = Step(name, type, optional, enc, readers, bit);
            if (next is not null)
                stack.Push((i + 1, next, bit + (optional ? 1 : 0), [.. plan, (name, optional ? "optional" : "mandatory", enc)], 0));
        }
    }

    bool Replay(List<(string Name, JsonNode Type)> fields, List<(string Name, string How, string? Enc)> plan, List<(Reader R, int Mask)> readers)
    {
        var bit = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            var optional = plan[i].How == "optional";
            var next = Step(fields[i].Name, fields[i].Type, optional, plan[i].Enc, readers, bit);
            if (next is null) return false;
            readers = next;
            if (optional) bit++;
        }
        return readers.All(x => x.R.End && x.R.BoolClean && (x.Mask >> bit) == 0);
    }
}
