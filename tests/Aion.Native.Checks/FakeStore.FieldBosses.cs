// The field boss kill rows (game.field_boss_kills) in memory, with the rule of PostgresGameplayStore: the newer kill wins.
sealed partial class FakeStore
{
    public List<(int Map, int Id, long KilledAt, long RespawnAt)> FieldBossRows { get; } = [];

    public Task<IReadOnlyList<(int Map, int Id, long KilledAt, long RespawnAt)>> FieldBossKillsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int Map, int Id, long KilledAt, long RespawnAt)>>(FieldBossRows.ToList());

    public Task<bool> SaveFieldBossKillAsync(int map, int id, long killedAt, long respawnAt, CancellationToken ct)
    {
        var at = FieldBossRows.FindIndex(r => r.Map == map && r.Id == id);
        if (at >= 0 && FieldBossRows[at].KilledAt > killedAt) return Task.FromResult(false);
        if (at >= 0) FieldBossRows[at] = (map, id, killedAt, respawnAt);
        else FieldBossRows.Add((map, id, killedAt, respawnAt));
        return Task.FromResult(true);
    }
}
