using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

// Items, economy and legions (migrations 042-045). Ported from A2Core PR #4 (Artezianec) and rewritten where it failed:
// every multi-column read closes its reader before the next command, numeric columns are read with their own width,
// every UPDATE/DELETE that must hit a row checks its row count, and all parts of a change share one transaction.
public sealed partial class PostgresGameplayStore : IEconomyStore
{
    /// <summary>One row's columns (the reader is closed before returning), or null.</summary>
    private static async Task<object[]?> Row(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    {
        await using var q = Query(c, t, sql, args);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var values = new object[r.FieldCount];
        r.GetValues(values);
        return values;
    }

    private static async Task<List<object[]>> Rows(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    {
        var list = new List<object[]>();
        await using var q = Query(c, t, sql, args);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var values = new object[r.FieldCount];
            r.GetValues(values);
            list.Add(values);
        }
        return list;
    }

    private static async Task<string?> LatestBuild(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        await Scalar(c, t, "SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1", ct) as string;

    private static async Task GrantKina(NpgsqlConnection c, NpgsqlTransaction t, Guid character, long kina, CancellationToken ct)
    {
        if (kina <= 0) return;
        var build = await LatestBuild(c, t, ct) ?? throw new GameplayException("no_build");
        await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, 930100001, kina, Guid.NewGuid());
    }

    private static async Task<bool> PayAll(NpgsqlConnection c, NpgsqlTransaction t, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        foreach (var (cost, revision, count) in costs)
            if (count < 1 || !await Consume(c, t, character, cost, revision, count, ct)) return false;
        return true;
    }

    private static DateTimeOffset Utc(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => throw new InvalidCastException("timestamp"),
    };

    // ---------- Godstones ----------

    public async Task<IReadOnlyDictionary<int, int>> GodstonesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new Dictionary<int, int>();
        var map = (await Rows(c, t, "SELECT godstone_id,count FROM game.character_godstones WHERE character_id=$1", ct, character))
            .ToDictionary(r => (int)r[0], r => (int)r[1]);
        await t.CommitAsync(ct); return map;
    }

    private static Task AddGodstone(NpgsqlConnection c, NpgsqlTransaction t, Guid character, int id, int count, CancellationToken ct) =>
        Execute(c, t, """
            INSERT INTO game.character_godstones(character_id,godstone_id,count) VALUES($1,$2,$3)
            ON CONFLICT (character_id,godstone_id) DO UPDATE SET count=game.character_godstones.count+EXCLUDED.count, updated_at=now()
            """, ct, character, id, count);

    /// <summary>Takes count stones out of the collection; false when there are fewer.</summary>
    private static async Task<bool> TakeGodstone(NpgsqlConnection c, NpgsqlTransaction t, Guid character, int id, int count, CancellationToken ct)
    {
        if (count < 1) return false;
        if (await ExecuteCount(c, t, "UPDATE game.character_godstones SET count=count-$3, updated_at=now() WHERE character_id=$1 AND godstone_id=$2 AND count>$3",
                ct, character, id, count) == 1) return true;
        return await ExecuteCount(c, t, "DELETE FROM game.character_godstones WHERE character_id=$1 AND godstone_id=$2 AND count=$3", ct, character, id, count) == 1;
    }

