using Aion.Core.Characters;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresCharacterRepository
{
    public async Task<CharacterState?> RecordLoginAsync(Guid accountId, Guid characterId, DateTimeOffset at, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE game.characters c SET last_login_at=$3
            FROM auth.accounts a
            WHERE c.account_id=$1 AND c.character_id=$2 AND a.account_id=c.account_id AND a.disabled_at IS NULL
            RETURNING {StateColumns}
            """);
        Scope(command, accountId, characterId);
        command.Parameters.AddWithValue(at);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadState(reader) : null;
    }

    public async Task<CharacterState?> RecordLogoutAsync(Guid accountId, Guid characterId, DateTimeOffset at, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE game.characters c SET last_logout_at=$3
            FROM auth.accounts a
            WHERE c.account_id=$1 AND c.character_id=$2 AND a.account_id=c.account_id AND a.disabled_at IS NULL
            RETURNING {StateColumns}
            """);
        Scope(command, accountId, characterId);
        command.Parameters.AddWithValue(at);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadState(reader) : null;
    }
}
