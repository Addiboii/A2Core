using System.Net;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Platform;
using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformVirtualCurrencyTests
{
    [Fact]
    public void Balance_json_group_145_uses_quna_virtual_currency_id_and_string_amounts()
    {
        using var doc = JsonDocument.Parse(ShopQunaBalanceService.JsonBody(50_000, GameSession.QunaBmCurrencyGroupId));
        var root = doc.RootElement;
        Assert.Equal("1", root.GetProperty("balance_info_list_count").GetString());
        Assert.Equal("50000.000000", root.GetProperty("total_balance").GetString());
        var row = root.GetProperty("balance_info_list")[0];
        Assert.Equal(GameSession.QunaBmCurrencyGroupId.ToString(), row.GetProperty("currency_group_id").GetString());
        Assert.Equal(GameSession.QunaVirtualCurrencyId.ToString(), row.GetProperty("currency_id").GetString());
        Assert.Equal("50000.000000", row.GetProperty("balance").GetString());
    }

    [Fact]
    public void Balance_json_group_158_returns_mileage_currency_id_and_zero()
    {
        using var doc = JsonDocument.Parse(ShopQunaBalanceService.JsonBody(50_000, GameSession.MileageCurrencyGroupId));
        var row = doc.RootElement.GetProperty("balance_info_list")[0];
        Assert.Equal(GameSession.MileageCurrencyGroupId.ToString(), row.GetProperty("currency_group_id").GetString());
        Assert.Equal(GameSession.MileageVirtualCurrencyId.ToString(), row.GetProperty("currency_id").GetString());
        Assert.Equal("0.000000", row.GetProperty("balance").GetString());
        Assert.Equal("0.000000", doc.RootElement.GetProperty("total_balance").GetString());
    }

    [Fact]
    public void Me_country_uses_geolocation_fixture()
    {
        var geo = """{"geolocation":"NO","coordinates":{"latitude":"59.9139","longitude":"10.7522"}}"""u8.ToArray();
        using var doc = JsonDocument.Parse(PlatformApiGateResponses.MeCountry(geo));
        Assert.Equal("NO", doc.RootElement.GetProperty("country_code").GetString());
    }

    [Fact]
    public void Me_country_defaults_when_geolocation_missing()
    {
        using var doc = JsonDocument.Parse(PlatformApiGateResponses.MeCountry(null));
        Assert.Equal("US", doc.RootElement.GetProperty("country_code").GetString());
    }

    [Fact]
    public async Task Balance_returns_zero_when_account_unknown()
    {
        var svc = new ShopQunaBalanceService(new WorldPresence(), platformAuth: null, accountQuna: new StubAccountQuna(50_000));
        Assert.Equal(0, await svc.BalanceAsync(IPAddress.Loopback, null, "9999999", default));
    }

    [Fact]
    public async Task Balance_resolves_wire_game_account_id_before_world_entry()
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Items.Add(FakeCharacterStore.BagItem(535300006, 50_000, 9001));
        var accountQuna = new StoreAccountQuna(store);
        var platform = new WireOnlyPlatformAuth(store.AccountId);
        var svc = new ShopQunaBalanceService(new WorldPresence(), platform, accountQuna);
        Assert.Equal(50_000, await svc.BalanceAsync(IPAddress.Loopback, platformSessionToken: null, wireAccountId: "1000001", default));
    }

    sealed class StubAccountQuna(long balance) : IAccountQunaBalance
    {
        public Task<long> SumAsync(Guid accountId, CancellationToken ct) => Task.FromResult(balance);
    }

    sealed class StoreAccountQuna(FakeCharacterStore store) : IAccountQunaBalance
    {
        public Task<long> SumAsync(Guid accountId, CancellationToken ct) =>
            accountId == store.AccountId ? store.AccountQunaBalanceAsync(ct) : Task.FromResult(0L);
    }

    sealed class WireOnlyPlatformAuth(Guid accountId) : IPlatformSecondaryAuth
    {
        public Task RememberClientAsync(IPAddress? address, Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> ResolveAccountAsync(IPAddress? address, string? platformAccessToken, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolveAccountByWireIdAsync(string wireAccountId, CancellationToken ct) =>
            wireAccountId == "1000001" ? Task.FromResult<Guid?>(accountId) : Task.FromResult<Guid?>(null);
        public Task<byte[]?> AuthTypesAsync(Guid accountId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> StartAsync(Guid accountId, string? audienceAppId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> FinishAsync(Guid accountId, string sessionId, string? code, string? recoveryCode, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
        public Task<bool> IsVerifiedAsync(Guid accountId, string platformAccessToken, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> HasRecentVerificationAsync(Guid accountId, CancellationToken ct) => Task.FromResult(false);
    }
}
