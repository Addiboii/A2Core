using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformStoreAccountTests
{
    [Fact]
    public void Warehouse_echoes_account_server_and_character_like_official()
    {
        var body = PlatformApiGateResponses.Warehouse(JsonNode.Parse("""{"page_index":1,"game_server_id":"2361","char_key":"2"}""")!.AsObject(), "10001");
        Assert.Equal("""{"total_count":"0","game_account_id":"10001","game_server_id":"2361","char_key":"2","warehouse_items":[]}""", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void Storage_bucket_and_objects_answer_official_shapes()
    {
        Assert.True(PlatformApiGateResponses.TryStorage("GET", "/local-api-gate/storage/v1.0/buckets/10001", null, out var bucket));
        Assert.Equal("Editor", JsonDocument.Parse(bucket).RootElement.GetProperty("metadata").GetProperty("acl")[0].GetProperty("role").GetString());
        Assert.True(PlatformApiGateResponses.TryStorage("GET", "/local-api-gate/storage/v1.0/buckets/10001/objects/server.test1", null, out var empty));
        Assert.Equal("""{"value":"EmptyString"}""", Encoding.UTF8.GetString(empty));
        Assert.True(PlatformApiGateResponses.TryStorage("PUT", "/local-api-gate/storage/v1.0/buckets/10001/objects/server.test1", JsonNode.Parse("""{"value":"a=1"}""")!.AsObject(), out _));
        Assert.True(PlatformApiGateResponses.TryStorage("GET", "/local-api-gate/storage/v1.0/buckets/10001/objects/server.test1", null, out var saved));
        Assert.Equal("""{"value":"a=1"}""", Encoding.UTF8.GetString(saved));
    }

    [Fact]
    public void External_account_links_the_steam_store()
    {
        var o = PlatformResponses.ExternalAccount("u1", "Name");
        Assert.Equal("steam", o["store_auth_provider_code"]!.GetValue<string>());
        Assert.Equal("u1", o["external_account_key"]!.GetValue<string>());
    }
}