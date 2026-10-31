namespace Aion.World;

/// <summary>When player skill damage lands relative to cast accept (Skill.dat CastingTime + effect row timing).</summary>
public static class SkillTiming
{
    public const int MultiHitStaggerMs = 400;

    /// <summary>Milliseconds after the cast is accepted when this damage row should land.</summary>
    public static int HitLandMs(SkillDefinition skill, int hitIndex)
    {
        var rows = skill.Damage;
        var row = rows[hitIndex];
        var offset = row.HitAtMs > 0 ? row.HitAtMs
            : rows.Length > 1 ? NpcAi.HitDelayMs + hitIndex * MultiHitStaggerMs : NpcAi.HitDelayMs;
        return skill.CastingMs + offset;
    }

    /// <summary>
    /// A projectile row's release after the cast accept: its own row time, else OBSERVED official Ranger 2026-10-30 (the
    /// exported Skill.dat rows carry no time): a single arrow one server tick after the cast batch (Snipe's 0x3803 57 / 76 ms
    /// after the 0x3802/0x3801 batch), several arrows from the cast batch itself, 149 ms apart (Rapid Fire). The row lands
    /// after the release and its flight.
    /// </summary>
    public const int ProjectileReleaseMs = 57;

    public const int ProjectileStaggerMs = 149;

    /// <summary>Release of timed row <paramref name="timedRow"/> (see <see cref="TimedRows"/>).</summary>
    public static int ProjectileReleaseAtMs(SkillDefinition skill, int timedRow)
    {
        var rows = TimedRows(skill);
        var first = Array.IndexOf(rows, timedRow);
        if (first >= 0 && skill.Damage[first].HitAtMs > 0) return skill.CastingMs + skill.Damage[first].HitAtMs;
        return skill.CastingMs + (rows.Length == 0 || rows[^1] == 0 ? ProjectileReleaseMs : timedRow * ProjectileStaggerMs);
    }

    /// <summary>
    /// The Skill.dat SkillEffectTimeDataList row of each damage row. A timed row's effects are numbered skill * 100 + group * 10 + n;
    /// n = 1 opens the next timed row, n &gt; 1 is another effect of the same one: Deadshot 1401000011 / 1401000012 is one shot,
    /// Rapid Fire's 1403000011 x 3 three, Tempest Shot's 1434000011 / 1434000021 two. The count equals Skill.dat EffectValueList[4]
    /// of every multi-row ranged skill.
    /// </summary>
    public static int[] TimedRows(SkillDefinition skill)
    {
        var rows = new int[skill.Damage.Length];
        for (var i = 1; i < rows.Length; i++)
            rows[i] = rows[i - 1] + (skill.Damage[i].EffectId % 10 == 1 ? 1 : 0);
        return rows;
    }
}
