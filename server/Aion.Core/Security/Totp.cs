using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>RFC 6238 TOTP (SHA-1, 30 s step, 6 digits) compatible with common authenticator apps.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    public static string GenerateSecret(int bytes = 20)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(raw);
    }

    public static string BuildOtpAuthUri(string issuer, string accountName, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var query = $"secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
        return $"otpauth://totp/{label}?{query}";
    }

    public static bool Verify(string secret, string? code, int window = 1)
    {
        if (code is not { Length: Digits } || !code.All(char.IsDigit)) return false;
        var key = Base32Decode(secret);
        if (key.Length == 0) return false;
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -window; offset <= window; offset++)
        {
            if (ComputeCode(key, counter + offset) == code) return true;
        }
        return false;
    }

    public static string CodeForCounter(string secret, long counter) => ComputeCode(Base32Decode(secret), counter);

    public static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString($"D{Digits}");
    }

    static string Base32Encode(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = data[0], next = 1, bitsLeft = 8;
        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[next++] & 0xFF;
                    bitsLeft += 8;
                }
                else
                {
                    var pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }
            var index = (buffer >> (bitsLeft - 5)) & 0x1F;
            bitsLeft -= 5;
            sb.Append(alphabet[index]);
        }
        return sb.ToString();
    }

    static byte[] Base32Decode(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        secret = secret.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (secret.Length == 0) return [];
        var output = new List<byte>(secret.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in secret)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) return [];
            buffer = (buffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
