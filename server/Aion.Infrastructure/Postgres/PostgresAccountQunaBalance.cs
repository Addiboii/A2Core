using Aion.Application.Gameplay;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed class PostgresAccountQunaBalance(NpgsqlDataSource source) : IAccountQunaBalance
{
    /// <summary>Item.dat GameCash currency (same as <see cref="PlatformGoodsTables.Embedded.QunaItemId"/>).</summary>
    const int QunaItemId = 535300006;

    public async Task<long> SumAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("""
            SELECT COALESCE(SUM(i.quantity), 0)::bigint
            FROM game.item_instances i
            WHERE i.template_id = $2
              AND (
                i.account_storage_owner = $1
                OR i.character_id IN (SELECT c.character_id FROM game.characters c WHERE c.account_id = $1)
              )
              AND i.location IN ('bag', 'storage', 'account_storage')
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(QunaItemId);
        var sum = await command.ExecuteScalarAsync(ct);
        return sum is long value ? value : 0;
    }
}
