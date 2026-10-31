using System.Text.Json;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformLoginApiTests
{
    [Fact]
    public async Task Private_id_is_stable_for_the_same_seed()
    {
        var first = await PlatformLoginApiResponses.PrivateIdBodyAsync("token-a", null, null, null, null, default);
        var second = await PlatformLoginApiResponses.PrivateIdBodyAsync("token-a", null, null, null, null, default);
        Assert.Equal(first, second);
        using var doc = JsonDocument.Parse(first);
        Assert.True(doc.RootElement.TryGetProperty("private_id", out var id));
        Assert.Equal(64, id.GetString()!.Length);
    }

    [Fact]
    public void Login_api_root_returns_ok_without_503()
    {
        Assert.True(PlatformLoginApiResponses.TryReply("GET", "/local-login-api", out var reply));
        Assert.Equal(200, reply.Status);
        Assert.Contains("ok", JsonDocument.Parse(reply.Body).RootElement.EnumerateObject().Select(p => p.Name));
    }
}
