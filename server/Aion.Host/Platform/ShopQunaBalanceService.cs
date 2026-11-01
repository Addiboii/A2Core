using System.Globalization;
using System.Net;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Platform;

/// <summary>Account-wide GameCash (Quna) for platform GET virtual_currency/v2.0/me/balances and in-world refresh parity.</summary>
public sealed class ShopQunaBalanceService(
    WorldPresence presence,
    IPlatformSecondaryAuth? platformAuth,
    IAccountQunaBalance? accountQuna)
{
    public async Task<long> BalanceAsync(IPAddress? clientAddress, string? platformSessionToken, string? wireAccountId, CancellationToken ct)
    {
        if (await ResolveAccountIdAsync(clientAddress, platformSessionToken, wireAccountId, ct) is not { } accountId)
            return 0;
        if (platformAuth is not null)
            await platformAuth.RememberClientAsync(clientAddress, accountId, ct);
        if (accountQuna is not null)
            return await accountQuna.SumAsync(accountId, ct);
        return OnlineSessionQuna(accountId);
    }

    long OnlineSessionQuna(Guid accountId)
    {
        var online = presence.All.Where(e => e.AccountId == accountId || e.Session.Store?.AccountId == accountId).ToList();
        if (online.Count == 1) return online[0].Session.QunaBalance();
        return 0;
    }

    async Task<Guid?> ResolveAccountIdAsync(IPAddress? clientAddress, string? platformSessionToken, string? wireAccountId, CancellationToken ct)
    {
        if (platformAuth is not null)
        {
            if (await platformAuth.ResolveAccountAsync(clientAddress, platformSessionToken, ct) is { } fromAuth)
                return fromAuth;
            if (wireAccountId is not null && await platformAuth.ResolveAccountByWireIdAsync(wireAccountId, ct) is { } fromWire)
                return fromWire;
        }
        if (wireAccountId is not null && SessionByWireAccount(wireAccountId)?.Store?.AccountId is { } fromOnline)
            return fromOnline;
        return null;
    }

    GameSession? SessionByWireAccount(string wireAccountId) =>
        presence.All.FirstOrDefault(e => string.Equals(e.Session.WireAccountId, wireAccountId, StringComparison.OrdinalIgnoreCase))?.Session;

    /// <summary>Official balance_info_list shape for one currency_group_id query (145 GameCash Quna, 158 mileage).</summary>
    public static byte[] JsonBody(long qunaInBag, int currencyGroupId)
    {
        var (currencyId, balance) = currencyGroupId switch
        {
            GameSession.QunaBmCurrencyGroupId => (GameSession.QunaVirtualCurrencyId, qunaInBag),
            GameSession.MileageCurrencyGroupId => (GameSession.MileageVirtualCurrencyId, 0L),
            _ => (GameSession.QunaVirtualCurrencyId, 0L),
        };
        return JsonBodyFor(currencyGroupId, currencyId, balance);
    }

    public static byte[] JsonBodyFor(int currencyGroupId, int currencyId, long balance)
    {
        var amount = balance.ToString("F6", CultureInfo.InvariantCulture);
        var zero = "0.000000";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            balance_info_list = new[]
            {
                new
                {
                    currency_id = currencyId.ToString(CultureInfo.InvariantCulture),
                    currency_group_id = currencyGroupId.ToString(CultureInfo.InvariantCulture),
                    balance = amount,
                    pending_balance = zero,
                },
            },
            balance_info_list_count = "1",
            total_pending_balance = zero,
            total_balance = amount,
        }, PlatformResponses.Plain);
    }
}
