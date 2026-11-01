namespace Aion.Application.Gameplay;

/// <summary>
/// NC GameCash (Quna): account-wide balance from item 535300006 stacks (all characters + account storage),
/// matching platform GET virtual_currency/v2.0/me/balances before a character is in the world.
/// </summary>
public interface IAccountQunaBalance
{
    Task<long> SumAsync(Guid accountId, CancellationToken ct = default);
}
