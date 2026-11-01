using System.Text.Json;
using Aion.Host.Platform;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformGateEmptyRepliesTests
{
    [Theory]
    [InlineData("GET", "/local-api-gate/trade/v1.0/policies")]
    [InlineData("GET", "/local-api-gate/trade/v1.0/me/earnings")]
    [InlineData("POST", "/local-api-gate/mobile_warehouse/v1.0/my/items")]
    [InlineData("POST", "/local-api-gate/fizz/v2.0/tokens")]
    [InlineData("GET", "/local-api-gate/role/v1.0/apps/B91B66D7-4B0A-4E3C-A767-DADCE0B52962/roles")]
    [InlineData("GET", "/local-api-gate/profileimage/v1.0/game_profile_images/10001")]
    public void Shop_side_calls_get_valid_json(string method, string path)
    {
        Assert.True(PlatformApiGateResponses.TryReply(method, path, QueryCollection.Empty, null, out var body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
