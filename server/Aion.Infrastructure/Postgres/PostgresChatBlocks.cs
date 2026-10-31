using Aion.Application.Chat;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

/// <summary>Friend-list blocks (game.character_blocks) for chat whispers: blocked either way. Ported from
/// A2Core PR #4 (Artezianec), asynchronous instead of a blocking wait on the request thread.</summary>
public sealed class PostgresChatBlocks(NpgsqlDataSource source) : IChatBlockList
{
    public async Task<bool> BlockedAsync(Guid character, Guid other, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT 1 FROM game.character_blocks
            WHERE (owner_id=$1 AND blocked_id=$2) OR (owner_id=$2 AND blocked_id=$1) LIMIT 1
            """);
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(other);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
