namespace Aion.Application.Authentication;

/// <summary>Two-step sign-in (TOTP) status; Available is false when the host has no Aion:Totp:EncryptionKey.</summary>
public sealed record TotpStatus(bool Available, bool Enabled, int RecoveryCodesRemaining);
public sealed record TotpEnrollment(string Secret, string OtpAuthUri)
{
    public override string ToString() => "TotpEnrollment [secret redacted]";
}
public sealed record TotpLoginChallenge(string ChallengeToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "TotpLoginChallenge [token redacted]";
}

/// <summary>A password sign-in: a session, or (two-step accounts) a challenge to complete with a code.
/// Unavailable: the account has two-step sign-in but this host cannot check codes (no encryption key).</summary>
public sealed record PasswordSignIn(AccountSession? Session, TotpLoginChallenge? Challenge, bool Unavailable = false);

/// <summary>Account-site two-step sign-in. Ported from A2Core PR #4 (Artezianec); see server/Aion.Host/Authentication/TotpEndpoints.cs.</summary>
public interface ITotpAuthentication
{
    /// <summary>False when Aion:Totp:EncryptionKey is unset: enrolment is refused and two-step accounts cannot sign in.</summary>
    bool Available { get; }
    /// <summary>Password sign-in that returns a challenge instead of a session for two-step accounts
    /// (IAccountAuthentication.SignInAsync returns no session for those).</summary>
    Task<PasswordSignIn?> SignInAsync(string username, string password, TimeSpan lifetime, CancellationToken ct);
    Task<AccountSession?> CompleteSignInAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct);
    Task<TotpStatus> StatusAsync(Guid accountId, CancellationToken ct);
    /// <summary>Starts enrolment after checking the account password; null when the password is wrong or two-step is already on.</summary>
    Task<TotpEnrollment?> BeginEnrollmentAsync(Guid accountId, string password, CancellationToken ct);
    /// <summary>Turns two-step on with the first code from the app; returns the one-time recovery codes.</summary>
    Task<string[]?> ConfirmEnrollmentAsync(Guid accountId, string? code, CancellationToken ct);
    Task<string[]?> RegenerateRecoveryCodesAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct);
    Task<bool> DisableAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct);
    /// <summary>Admin only (checked in the database, audited as totp_reset): removes two-step sign-in from an account.</summary>
    Task<bool> AdminResetAsync(string session, Guid target, CancellationToken ct);
}
