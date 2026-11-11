// A self-signed certificate for the account site, as install.sh makes with openssl on Linux (deploy/windows/install.ps1).
//   dotnet run SelfSigned.cs -- <IPv4 address> <cert.pem> <key.pem>   writes both files and prints the launcher pin
//   dotnet run SelfSigned.cs -- --pin <cert.pem>                       prints the pin of an existing certificate
// The pin is the SHA-256 of the certificate's SubjectPublicKeyInfo in base64: launcher.json "certificatePins".
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

static string Pin(X509Certificate2 certificate) =>
    Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

if (args.Length == 2 && args[0] == "--pin")
{
    using var existing = X509Certificate2.CreateFromPem(File.ReadAllText(args[1]));
    Console.WriteLine(Pin(existing));
    return 0;
}
if (args.Length != 3 || !IPAddress.TryParse(args[0], out var address))
{
    Console.Error.WriteLine("usage: SelfSigned.cs <IPv4 address> <cert.pem> <key.pem> | --pin <cert.pem>");
    return 2;
}
using var key = RSA.Create(2048);
var request = new CertificateRequest($"CN={address}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder();
names.AddIpAddress(address);
request.CertificateExtensions.Add(names.Build());
// 825 days, as install.sh.
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(825));
File.WriteAllText(args[1], certificate.ExportCertificatePem() + "\n");
File.WriteAllText(args[2], key.ExportPkcs8PrivateKeyPem() + "\n");
Console.WriteLine(Pin(certificate));
return 0;
