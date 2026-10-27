namespace Aion.Core.Accounts;

/// <summary>
/// Identity produced by trusted authentication/ticket validation code.
/// Never deserialize this type directly from a client request.
/// </summary>
public sealed record VerifiedAccount
{
    public Guid AccountId { get; }

    public VerifiedAccount(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An authenticated account ID is required.", nameof(accountId));
        AccountId = accountId;
    }
}
