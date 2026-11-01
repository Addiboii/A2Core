using Aion.Core.Security;

var secret = Totp.GenerateSecret();
var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
Check(Totp.Verify(secret, Totp.CodeForCounter(secret, counter)), "current step");
Check(!Totp.Verify(secret, "000000", window: 0), "reject wrong code");
var uri = Totp.BuildOtpAuthUri("Aion Local", "alice", secret);
Check(uri.Contains("secret=" + secret), "otpauth contains secret");
var protection = new TotpSecretProtection(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
var (cipher, nonce) = protection.Encrypt(secret);
Check(protection.Decrypt(cipher, nonce) == secret, "secret roundtrip");

static void Check(bool ok, string name)
{
    if (!ok) throw new Exception("Failed: " + name);
    Console.WriteLine("ok " + name);
}

Console.WriteLine("Totp tests passed.");
