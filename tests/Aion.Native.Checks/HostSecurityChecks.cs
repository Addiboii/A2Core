using Aion.Core.Security;

/// <summary>Host security pieces that need no database: recovery-code input.</summary>
static class HostSecurityChecks
{
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
        return count;
    }
}
