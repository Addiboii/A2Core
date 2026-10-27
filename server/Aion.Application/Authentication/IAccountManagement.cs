namespace Aion.Application.Authentication;

public interface IAccountManagement
{
    Task<bool> ChangePasswordAsync(string session, string currentPassword, string newPassword, CancellationToken ct=default);
    Task<bool> RevokeSessionsAsync(string session, CancellationToken ct=default);
    Task<bool> DisableAccountAsync(string session, Guid target, CancellationToken ct=default);
    // Admin only (checked in the database); role is gamemaster or admin. Applies at the target's next game login.
    Task<bool> SetStaffRoleAsync(string session, Guid target, string role, bool granted, CancellationToken ct=default);
}
