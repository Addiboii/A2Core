namespace Aion.World;

/// <summary>
/// The host's base rates, Aion:Rates:Experience, :Kinah and :Drops (OURS: our own server settings, nothing from the
/// client or a recording). 1 is the normal rate; 0 turns the source off; at most <see cref="Max"/>. Applied where the
/// world creates them: kill and quest experience, Kinah from loot and quest rewards, and loot-table rolls. Scheduled
/// event boosts (<see cref="SharedWorld.ExpRate"/>, <see cref="SharedWorld.DropRate"/>) multiply on top.
/// </summary>
public sealed record WorldRates
{
    /// <summary>OURS: the highest configurable rate.</summary>
    public const double Max = 100;
    /// <summary>OURS: at most this many loot-table rolls per kill (base rate times an event boost), so one drop message stays small.</summary>
    public const int MaxLootRolls = 100;

    public double Experience { get; init; } = 1;
    public double Kinah { get; init; } = 1;
    public double Drops { get; init; } = 1;

    /// <summary>Throws when a rate is not a number from 0 to <see cref="Max"/> (the host refuses to start).</summary>
    public WorldRates Validate()
    {
        foreach (var (name, value) in new[] { ("Experience", Experience), ("Kinah", Kinah), ("Drops", Drops) })
            if (!double.IsFinite(value) || value < 0 || value > Max)
                throw new InvalidOperationException($"Aion:Rates:{name} must be a number from 0 to {Max:0} (it is {value}).");
        return this;
    }

    /// <summary>An amount at a rate, rounded; never negative, at most long.MaxValue.</summary>
    public static long Scale(long amount, double rate)
    {
        if (amount <= 0 || rate <= 0) return 0;
        var scaled = Math.Round(amount * rate);
        return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
    }

    /// <summary>Loot-table rolls for one kill at this rate: the whole part, plus one more with the fraction's chance.</summary>
    public static int Rolls(double rate, Random rng)
    {
        if (!(rate > 0)) return 0;
        var whole = Math.Floor(rate);
        var rolls = (int)Math.Min(whole, MaxLootRolls) + (rate - whole > 0 && rng.NextDouble() < rate - whole ? 1 : 0);
        return Math.Min(rolls, MaxLootRolls);
    }
}
