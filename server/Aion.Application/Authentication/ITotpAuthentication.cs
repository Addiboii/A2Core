using System.Net;

namespace Aion.Application.Authentication;

public sealed record TotpStatus(bool Enabled, int RecoveryCodesRemaining);
public sealed record TotpEnrollment(string Secret, string OtpAuthUri, string[] RecoveryCodes);
public sealed record TotpLoginChallenge(string ChallengeToken, DateTimeOffset ExpiresAt);

public interface ITotpAuthentication
{
    Task<TotpStatus?> StatusAsync(Guid accountId, CancellationToken ct);
    Task<TotpEnrollment?> BeginEnrollmentAsync(Guid accountId, string accountLabel, CancellationToken ct);
    Task<(bool Ok, string[]? RecoveryCodes)> ConfirmEnrollmentAsync(Guid accountId, string code, CancellationToken ct);
    Task<(bool Ok, string[]? RecoveryCodes)> RegenerateRecoveryCodesAsync(Guid accountId, string password, CancellationToken ct);
    Task<bool> DisableAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct);
    Task<bool> VerifyForAccountAsync(Guid accountId, string? code, string? recoveryCode, CancellationToken ct);
    Task<TotpLoginChallenge?> BeginLoginAsync(Guid accountId, CancellationToken ct);
    Task<AccountSession?> CompleteLoginAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct);
    Task<bool> AdminResetAsync(Guid targetAccountId, CancellationToken ct);
    Task<bool> IsEnabledAsync(Guid accountId, CancellationToken ct);
}

public interface IPlatformSecondaryAuth
{
    Task RememberClientAsync(IPAddress? address, Guid accountId, CancellationToken ct = default);
    Task<Guid?> ResolveAccountAsync(IPAddress? address, string? platformAccessToken, CancellationToken ct);
    /// <summary>Maps platform <c>game_account_id</c> (wire id from login) to the auth account.</summary>
    Task<Guid?> ResolveAccountByWireIdAsync(string wireAccountId, CancellationToken ct);
    Task<byte[]?> AuthTypesAsync(Guid accountId, CancellationToken ct);
    Task<byte[]?> StartAsync(Guid accountId, string? audienceAppId, CancellationToken ct);
    Task<byte[]?> FinishAsync(Guid accountId, string sessionId, string? code, string? recoveryCode, CancellationToken ct);
    Task<bool> IsVerifiedAsync(Guid accountId, string platformAccessToken, CancellationToken ct);
    Task<bool> HasRecentVerificationAsync(Guid accountId, CancellationToken ct);
}
