using Aion.Application.Gameplay;

namespace Aion.Host.Native;

/// <summary>Expired player mail with untaken attachments is returned to the sender (migration 056/070).</summary>
public sealed class PostExpirySweep(IGameplayStore store, ILogger<PostExpirySweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var count = await store.ReturnExpiredPostsAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Returned {Count} expired posts to senders", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning(error, "Post expiry sweep failed; will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
