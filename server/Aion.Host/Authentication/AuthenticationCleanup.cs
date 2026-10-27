using Aion.Infrastructure.Postgres;

namespace Aion.Host.Authentication;

public sealed class AuthenticationCleanup(PostgresAccountAuthentication accounts,
    ILogger<AuthenticationCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await accounts.PruneExpiredAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Npgsql.NpgsqlException error) { logger.LogWarning(error, "Expired authentication state cleanup failed; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
