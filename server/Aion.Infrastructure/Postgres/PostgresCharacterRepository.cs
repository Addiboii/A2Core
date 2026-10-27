using System.Text.Json;
using Aion.Application.Characters;
using Aion.Core.Characters;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>One shared pooled data source; each operation borrows a connection.</summary>
public sealed partial class PostgresCharacterRepository(NpgsqlDataSource dataSource)
    : ICharacterRepository, ICharacterStateRepository
{
    private const string SummaryColumns = "c.character_id,c.account_id,c.world_id,c.name,c.level";
    private const string StateColumns = SummaryColumns +
        ",c.creation_id,c.race,c.gender,c.appearance,c.experience,c.map_id,c.position::text,c.resources::text,c.revision,c.wire_id,c.voice_type,c.lobby_slot,c.ascension::int,c.staff";

    public async Task<IReadOnlyList<CharacterSummary>> ListOwnedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {SummaryColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND a.disabled_at IS NULL ORDER BY c.created_at,c.character_id
            """);
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CharacterSummary>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<CharacterSummary?> FindOwnedAsync(Guid accountId, Guid characterId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {SummaryColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND c.character_id=$2 AND a.disabled_at IS NULL
            """);
        Scope(command, accountId, characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
    }

    public async Task<CharacterState?> LoadOwnedAsync(Guid accountId, Guid characterId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {StateColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND c.character_id=$2 AND a.disabled_at IS NULL
            """);
        Scope(command, accountId, characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<CharacterState?> CreateOwnedAsync(Guid accountId, NewCharacter character, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            INSERT INTO game.characters AS c
                (account_id,world_id,name,creation_id,race,gender,appearance,map_id,position,resources,voice_type,lobby_slot)
            SELECT account_id,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12 FROM auth.accounts
            WHERE account_id=$1 AND disabled_at IS NULL
            RETURNING {StateColumns}
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(character.WorldId);
        command.Parameters.AddWithValue(character.Name);
        command.Parameters.AddWithValue(character.CreationId);
        command.Parameters.AddWithValue(character.Race);
        command.Parameters.AddWithValue(character.Gender);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, character.Appearance);
        Map(command, character.MapId);
        Json(command, character.Position);
        Json(command, character.Resources);
        command.Parameters.AddWithValue(character.VoiceType);
        Map(command,character.LobbySlot);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation
                                             && error.ConstraintName == "character_name_idx")
        {
            throw new CharacterNameUnavailableException();
        }
    }

    public async Task<long?> SaveOwnedAsync(Guid accountId, Guid characterId, long expectedRevision,
        CharacterProgress progress, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE game.characters c SET level=$4,experience=$5,map_id=$6,position=$7,resources=$8
            FROM auth.accounts a WHERE c.account_id=$1 AND c.character_id=$2 AND c.revision=$3
            AND a.account_id=c.account_id AND a.disabled_at IS NULL RETURNING c.revision
            """);
        Scope(command, accountId, characterId);
        command.Parameters.AddWithValue(expectedRevision);
        command.Parameters.AddWithValue(progress.Level);
        command.Parameters.AddWithValue(progress.Experience);
        Map(command, progress.MapId);
        Json(command, progress.Position);
        Json(command, progress.Resources);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long revision ? revision : null;
    }

    private static void Scope(NpgsqlCommand command, Guid accountId, Guid characterId)
    {
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(characterId);
    }
    private static void Map(NpgsqlCommand command, int? map) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)map ?? DBNull.Value);
    private static void Json(NpgsqlCommand command, JsonElement json) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json.GetRawText());
    private static CharacterSummary ReadSummary(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4));
    private static CharacterState ReadState(NpgsqlDataReader reader) =>
        new(ReadSummary(reader), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetFieldValue<byte[]>(8), new CharacterProgress(reader.GetInt32(4), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                JsonSerializer.Deserialize<JsonElement>(reader.GetString(11)),
                JsonSerializer.Deserialize<JsonElement>(reader.GetString(12))), reader.GetInt64(13),
                reader.GetInt32(14),reader.GetInt32(15),reader.IsDBNull(16)?null:reader.GetInt32(16),reader.GetInt32(17),reader.GetBoolean(18));
}
