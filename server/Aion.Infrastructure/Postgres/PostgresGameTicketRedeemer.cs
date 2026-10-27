using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed class PostgresGameTicketRedeemer(NpgsqlDataSource source) : IGameTicketRedeemer
{
    public async Task<VerifiedAccount?> RedeemAsync(string token, int worldId, CancellationToken cancellationToken)
    {
        if (!PostgresAccountAuthentication.ValidToken(token)) return null;
        await using var command = source.CreateCommand("SELECT auth.consume_game_ticket($1,$2)");
        command.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        command.Parameters.AddWithValue(worldId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid account ? new(account) : null;
    }
}
