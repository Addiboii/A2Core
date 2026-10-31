using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

public sealed class TotpSecretProtection
{
    readonly byte[] key;

    public TotpSecretProtection(byte[] keyBytes)
    {
        if (keyBytes.Length != 32) throw new ArgumentException("Expected a 256-bit encryption key.");
        key = keyBytes;
    }

    public TotpSecretProtection(string keyBase64) : this(Convert.FromBase64String(keyBase64)) { }

    public (byte[] Ciphertext, byte[] Nonce) Encrypt(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var packed = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, packed, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, cipher.Length, tag.Length);
        return (packed, nonce);
    }

    public string? Decrypt(byte[] ciphertext, byte[] nonce)
    {
        if (ciphertext.Length <= 16 || nonce.Length != 12) return null;
        var cipher = ciphertext.AsSpan(0, ciphertext.Length - 16);
        var tag = ciphertext.AsSpan(ciphertext.Length - 16, 16);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }
}
