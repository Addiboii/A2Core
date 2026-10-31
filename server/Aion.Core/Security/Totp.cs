using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 s step, 6 digits), the defaults every authenticator app reads from
/// an otpauth:// URI. OURS (account-site two-step sign-in; nothing from the game protocol).
/// Ported from A2Core PR #4 (Artezianec), with replay protection (callers keep the last accepted step and
/// pass it as <c>afterStep</c>) and a constant-time code compare.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    /// <summary>Steps accepted either side of now (clock drift between phone and server).</summary>
    public const int DefaultWindow = 1;

    public static string GenerateSecret(int bytes = 20) => Base32Encode(RandomNumberGenerator.GetBytes(bytes));

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string BuildOtpAuthUri(string issuer, string accountName, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>The time step <paramref name="code"/> belongs to, within <paramref name="window"/> steps of
    /// <paramref name="now"/> and strictly after <paramref name="afterStep"/> (a code is accepted once), or null.</summary>
    public static long? Match(string secret, string? code, DateTimeOffset now, long afterStep = long.MinValue, int window = DefaultWindow)
    {
        if (code is null) return null;
        code = code.Trim().Replace(" ", "");
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var key = Base32Decode(secret);
        if (key.Length == 0) return null;
        var given = Encoding.ASCII.GetBytes(code);
        var current = CurrentStep(now);
        long? matched = null;
        // Every step in the window is computed and compared (no early exit on a match).
        for (var step = current - window; step <= current + window; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeCode(key, step)), given);
            if (equal && step > afterStep && matched is null) matched = step;
        }
        return matched;
    }

    public static string CodeForStep(string secret, long step) => ComputeCode(Base32Decode(secret), step);

    public static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return text.ToString();
    }

    public static byte[] Base32Decode(string secret)
    {
        secret = secret.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        if (secret.Length == 0) return [];
        var output = new List<byte>(secret.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in secret)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) return [];
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
