namespace Aion.World;

// The requests of the client-menu systems of feature/systems-3 (market, dungeon rooms, Arcana, ranking, Substance morph),
// routed from GameSession.HandleAsync in one place so the shared dispatch stays one line.
public sealed partial class GameSession
{
    async Task<List<byte[]>?> Systems3RequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (await MarketRequestAsync(plain, now, ct) is { } market) return market;
        if (DungeonRoomRequest(plain, now) is { } rooms) return rooms;
        if (await RankingRequestAsync(plain, now, ct) is { } ranking) return ranking;
        if (await ConversionRequestAsync(plain, now, ct) is { } conversion) return conversion;
        if (await ArcanaRequestAsync(plain, now, ct) is { } arcana) return arcana;
        return null;
    }
}
