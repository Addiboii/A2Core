using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>AES-256-GCM for stored TOTP secrets (the database holds ciphertext + tag and a 12-byte nonce).
/// The key is Aion:Totp:EncryptionKey (base64 of 32 random bytes), configured on its own: no key is derived
/// from another secret. Ported from A2Core PR #4 (Artezianec).</summary>
public sealed class TotpSecretProtection
{
    const int TagBytes = 16, NonceBytes = 12;
    readonly byte[] key;

    public TotpSecretProtection(byte[] keyBytes)
    {
        if (keyBytes.Length != 32) throw new ArgumentException("Expected a 256-bit encryption key (32 bytes, base64).");
        key = keyBytes.ToArray();
    }

    /// <summary>Parses a base64 key; null when the text is not base64 of exactly 32 bytes.</summary>
    public static TotpSecretProtection? TryCreate(string? keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64)) return null;
        var bytes = new byte[64];
        return Convert.TryFromBase64String(keyBase64.Trim(), bytes, out var written) && written == 32
            ? new TotpSecretProtection(bytes[..32]) : null;
    }

    public (byte[] Ciphertext, byte[] Nonce) Encrypt(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var packed = new byte[plain.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, packed.AsSpan(0, plain.Length), packed.AsSpan(plain.Length));
        return (packed, nonce);
    }

    public string? Decrypt(byte[] ciphertext, byte[] nonce)
    {
        if (ciphertext.Length <= TagBytes || nonce.Length != NonceBytes) return null;
        var plain = new byte[ciphertext.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }
}
