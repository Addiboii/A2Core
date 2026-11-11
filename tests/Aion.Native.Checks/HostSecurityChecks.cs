using System.Text.RegularExpressions;
using Aion.Core.Accounts;
using Aion.Core.Security;

/// <summary>Host security pieces that need no database: recovery-code input, the staff permission table.</summary>
static class HostSecurityChecks
{
    /// <summary>auth.staff_permissions rows inserted by migrations 076 and later (action → role).</summary>
    static Dictionary<string, string> MigrationPermissions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "database", "migrations"))) directory = directory.Parent;
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        if (directory is null) return rows;
        foreach (var file in Directory.GetFiles(Path.Combine(directory.FullName, "database", "migrations"), "*.sql").Order(StringComparer.Ordinal))
        {
            if (string.CompareOrdinal(Path.GetFileName(file), "076") < 0) continue;
            foreach (Match insert in Regex.Matches(File.ReadAllText(file), @"INSERT INTO auth\.staff_permissions\(action,min_role\) VALUES(.*?);", RegexOptions.Singleline))
                foreach (Match row in Regex.Matches(insert.Groups[1].Value, @"\('([^']+)','(gamemaster|moderator|admin)'\)"))
                    rows[row.Groups[1].Value] = row.Groups[2].Value;
        }
        return rows;
    }

    public static int Run(Action<bool, string> check)
    {
        var count = 0;
        void C(bool condition, string name) { check(condition, name); count++; }

        // Recovery codes (OURS format XXXXX-XXXXX): any letter case, spaces and dashes ignored, same stored hash as before.
        var code = RecoveryCodes.Generate();
        C(code.Length == 11 && code[5] == '-' && RecoveryCodes.Normalize(code) == code.Replace("-", ""), "recovery code: generated as XXXXX-XXXXX");
        var stored = RecoveryCodes.Hash("AB12C-D34EF");
        C(stored == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("AB12CD34EF"u8)), "recovery code: stored hash unchanged since migration 040");
        var enDash = (char)0x2013; var emDash = (char)0x2014; var nbsp = (char)0x00a0;
        foreach (var typed in new[] { "ab12c-d34ef", "AB12CD34EF", "ab12cd34ef", " ab12c - d34ef ", "AB 12C\tD34 EF", $"ab12c{enDash}d34ef", $"ab12c{emDash}d34ef", $"Ab12C{nbsp}D34eF", "a-b-1-2-c-d-3-4-e-f" })
            C(RecoveryCodes.Hash(typed) == stored, $"recovery code accepted as typed: {typed.Replace("\t", "\\t")}");
        foreach (var wrong in new[] { null, "", "AB12C-D34E", "AB12C-D34EF0", "AB12C-D34EG", "AB12C.D34EF", new string(' ', 70) + "AB12CD34EF" })
            C(RecoveryCodes.Hash(wrong) is null, $"recovery code refused: {wrong ?? "null"}");

        // Staff ranks (migration 076, OURS): ordered, unknown actions refused, the database rows are the C# table.
        C(StaffPermissions.RankOf("gamemaster") < StaffPermissions.RankOf("moderator") && StaffPermissions.RankOf("moderator") < StaffPermissions.RankOf("admin")
          && StaffPermissions.RankOf("gm") == StaffRank.None && StaffPermissions.RankOf((string?)null) == StaffRank.None, "ranks: gamemaster < moderator < admin");
        C(StaffPermissions.Allows("admin", "gm.SetLevel") && StaffPermissions.Allows("moderator", "gm.SetLevel") && !StaffPermissions.Allows("gamemaster", "gm.SetLevel")
          && !StaffPermissions.Allows("admin", "no.such.action") && !StaffPermissions.Allows((string?)null, "gm.SetInvisible"), "ranks: table minimum, unknown refused");
        C(!StaffPermissions.Allows("moderator", StaffPermissions.GrantOwnAccount) && StaffPermissions.Allows("admin", StaffPermissions.GrantOwnAccount),
          "ranks: value for one's own account is Administrator only");
        var missing = Aion.World.BuilderCommands.Supported.Where(c => !StaffPermissions.Minimum.ContainsKey(StaffPermissions.GmAction(c))).ToList();
        C(missing.Count == 0, "ranks: every builder command has a row" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
        C(StaffPermissions.SelfValueCommands.All(c => Aion.World.BuilderCommands.Supported.Contains(c)), "ranks: value commands are builder commands");
        var rows = MigrationPermissions();
        var differ = rows.Keys.Union(StaffPermissions.Minimum.Keys)
            .Where(a => !rows.TryGetValue(a, out var r) || !StaffPermissions.Minimum.TryGetValue(a, out var m) || StaffPermissions.RankOf(r) != m).ToList();
        C(rows.Count > 0 && differ.Count == 0, "ranks: auth.staff_permissions rows equal StaffPermissions" + (differ.Count > 0 ? ": differ " + string.Join(", ", differ) : ""));

        // Listener diagnostics (OURS): counts and timings only, reasons as fixed codes.
        var stats = new Aion.World.ListenerStats("game");
        stats.Accepted(); stats.Accepted(); stats.Ended("closed");
        stats.Refused("capacity"); stats.Refused("capacity"); stats.Refused("Hello world: someone's chat text"); stats.Refused(null!);
        stats.Malformed();
        var tick = System.Diagnostics.Stopwatch.Frequency / 1000;  // 1 ms
        stats.Handled(0x3613, tick); stats.Handled(0x3613, 3 * tick); stats.Handled(0x3600, 2 * tick); stats.Handled(-1, tick); stats.Handled(0x10000, tick);
        Parallel.For(0, 1000, _ => stats.Handled(0x3600, tick));
        var view = stats.Read();
        C(view.Accepted == 2 && view.Active == 1 && view.Ends["closed"] == 1 && view.MalformedSkips == 1, "diagnostics: connections, ends and malformed skips counted");
        C(view.Refusals.Count == 2 && view.Refusals["capacity"] == 2 && view.Refusals["other"] == 2, "diagnostics: refusal reasons are fixed codes, other text is 'other'");
        C(view.Opcodes.Count == 2 && view.Opcodes[0].Opcode == "0x3600" && view.Opcodes[0].Count == 1001 && view.Opcodes[1].Opcode == "0x3613"
          && view.Opcodes[1].Count == 2 && view.Opcodes[1].MaxMs == 3 && view.Opcodes[1].MeanMs == 2 && view.Opcodes[1].TotalMs == 4,
          "diagnostics: per-opcode count, total, mean and max; out-of-range opcodes not recorded; busiest first");
        C(stats.Read(maxOpcodes: 1).Opcodes.Count == 1, "diagnostics: opcode list bounded");
        var json = System.Text.Json.JsonSerializer.Serialize(view);
        C(!json.Contains("someone", StringComparison.OrdinalIgnoreCase) && !json.Contains("Hello", StringComparison.Ordinal), "diagnostics: no text from requests kept");
        return count;
    }
}
