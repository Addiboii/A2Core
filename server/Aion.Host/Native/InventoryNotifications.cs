using Aion.World;
using Npgsql;

namespace Aion.Host.Native;

/// <summary>
/// Live inventory updates for changes made outside the game loop (the staff dashboard runs in
/// another process): a staff grant commits with pg_notify('aion_inventory', character id); this
/// listener finds the character in the world and sends the difference (0x561b) on its game
/// loop, as //AcquireItem does. Characters not in the world see the item at the next entry.
/// </summary>
public sealed class InventoryNotifications(NpgsqlDataSource source, WorldPresence presence, ILogger<InventoryNotifications> logger) : BackgroundService
{
    public const string Channel = "aion_inventory";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                connection.Notification += (_, e) => _ = DeliverAsync(e.Payload, ct);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection)) await listen.ExecuteNonQueryAsync(ct);
                while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Inventory notifications interrupted: {Error}; retrying", error.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    async Task DeliverAsync(string payload, CancellationToken ct)
    {
        if (!Guid.TryParse(payload, out var character) || presence.FindCharacter(character) is not { } entry) return;
        try
        {
            await entry.Session.InvokeAsync(async _ => (true, await entry.Session.InventoryChangesAsync(ct)));
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Live inventory update for {Character} failed: {Error}", character, error.Message);
        }
    }
}
