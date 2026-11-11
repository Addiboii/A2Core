using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>
/// Two-step sign-in recovery codes. OURS (account site, nothing from the game protocol): 10 random codes shown as
/// XXXXX-XXXXX (5 bytes of upper-case hex, 40 bits each), stored as SHA-256 of the normalized code.
/// A typed code is accepted in any letter case, with or without the dash, and with spaces, tabs or other dashes
/// (a phone keyboard's en dash, a copied non-breaking space) anywhere in it.
/// </summary>
public static class RecoveryCodes
{
    public const int Length = 10;
    /// <summary>Longest input looked at (a code with generous spacing); longer input is refused unread.</summary>
    public const int MaxInput = 64;

    public static string Generate()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(Length / 2));
        return hex[..5] + "-" + hex[5..];
    }

    /// <summary>The code's 10 upper-case hex digits, or null when the input is not a recovery code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInput) return null;
        var digits = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || IsDash(c)) continue;
            if (!char.IsAsciiHexDigit(c) || digits.Length == Length) return null;
            digits.Append(char.ToUpperInvariant(c));
        }
        return digits.Length == Length ? digits.ToString() : null;
    }

    /// <summary>The stored form: upper-case hex SHA-256 of the normalized code (unchanged since migration 040).</summary>
    public static string? Hash(string? input) =>
        Normalize(input) is { } code ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(code))) : null;

    static bool IsDash(char c) => c == '-' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.DashPunctuation
        || c == (char)0x2212;   // minus sign
}
