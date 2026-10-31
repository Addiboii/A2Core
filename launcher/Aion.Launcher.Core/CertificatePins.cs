using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aion.Launcher;

/// <summary>
/// Account-server certificate pins (launcher.json "certificatePins"): SHA-256 of the certificate's
/// SubjectPublicKeyInfo (DER), written as base64 (44 characters, the form openssl and curl's
/// --pinnedpubkey print) or as 64 hex characters. A certificate is accepted when the system trusts it
/// (no TLS policy errors), or, only when it has errors (self-signed, an IP address as the name), when its
/// public key matches one of the pins. Several pins allow a key change: add the new pin before switching.
/// Idea from A2Core PR #4 (Artezianec, a thumbprint that replaced the normal check); this keeps the
/// normal check and pins the key, so a renewed certificate with the same key keeps working.
/// </summary>
public static class CertificatePins
{
    /// <summary>The pin bytes, or null when the text is neither base64 nor hex of 32 bytes.</summary>
    public static byte[]? Parse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("sha256//", StringComparison.Ordinal)) text = text[8..];
        if (text.Length == 64 && text.All(char.IsAsciiHexDigit)) return Convert.FromHexString(text);
        var bytes = new byte[48];
        return text.Length == 44 && Convert.TryFromBase64String(text, bytes, out var written) && written == 32 ? bytes[..32] : null;
    }

    /// <summary>SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static byte[] SpkiSha256(X509Certificate2 certificate) =>
        SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public static string Base64(X509Certificate2 certificate) => Convert.ToBase64String(SpkiSha256(certificate));

    /// <summary>The validation rule (see the class summary).</summary>
    public static bool Accept(X509Certificate2? certificate, SslPolicyErrors errors, IReadOnlyCollection<byte[]> pins)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || pins.Count == 0 || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        var actual = SpkiSha256(certificate);
        var match = false;
        foreach (var pin in pins) match |= CryptographicOperations.FixedTimeEquals(pin, actual);
        return match;
    }

    /// <summary>A handler with the normal system check, plus the pins when there are any.</summary>
    public static HttpMessageHandler Handler(IReadOnlyCollection<string>? pinTexts)
    {
        var handler = new HttpClientHandler();
        var pins = (pinTexts ?? []).Select(Parse).OfType<byte[]>().ToArray();
        if (pins.Length > 0)
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => Accept(certificate, errors, pins);
        return handler;
    }
}