    public async Task<bool> UseGodstoneItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int godstoneId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || !await Consume(c, t, character, item, revision, 1, ct)) return false;
        await AddGodstone(c, t, character, godstoneId, 1, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('godstone_acquired',$1,$2,$3)", ct, character, account.AccountId, Json(new { item, godstoneId }));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SocketGodstoneAsync(VerifiedAccount account, Guid character, Guid item, long revision, int slot, int slots, int godstoneId,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default)
    {
        if (slots < 1 || slot < 0 || slot >= slots) return false;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        if (await Scalar(c, t, "SELECT upgrades::text FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location IN ('bag','equipment') AND quantity=1 FOR UPDATE",
                ct, item, character, revision) is not string text) return false;
        var current = new int[slots];
        using (var upgrades = JsonDocument.Parse(text))
            if (upgrades.RootElement.TryGetProperty("godstones", out var stones) && stones.ValueKind == JsonValueKind.Array)
                foreach (var (stone, i) in stones.EnumerateArray().Select((s, i) => (s, i)).Where(x => x.i < slots))
                    current[i] = stone.TryGetInt32(out var n) ? n : 0;
        if (current[slot] != 0 || !await TakeGodstone(c, t, character, godstoneId, 1, ct) || !await PayAll(c, t, character, costs, ct)) return false;
        current[slot] = godstoneId;
        if (await ExecuteCount(c, t, "UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('godstones',$2::jsonb) WHERE instance_id=$1",
                ct, item, JsonSerializer.Serialize(current)) != 1) return false;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('godstone_socketed',$1,$2,$3)", ct, character, account.AccountId, Json(new { item, slot, godstoneId }));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> CombineGodstonesAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct = default)
    {
        if (materials.Count == 0 || materials.Values.Any(n => n < 1)) return false;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        foreach (var (id, count) in materials.OrderBy(m => m.Key))
            if (!await TakeGodstone(c, t, character, id, count, ct)) return false;
        if (rewardId > 0) await AddGodstone(c, t, character, rewardId, 1, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('godstone_combined',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { materials = materials.Select(m => new[] { m.Key, m.Value }), rewardId }));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> UpgradeItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        JsonElement upgrades, CancellationToken ct = default)
    {
        if (upgrades.ValueKind != JsonValueKind.Object) throw new GameplayException("invalid_upgrade");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || !await PayAll(c, t, character, costs, ct)) return false;
        if (await ExecuteCount(c, t, "UPDATE game.item_instances SET upgrades=upgrades||$4 WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location IN ('bag','equipment')",
                ct, item, character, revision, upgrades) != 1) return false;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_upgraded',$1,$2,$3)", ct, character, account.AccountId, Json(new { item, upgrades }));
        await t.CommitAsync(ct); return true;
    }

    // ---------- Legion warehouse and wallet ----------

    /// <summary>The character's legion and grade, the legion row locked (warehouse and wallet changes are serialised per legion).</summary>
    private static async Task<(long Guild, int Grade, string Name)?> Membership(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        if (await Row(c, t, "SELECT m.guild_id,m.grade,ch.name FROM game.guild_members m JOIN game.characters ch USING(character_id) WHERE m.character_id=$1", ct, character) is not { } row)
            return null;
        var guild = (long)row[0];
        if (await Scalar(c, t, "SELECT guild_id FROM game.guilds WHERE guild_id=$1 FOR UPDATE", ct, guild) is not long) return null;
        return (guild, (short)row[1], (string)row[2]);
    }

    private static async Task<List<GuildStorageEntry>> StorageItems(NpgsqlConnection c, NpgsqlTransaction t, long guild, CancellationToken ct)
    {
        var rows = await Rows(c, t, """
            SELECT i.instance_id,i.build_id,i.template_id,i.quantity,i.location,i.revision,i.rolled_stats::text,i.upgrades::text,i.wire_id,s.slot
            FROM game.guild_storage_items s JOIN game.item_instances i USING(instance_id) WHERE s.guild_id=$1 ORDER BY s.slot
            """, ct, guild);
        return rows.Select(r => new GuildStorageEntry(new ItemState((Guid)r[0], (string)r[1], (int)r[2], (long)r[3], (string)r[4], (int)r[9], (long)r[5],
            JsonSerializer.Deserialize<JsonElement>((string)r[6]), JsonSerializer.Deserialize<JsonElement>((string)r[7]), (long)r[8]), (int)r[9])).ToList();
    }

    public async Task<GuildStorageView?> GuildStorageAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return null;
        var head = await Row(c, t, "SELECT storage_extensions,storage_revision,storage_sorted_ms FROM game.guilds WHERE guild_id=$1", ct, member.Guild);
        var view = new GuildStorageView(await StorageItems(c, t, member.Guild, ct), (int)head![0], (long)head[1], (long)head[2]);
        await t.CommitAsync(ct); return view;
    }

    private static Task StorageHistory(NpgsqlConnection c, NpgsqlTransaction t, long guild, Guid character, string name, int type, int template, long count, CancellationToken ct) =>
        Execute(c, t, "INSERT INTO game.guild_storage_history(guild_id,character_id,char_name,record_type,template_id,quantity) VALUES($1,$2,$3,$4,$5,$6)",
            ct, guild, character, name, (short)type, template, count);

    public async Task<string> StoreGuildItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, int slots, int authorityBit, CancellationToken ct = default)
    {
        if (count < 1 || slots < 1) return "none";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return "authority";
        if (await Row(c, t, "SELECT quantity,template_id,(upgrades ? 'soul_bind') FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
                ct, item, character, revision) is not { } row) return "none";
        var (quantity, template, bound) = ((long)row[0], (int)row[1], (bool)row[2]);
        if (bound) return "bound";
        if (quantity < count) return "none";
        var used = (await Rows(c, t, "SELECT slot FROM game.guild_storage_items WHERE guild_id=$1", ct, member.Guild)).Select(r => (int)r[0]).ToHashSet();
        var slot = Enumerable.Range(0, slots).Cast<int?>().FirstOrDefault(s => !used.Contains(s!.Value));
        if (slot is null) return "full";
        Guid stored;
        if (quantity == count)
        {
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET location='guild_storage',slot=NULL WHERE instance_id=$1", ct, item) != 1) return "none";
            stored = item;
        }
        else
        {
            // Part of a stack: the stored units become their own row (same template, stats and upgrades); the bag row keeps the rest.
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1 AND quantity>$2", ct, item, count) != 1) return "none";
            stored = (Guid)(await Scalar(c, t, """
                INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats,upgrades)
                SELECT character_id,build_id,template_id,$2,'guild_storage',rolled_stats,upgrades FROM game.item_instances WHERE instance_id=$1 RETURNING instance_id
                """, ct, item, count))!;
        }
        await Execute(c, t, "INSERT INTO game.guild_storage_items(instance_id,guild_id,slot) VALUES($1,$2,$3)", ct, stored, member.Guild, slot.Value);
        await Execute(c, t, "UPDATE game.guilds SET storage_revision=storage_revision+1 WHERE guild_id=$1", ct, member.Guild);
        await StorageHistory(c, t, member.Guild, character, member.Name, 1, template, count, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_storage_store',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { guild = member.Guild, item = stored, template, count }));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> TakeGuildItemAsync(VerifiedAccount account, Guid character, Guid item, long count, int authorityBit, CancellationToken ct = default)
    {
        if (count < 1) return "none";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return "authority";
        // The warehouse row is locked: a second taker waits, then finds it gone or smaller (no double take).
        if (await Row(c, t, """
                SELECT i.quantity,i.template_id,i.build_id,(i.upgrades='{}'::jsonb AND i.rolled_stats='{}'::jsonb) FROM game.guild_storage_items s
                JOIN game.item_instances i USING(instance_id) WHERE s.instance_id=$1 AND s.guild_id=$2 AND i.location='guild_storage' FOR UPDATE OF s, i
                """, ct, item, member.Guild) is not { } row) return "none";
        var (quantity, template, build, plain) = ((long)row[0], (int)row[1], (string)row[2], (bool)row[3]);
        if (quantity < count || (!plain && count != quantity)) return "none";
        if (plain)
        {
            // A plain stack joins the taker's own stacks (game.grant_item tops them up).
            if (count == quantity)
            {
                if (await ExecuteCount(c, t, "DELETE FROM game.guild_storage_items WHERE instance_id=$1", ct, item) != 1) return "none";
                if (await ExecuteCount(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1 AND location='guild_storage'", ct, item) != 1) return "none";
            }
            else if (await ExecuteCount(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1 AND quantity>$2", ct, item, count) != 1) return "none";
            await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, template, count, Guid.NewGuid());
        }
        else
        {
            // An item with upgrades moves whole: enchant, godstones, rolled stats and binding stay on the instance.
            if (await ExecuteCount(c, t, "DELETE FROM game.guild_storage_items WHERE instance_id=$1", ct, item) != 1) return "none";
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET character_id=$2,location='bag',slot=NULL WHERE instance_id=$1 AND location='guild_storage'", ct, item, character) != 1)
                return "none";
        }
        await Execute(c, t, "UPDATE game.guilds SET storage_revision=storage_revision+1 WHERE guild_id=$1", ct, member.Guild);
        await StorageHistory(c, t, member.Guild, character, member.Name, 2, template, count, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_storage_take',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { guild = member.Guild, item, template, count }));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> ExtendGuildStorageAsync(VerifiedAccount account, Guid character, int expected, int maxExtensions, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int authorityBit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return "authority";
        if (expected > maxExtensions) return "max";
        if (await ExecuteCount(c, t, "UPDATE game.guilds SET storage_extensions=$2,storage_revision=storage_revision+1 WHERE guild_id=$1 AND storage_extensions=$2-1",
                ct, member.Guild, expected) != 1) return "none";
        if (!await PayAll(c, t, character, costs, ct)) return "money";
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<bool> SortGuildStorageAsync(VerifiedAccount account, Guid character, long nowMs, long cooldownMs, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member
            || !GuildAuthority.Can(member.Grade, GuildAuthority.StorageSort)) return false;
        if (await ExecuteCount(c, t, "UPDATE game.guilds SET storage_sorted_ms=$2,storage_revision=storage_revision+1 WHERE guild_id=$1 AND storage_sorted_ms<=$2-$3",
                ct, member.Guild, nowMs, cooldownMs) != 1) return false;
        var order = (await Rows(c, t, """
            SELECT s.instance_id FROM game.guild_storage_items s JOIN game.item_instances i USING(instance_id)
            WHERE s.guild_id=$1 ORDER BY i.template_id,i.created_at,i.instance_id
            """, ct, member.Guild)).Select(r => (Guid)r[0]).ToList();
        // Two passes so the (guild, slot) unique key never collides mid-sort.
        await Execute(c, t, "UPDATE game.guild_storage_items SET slot=slot+1000000 WHERE guild_id=$1", ct, member.Guild);
        for (var i = 0; i < order.Count; i++) await Execute(c, t, "UPDATE game.guild_storage_items SET slot=$2 WHERE instance_id=$1", ct, order[i], i);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<GuildStorageRecord>> GuildStorageHistoryAsync(VerifiedAccount account, Guid character, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return [];
        var list = (await Rows(c, t, "SELECT char_name,record_type,template_id,quantity,recorded_at FROM game.guild_storage_history WHERE guild_id=$1 AND recorded_at>=$2 ORDER BY recorded_at DESC LIMIT 300",
            ct, member.Guild, since)).Select(r => new GuildStorageRecord((string)r[0], (short)r[1], (int)r[2], (long)r[3], Utc(r[4]))).ToList();
        await t.CommitAsync(ct); return list;
    }

    public async Task<long?> GuildWalletAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return null;
        var amount = await Scalar(c, t, "SELECT amount FROM game.guild_wallet WHERE guild_id=$1 AND wallet_type=1", ct, member.Guild) as long? ?? 0;
        await t.CommitAsync(ct); return amount;
    }

    /// <summary>amount = amount + delta in one statement; a spend larger than the wallet changes nothing (null).</summary>
    private static async Task<long?> WalletAdd(NpgsqlConnection c, NpgsqlTransaction t, long guild, long delta, CancellationToken ct)
    {
        if (delta >= 0)
            return await Scalar(c, t, """
                INSERT INTO game.guild_wallet(guild_id,wallet_type,amount) VALUES($1,1,$2)
                ON CONFLICT (guild_id,wallet_type) DO UPDATE SET amount=game.guild_wallet.amount+EXCLUDED.amount RETURNING amount
                """, ct, guild, delta) as long?;
        return await Scalar(c, t, "UPDATE game.guild_wallet SET amount=amount+$2 WHERE guild_id=$1 AND wallet_type=1 AND amount+$2>=0 RETURNING amount", ct, guild, delta) as long?;
    }

    public async Task<long?> AddGuildWalletAsync(VerifiedAccount account, Guid character, long delta, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return null;
        if (delta < 0 && member.Grade != 0) return null;
        if (await WalletAdd(c, t, member.Guild, delta, ct) is not { } amount) return null;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_wallet',$1,$2,$3)", ct, character, account.AccountId, Json(new { guild = member.Guild, delta, amount }));
        await t.CommitAsync(ct); return amount;
    }

    public async Task<(string Result, int Left)> GuildDonateAsync(VerifiedAccount account, Guid character, DateOnly day, int perDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        long essence, long exp, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return ("none", 0);
        var done = await Scalar(c, t, """
            UPDATE game.guild_members SET donations=CASE WHEN donation_day=$2 THEN donations+1 ELSE 1 END, donation_day=$2
            WHERE character_id=$1 AND (donation_day IS DISTINCT FROM $2 OR donations<$3) RETURNING donations
            """, ct, character, day, perDay) as int?;
        if (done is not { } used) return ("limit", 0);
        if (!await PayAll(c, t, character, costs, ct)) return ("money", perDay - used + 1);
        await WalletAdd(c, t, member.Guild, essence, ct);
        await Execute(c, t, "UPDATE game.guilds SET exp=exp+$2 WHERE guild_id=$1", ct, member.Guild, exp);
        await t.CommitAsync(ct); return ("ok", Math.Max(0, perDay - used));
    }

    public async Task<int> GuildDonationsLeftAsync(VerifiedAccount account, Guid character, DateOnly day, int perDay, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return 0;
        var row = await Row(c, t, "SELECT donation_day,donations FROM game.guild_members WHERE character_id=$1", ct, character);
        await t.CommitAsync(ct);
        if (row is null) return 0;
        return row[0] is DateOnly last && last == day ? Math.Max(0, perDay - (int)row[1]) : row[0] is DateTime d && DateOnly.FromDateTime(d) == day ? Math.Max(0, perDay - (int)row[1]) : perDay;
    }

    // ---------- Officers and applications ----------

    public async Task<string> SetGuildTextAsync(VerifiedAccount account, Guid character, string field, string value, int authorityBit, CancellationToken ct = default)
    {
        var column = field switch { "notice" => "notice", "intro" => "intro", _ => throw new GameplayException("invalid_field") };
        if (value.Length > 500) return "none";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return "authority";
        await Execute(c, t, $"UPDATE game.guilds SET {column}=$2 WHERE guild_id=$1", ct, member.Guild, value);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> SetGuildJoinRuleAsync(VerifiedAccount account, Guid character, int rule, string password, int authorityBit, CancellationToken ct = default)
    {
        if (rule is < 0 or > 3 || password.Length > 32) return "none";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return "authority";
        await Execute(c, t, "UPDATE game.guilds SET join_rule=$2,password=$3 WHERE guild_id=$1", ct, member.Guild, (short)rule, rule == 3 ? password : "");
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<bool> SetGuildCommentAsync(VerifiedAccount account, Guid character, string comment, CancellationToken ct = default)
    {
        if (comment.Length > 100) return false;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        var done = await ExecuteCount(c, t, "UPDATE game.guild_members SET comment=$2 WHERE character_id=$1", ct, character, comment) == 1;
        await t.CommitAsync(ct); return done;
    }

    private static async Task<(Guid Id, int Grade)?> MemberByName(NpgsqlConnection c, NpgsqlTransaction t, long guild, string nickname, CancellationToken ct) =>
        await Row(c, t, "SELECT m.character_id,m.grade FROM game.guild_members m JOIN game.characters ch USING(character_id) WHERE m.guild_id=$1 AND lower(ch.name)=lower($2) FOR UPDATE OF m",
            ct, guild, nickname) is { } row ? ((Guid)row[0], (short)row[1]) : null;

    public async Task<string> SetGuildGradeAsync(VerifiedAccount account, Guid character, string nickname, int grade, int maxOfficers, CancellationToken ct = default)
    {
        if (grade is not (1 or 2)) return "none";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (!GuildAuthority.Can(member.Grade, GuildAuthority.ChangeGrade)) return "authority";
        if (await MemberByName(c, t, member.Guild, nickname, ct) is not { } target || target.Id == character) return "none";
        if (target.Grade <= member.Grade) return "authority";
        if (grade == 1 && target.Grade != 1 && await Scalar(c, t, "SELECT count(*) FROM game.guild_members WHERE guild_id=$1 AND grade=1", ct, member.Guild) is long officers && officers >= maxOfficers)
            return "full";
        await Execute(c, t, "UPDATE game.guild_members SET grade=$2 WHERE character_id=$1", ct, target.Id, (short)grade);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> DelegateGuildMasterAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return "none";
        if (member.Grade != 0) return "authority";
        if (await MemberByName(c, t, member.Guild, nickname, ct) is not { } target || target.Id == character) return "none";
        await Execute(c, t, "UPDATE game.guild_members SET grade=1 WHERE character_id=$1", ct, character);
        await Execute(c, t, "UPDATE game.guild_members SET grade=0 WHERE character_id=$1", ct, target.Id);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_master_delegated',$1,$2,$3)", ct, character, account.AccountId, Json(new { guild = member.Guild, to = target.Id }));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> RequestGuildJoinAsync(VerifiedAccount account, Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct = default)
    {
        if (comment.Length > 100) comment = comment[..100];
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        if (await Scalar(c, t, "SELECT guild_id FROM game.guild_members WHERE character_id=$1", ct, character) is long) return "member";
        if (await Scalar(c, t, "SELECT g.guild_id FROM game.guilds g JOIN game.characters a ON a.character_id=$2 WHERE g.guild_id=$1 AND g.join_rule=1 FOR UPDATE OF g", ct, guild, character) is not long) return "none";
        if (await Scalar(c, t, "SELECT 1 FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2", ct, character, guild) is not null) return "requested";
        if (await Scalar(c, t, "SELECT count(*) FROM game.guild_join_requests WHERE character_id=$1", ct, character) is long sent && sent >= maxSent) return "limit";
        if (await Scalar(c, t, "SELECT count(*) FROM game.guild_join_requests WHERE guild_id=$1", ct, guild) is long received && received >= maxReceived) return "full_requests";
        await Execute(c, t, "INSERT INTO game.guild_join_requests(character_id,guild_id,comment) VALUES($1,$2,$3)", ct, character, guild, comment);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<bool> CancelGuildJoinAsync(VerifiedAccount account, Guid character, long guild, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        var done = await ExecuteCount(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2", ct, character, guild) == 1;
        await t.CommitAsync(ct); return done;
    }

    public async Task<IReadOnlyList<GuildJoinRequest>> GuildJoinRequestsAsync(VerifiedAccount account, Guid character, int authorityBit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member || !GuildAuthority.Can(member.Grade, authorityBit)) return [];
        var list = (await Rows(c, t, """
            SELECT r.character_id,ch.name,ch.level,ch.wire_id,r.comment,r.requested_at FROM game.guild_join_requests r JOIN game.characters ch USING(character_id)
            WHERE r.guild_id=$1 ORDER BY r.requested_at
            """, ct, member.Guild)).Select(r => new GuildJoinRequest((Guid)r[0], (string)r[1], (int)r[2], (int)r[3], (string)r[4], Utc(r[5]))).ToList();
        await t.CommitAsync(ct); return list;
    }

    public async Task<IReadOnlyList<GuildView>> SentGuildJoinRequestsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var ids = (await Rows(c, t, "SELECT guild_id FROM game.guild_join_requests WHERE character_id=$1 ORDER BY requested_at", ct, character)).Select(r => (long)r[0]).ToList();
        var views = new List<GuildView>();
        foreach (var id in ids) if (await Guild(c, t, id, ct) is { } view) views.Add(view);
        await t.CommitAsync(ct); return views;
    }

    public async Task<(string Result, Guid? Applicant)> AnswerGuildJoinAsync(VerifiedAccount account, Guid character, string nickname, bool accept, int maxMembers, int authorityBit,
        CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return ("none", null);
        if (!GuildAuthority.Can(member.Grade, authorityBit)) return ("authority", null);
        if (await Scalar(c, t, "SELECT r.character_id FROM game.guild_join_requests r JOIN game.characters ch USING(character_id) WHERE r.guild_id=$1 AND lower(ch.name)=lower($2) FOR UPDATE OF r",
                ct, member.Guild, nickname) is not Guid applicant) return ("none", null);
        if (await ExecuteCount(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2", ct, applicant, member.Guild) != 1) return ("none", null);
        if (accept)
        {
            if (await Scalar(c, t, "SELECT count(*) FROM game.guild_members WHERE guild_id=$1", ct, member.Guild) is long count && count >= maxMembers) return ("full", applicant);
            if (await ExecuteCount(c, t, "INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,2) ON CONFLICT (character_id) DO NOTHING", ct, applicant, member.Guild) != 1)
            {
                await t.CommitAsync(ct); return ("member", applicant);
            }
            await Execute(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1", ct, applicant);
        }
        await t.CommitAsync(ct); return ("ok", applicant);
    }

    public async Task<(string Result, int Today, int Yesterday)> GuildAttendanceAsync(VerifiedAccount account, Guid character, DateOnly day, bool attend, long expGain, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } member) return ("none", 0, 0);
        // Roll the legion's counts over at the game-day boundary (yesterday = today's count only when the last day was the day before).
        await Execute(c, t, """
            UPDATE game.guilds SET attendance_yesterday=CASE WHEN attendance_day=$2::date-1 THEN attendance_today ELSE 0 END, attendance_today=0, attendance_day=$2
            WHERE guild_id=$1 AND attendance_day IS DISTINCT FROM $2
            """, ct, member.Guild, day);
        var result = "done";
        if (attend && await ExecuteCount(c, t, "UPDATE game.guild_members SET attendance_day=$2 WHERE character_id=$1 AND attendance_day IS DISTINCT FROM $2", ct, character, day) == 1)
        {
            await Execute(c, t, "UPDATE game.guilds SET attendance_today=attendance_today+1, exp=exp+$2 WHERE guild_id=$1", ct, member.Guild, expGain);
            result = "ok";
        }
        else if (!attend && await Scalar(c, t, "SELECT 1 FROM game.guild_members WHERE character_id=$1 AND attendance_day IS DISTINCT FROM $2", ct, character, day) is not null) result = "ok";
        var counts = await Row(c, t, "SELECT attendance_today,attendance_yesterday FROM game.guilds WHERE guild_id=$1", ct, member.Guild);
        await t.CommitAsync(ct); return (result, (int)counts![0], (int)counts[1]);
    }

    // ---------- Broker ----------

    private static TradeListing Listing(object[] r, DateTimeOffset now)
    {
        var (sold, cancelled, expires) = ((bool)r[8], (bool)r[9], Utc(r[7]));
        var state = sold ? "sold" : cancelled ? "cancelled" : expires <= now ? "expired" : "active";
        return new TradeListing((long)r[0], (Guid)r[1], r[2] is Guid item ? item : Guid.Empty, (int)r[3], (long)r[4], (long)r[5], Utc(r[6]), expires, state);
    }

    const string ListingColumns = "sale_id,seller_id,instance_id,template_id,quantity,unit_price,listed_at,expires_at,sold,cancelled";

    public async Task<TradeListing?> TradeSellAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, long unitPrice,
        IReadOnlyList<(Guid Item, long Revision, int Count)> fee, DateTimeOffset expires, CancellationToken ct = default)
    {
        if (count < 1 || unitPrice < 1) return null;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        if (await Row(c, t, "SELECT quantity,template_id,(upgrades ? 'soul_bind') FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
                ct, item, character, revision) is not { } row) return null;
        var (quantity, template, bound) = ((long)row[0], (int)row[1], (bool)row[2]);
        if (bound || quantity < count || fee.Any(f => f.Item == item)) return null;
        Guid escrow;
        if (quantity == count)
        {
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET location='trade_escrow',slot=NULL WHERE instance_id=$1", ct, item) != 1) return null;
            escrow = item;
        }
        else
        {
            // Part of a stack: the listed units are split into their own escrow row; the bag keeps the rest (never both).
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1 AND quantity>$2", ct, item, count) != 1) return null;
            escrow = (Guid)(await Scalar(c, t, """
                INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats,upgrades)
                SELECT character_id,build_id,template_id,$2,'trade_escrow',rolled_stats,upgrades FROM game.item_instances WHERE instance_id=$1 RETURNING instance_id
                """, ct, item, count))!;
        }
        if (!await PayAll(c, t, character, fee, ct)) return null;
        var listing = await Row(c, t, $"INSERT INTO game.trade_listings(seller_id,instance_id,template_id,quantity,unit_price,expires_at) VALUES($1,$2,$3,$4,$5,$6) RETURNING {ListingColumns}",
            ct, character, escrow, template, count, unitPrice, expires);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('trade_list',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { sale = (long)listing![0], item = escrow, template, count, unitPrice, fee = fee.Sum(f => (long)f.Count) }));
        await t.CommitAsync(ct); return Listing(listing, DateTimeOffset.UtcNow);
    }

    public async Task<TradeListing?> TradeCancelAsync(VerifiedAccount account, Guid character, long saleId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        if (await Row(c, t, $"SELECT {ListingColumns} FROM game.trade_listings WHERE sale_id=$1 AND seller_id=$2 AND NOT sold AND NOT cancelled FOR UPDATE", ct, saleId, character) is not { } row)
            return null;
        var listing = Listing(row, DateTimeOffset.UtcNow);
        if (await ExecuteCount(c, t, "UPDATE game.trade_listings SET cancelled=true WHERE sale_id=$1 AND NOT sold AND NOT cancelled", ct, saleId) != 1) return null;
        if (await ExecuteCount(c, t, "UPDATE game.item_instances SET location='bag',slot=NULL WHERE instance_id=$1 AND character_id=$2 AND location='trade_escrow'", ct, listing.Item, character) != 1)
            return null;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('trade_cancel',$1,$2,$3)", ct, character, account.AccountId, Json(new { sale = saleId }));
        await t.CommitAsync(ct); return listing;
    }

    public async Task<(string Result, TradeListing? Listing)> TradePurchaseAsync(VerifiedAccount account, Guid character, long saleId, long count, long expectedUnitPrice,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default)
    {
        if (count < 1) return ("count", null);
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return ("none", null);
        if (await Row(c, t, $"SELECT {ListingColumns} FROM game.trade_listings WHERE sale_id=$1 AND NOT sold AND NOT cancelled AND expires_at>now() FOR UPDATE", ct, saleId) is not { } row)
            return ("none", null);
        var listing = Listing(row, DateTimeOffset.UtcNow);
        if (listing.Seller == character || listing.UnitPrice != expectedUnitPrice) return ("none", listing);
        if (count > listing.Quantity) return ("count", listing);
        long total;
        try { total = checked(listing.UnitPrice * count); } catch (OverflowException) { return ("money", listing); }
        if (costs.Sum(x => (long)x.Count) != total || !await PayAll(c, t, character, costs, ct)) return ("money", listing);
        if (await Row(c, t, "SELECT build_id,(upgrades='{}'::jsonb AND rolled_stats='{}'::jsonb),quantity FROM game.item_instances WHERE instance_id=$1 AND location='trade_escrow' FOR UPDATE",
                ct, listing.Item) is not { } item || (long)item[2] != listing.Quantity) return ("none", listing);
        var (build, plain) = ((string)item[0], (bool)item[1]);
        if (count == listing.Quantity)
        {
            // The listing is closed first: deleting its escrow row sets instance_id to NULL, which an open listing may not have.
            await Execute(c, t, "UPDATE game.trade_listings SET sold=true,quantity=0,instance_id=NULL WHERE sale_id=$1", ct, saleId);
            if (plain)
            {
                if (await ExecuteCount(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1 AND location='trade_escrow'", ct, listing.Item) != 1) return ("none", listing);
                await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, listing.TemplateId, count, Guid.NewGuid());
            }
            else if (await ExecuteCount(c, t, "UPDATE game.item_instances SET character_id=$2,location='bag',slot=NULL WHERE instance_id=$1 AND location='trade_escrow'", ct, listing.Item, character) != 1)
                return ("none", listing);
        }
        else
        {
            // Part of the listing: the escrow row shrinks, the buyer gets the units (a stack; equipment never lists more than one).
            if (!plain) return ("count", listing);
            if (await ExecuteCount(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1 AND quantity>$2", ct, listing.Item, count) != 1) return ("none", listing);
            await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, listing.TemplateId, count, Guid.NewGuid());
            await Execute(c, t, "UPDATE game.trade_listings SET quantity=quantity-$2 WHERE sale_id=$1", ct, saleId, count);
        }
        await Execute(c, t, "INSERT INTO game.trade_deliveries(seller_id,sale_id,kina,template_id,quantity) VALUES($1,$2,$3,$4,$5)", ct, listing.Seller, saleId, total, listing.TemplateId, count);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('trade_purchase',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { sale = saleId, seller = listing.Seller, count, total }));
        await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, listing.Seller.ToString());
        await t.CommitAsync(ct); return ("ok", listing with { Quantity = listing.Quantity - count, State = count == listing.Quantity ? "sold" : "active" });
    }

    public async Task<TradeListing?> TradeListingAsync(VerifiedAccount account, long saleId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var row = await Row(c, t, $"SELECT {ListingColumns} FROM game.trade_listings WHERE sale_id=$1", ct, saleId);
        await t.CommitAsync(ct);
        return row is null ? null : Listing(row, DateTimeOffset.UtcNow);
    }

    public async Task<(IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries)> TradeSummaryAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return ([], []);
        var now = DateTimeOffset.UtcNow;
        var listings = (await Rows(c, t, $"SELECT {ListingColumns} FROM game.trade_listings WHERE seller_id=$1 AND NOT sold AND NOT cancelled ORDER BY sale_id", ct, character))
            .Select(r => Listing(r, now)).ToList();
        var deliveries = (await Rows(c, t, "SELECT delivery_id,kina,template_id,quantity,created_at FROM game.trade_deliveries WHERE seller_id=$1 AND completed_at IS NULL ORDER BY delivery_id", ct, character))
            .Select(r => new TradeDelivery((long)r[0], (long)r[1], (int)r[2], (long)r[3], Utc(r[4]))).ToList();
        await t.CommitAsync(ct); return (listings, deliveries);
    }

    public async Task<long?> CompleteTradeDeliveryAsync(VerifiedAccount account, Guid character, long deliveryId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        if (await Scalar(c, t, "UPDATE game.trade_deliveries SET completed_at=now() WHERE delivery_id=$1 AND seller_id=$2 AND completed_at IS NULL RETURNING kina", ct, deliveryId, character) is not long kina)
            return null;
        await GrantKina(c, t, character, kina, ct);
        await t.CommitAsync(ct); return kina;
    }

    // ---------- Gift box ----------

    public async Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(VerifiedAccount account, Guid character, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var list = (await Rows(c, t, """
            SELECT p.push_reward_id,p.reward_type,p.sender,p.title,p.body,p.item_template_id,p.item_count,p.sent_at,p.expire_at,(r.character_id IS NOT NULL)
            FROM game.push_rewards p LEFT JOIN game.character_push_rewards r ON r.push_reward_id=p.push_reward_id AND r.character_id=$1
            WHERE p.sent_at<=$2 AND p.expire_at>$2 ORDER BY p.push_reward_id
            """, ct, character, now)).Select(r => new PushRewardRecord((int)r[0], (short)r[1], (string)r[2], (string)r[3], (string)r[4], (int)r[5], (long)r[6], Utc(r[7]), Utc(r[8]), (bool)r[9])).ToList();
        await t.CommitAsync(ct); return list;
    }

    public async Task<PushRewardRecord?> TakePushRewardAsync(VerifiedAccount account, Guid character, int id, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        if (await Row(c, t, "SELECT push_reward_id,reward_type,sender,title,body,item_template_id,item_count,sent_at,expire_at FROM game.push_rewards WHERE push_reward_id=$1 AND sent_at<=$2 AND expire_at>$2",
                ct, id, now) is not { } r) return null;
        if (await ExecuteCount(c, t, "INSERT INTO game.character_push_rewards(character_id,push_reward_id) VALUES($1,$2) ON CONFLICT DO NOTHING", ct, character, id) != 1) return null;
        var reward = new PushRewardRecord((int)r[0], (short)r[1], (string)r[2], (string)r[3], (string)r[4], (int)r[5], (long)r[6], Utc(r[7]), Utc(r[8]), true);
        await GrantRewards(c, t, character, [(reward.TemplateId, reward.Count)], ct);
        await t.CommitAsync(ct); return reward;
    }
}
