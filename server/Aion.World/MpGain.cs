using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>PROVISIONAL MP gain on hits and natural MP regeneration (gamedata/provisional/mp-gain.xml), loaded once.</summary>
public sealed class MpGain
{
    readonly Dictionary<string, int> perCast;
    MpGain(Dictionary<string, int> perCast, int fallback, int regen) => (this.perCast, Default, RegenPerSecond) = (perCast, fallback, regen);

    public int Default { get; }
    public int RegenPerSecond { get; }
    public int PerCast(string? className) => className is not null && perCast.TryGetValue(className, out var v) ? v : Default;

    static readonly Lazy<MpGain> embedded = new(() =>
    {
        using var stream = typeof(MpGain).Assembly.GetManifestResourceStream("provisional/mp-gain.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/mp-gain.xml");
        var root = XDocument.Load(stream).Root ?? throw new InvalidDataException("mp-gain.xml: empty");
        int I(XElement e, string n) => int.Parse((string?)e.Attribute(n) ?? "0", CultureInfo.InvariantCulture);
        return new MpGain(root.Elements("class").ToDictionary(e => (string)e.Attribute("name")!, e => I(e, "per_cast"), StringComparer.OrdinalIgnoreCase),
            I(root, "default"), I(root, "regen_per_second"));
    });

    public static MpGain Embedded => embedded.Value;
}
