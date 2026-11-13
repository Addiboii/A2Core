using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>
/// Spacetime Rifts and hidden cubes, our rules (gamedata/server-rules.xml rifts / hidden_cubes elements). OURS except where noted.
/// </summary>
public sealed record RiftRules
{
    /// <summary>How a rift picks the world of the other faction (OURS, user 2026-11-13): auto (busy: spread, quiet: the populated world),
    /// spread (always the emptier world), populated (always the fuller world), pair (always the match world, as before the rule).</summary>
    public string Mode { get; init; } = RiftWorlds.Auto;
    /// <summary>The other faction counts as busy ("full") when one of its worlds holds at least this percentage of WorldCatalog.Capacity
    /// (players at home plus rift visitors). Default 60 % (300 of 500).</summary>
    public int BusyPercent { get; init; } = 60;
    /// <summary>How long a rift visit lasts before the player is sent home (minutes). OFFICIAL default: GlobalSetting
    /// rift_contentsticket_default_time 3600000 ms (Contents_Ticket_Rift_Time, RechargeMaxTime 3600 s); the ticket's recharges are not modelled.</summary>
    public int VisitMinutes { get; init; } = 60;
    /// <summary>A rift's announcement to everyone in the world when it opens (L10N MSG_RIFT_SPAWN "A Spacetime Rift has appeared.").</summary>
    public bool Announce { get; init; } = true;
    /// <summary>An opened hidden cube's slot comes back after this long (minutes) at a random position of its pool (CANDIDATE: no
    /// respawn time in the client data or the recordings).</summary>
    public int CubeRespawnMinutes { get; init; } = 15;

    public static RiftRules Default { get; } = new();

    public static RiftRules Parse(XElement? rifts, XElement? cubes)
    {
        int Number(XElement? e, string name, int fallback, int min, int max)
        {
            var v = int.Parse((string?)e?.Attribute(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return v >= min && v <= max ? v : throw new InvalidDataException($"server-rules.xml: {e?.Name} {name} must be {min}-{max}");
        }
        var mode = (string?)rifts?.Attribute("mode") ?? RiftWorlds.Auto;
        if (mode is not (RiftWorlds.Auto or RiftWorlds.Spread or RiftWorlds.Populated or RiftWorlds.Pair))
            throw new InvalidDataException("server-rules.xml: rifts mode must be auto, spread, populated or pair");
        return new RiftRules
        {
            Mode = mode,
            BusyPercent = Number(rifts, "busy_percent", 60, 1, 100),
            VisitMinutes = Number(rifts, "visit_minutes", 60, 1, 1440),
            Announce = (string?)rifts?.Attribute("announce") is not { } a || System.Xml.XmlConvert.ToBoolean(a),
            CubeRespawnMinutes = Number(cubes, "respawn_minutes", 15, 0, 10080),
        };
    }
}

/// <summary>
/// OURS (user 2026-11-13): which world of the other faction a Spacetime Rift sends a player to. Every world is one faction
/// (WorldCatalog: Light 1361/1362, Dark 2361/2362), so a rift on a Light map leads into a Dark world's copy of the Dark map and back.
/// <para>
/// What Worlds.cs does: it does not consolidate players. WorldCatalog.Tags puts Recommend on the emptiest open world of each race, so
/// new characters are spread over the worlds; nothing moves players between worlds. So the rift itself looks at the population.
/// </para>
/// <para>
/// The rule: population of a world = its own players online plus the rift visitors in it now. When the other faction is busy (one of
/// its worlds at BusyPercent of the capacity or more) the rift spreads its users over both of that faction's worlds (the emptier one
/// first, alternating on a tie). When it is quiet the rift sends everyone to that faction's most populated world, so the few players
/// meet; when nobody is there at all, to the player's match world (the pair's other world, as before the rule).
/// </para>
/// </summary>
public static class RiftWorlds
{
    public const string Auto = "auto", Spread = "spread", Populated = "populated", Pair = "pair";

    /// <summary>The world a player of <paramref name="homeWorld"/> arrives in through a rift; <paramref name="turn"/> breaks ties when spreading.</summary>
    public static int Pick(WorldCatalog worlds, int homeWorld, Func<int, int> population, RiftRules rules, int turn = 0)
    {
        var home = worlds.Of(homeWorld);
        var race = home?.Race ?? 1;
        var candidates = worlds.Worlds.Where(w => w.Race != race).OrderBy(w => w.Id).ToList();
        var match = worlds.MatchOf(homeWorld);
        if (candidates.Count == 0) return homeWorld;
        if (candidates.Count == 1) return candidates[0].Id;
        var counts = candidates.ToDictionary(w => w.Id, w => Math.Max(0, population(w.Id)));
        var mode = rules.Mode;
        if (mode == Auto) mode = Busy(worlds, counts.Values, rules) ? Spread : Populated;
        switch (mode)
        {
            case Pair:
                return candidates.Any(w => w.Id == match) ? match : candidates[0].Id;
            case Spread:
            {
                var least = counts.Values.Min();
                var tied = candidates.Where(w => counts[w.Id] == least).ToList();
                return tied[(int)((uint)turn % (uint)tied.Count)].Id;
            }
            default:
            {
                var most = counts.Values.Max();
                if (most == 0) return candidates.Any(w => w.Id == match) ? match : candidates[0].Id;
                var tied = candidates.Where(w => counts[w.Id] == most).ToList();
                return tied.Any(w => w.Id == match) ? match : tied[0].Id;
            }
        }
    }

    /// <summary>Busy: one of the faction's worlds holds BusyPercent of the capacity or more.</summary>
    public static bool Busy(WorldCatalog worlds, IEnumerable<int> counts, RiftRules rules) =>
        counts.Any(c => c * 100L >= (long)rules.BusyPercent * worlds.Capacity);
}
