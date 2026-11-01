using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformPolicyCustomDataTests
{
    const string Key = "0123456789abcdef";

    static JsonObject Served(Dictionary<string, string>? customData)
    {
        var dir = Directory.CreateTempSubdirectory("aion-policy-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "app-key.json"), $$"""{"value":"{{Convert.ToBase64String(new byte[256])}}"}""");
            File.WriteAllText(Path.Combine(dir.FullName, "local-test-key.json"), $$"""{"key":"{{Key}}"}""");
            var plain = Path.Combine(dir.FullName, "policy.json");
            File.WriteAllText(plain, """{"api_gate":"http://127.0.0.1:8087/local-api-gate","custom_data":{"SecondaryAuthCheckType":"OTP"}}""");
            var responses = PlatformResponses.Load(new PlatformOptions { FixtureDirectory = dir.FullName, PolicyPlaintextFile = plain, CustomData = customData }, 3700, 3701);
            var cipher = Convert.FromBase64String(JsonNode.Parse(responses.Policy!)!["value"]!.GetValue<string>());
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes(Key);
            return JsonNode.Parse(aes.DecryptCbc(cipher, aes.Key, PaddingMode.PKCS7))!["custom_data"]!.AsObject();
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Policy_turns_off_in_client_secondary_auth_like_official_steam()
    {
        Assert.Equal("None", Served(null)["SecondaryAuthCheckType"]!.GetValue<string>());
    }

    [Fact]
    public void Configured_custom_data_still_wins()
    {
        Assert.Equal("OTP", Served(new() { ["SecondaryAuthCheckType"] = "OTP" })["SecondaryAuthCheckType"]!.GetValue<string>());
    }
}
