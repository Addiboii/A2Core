using System.Text;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Four worlds (feature/four-worlds, a2core-log/four-worlds-2026-11-12.md): the catalog (ids, races, pairs, names as one setting),
/// the server list with the population push tags, the login port listing each character on its own world, the game port's lobby
/// and creation per picked world, the official bytes of ArenaMatchingInfo_NT and MyCharAppear's server / match server ids, the
/// world copies of the open-world maps and the shared inter-server maps, and migration 093.
/// </summary>
static class WorldChecks
{
    const string AccountSession = "1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b";
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }

    static byte[] GameLogin => Strings(0x3613, AccountSession, "", "t", "").Concat(new byte[9]).ToArray();
    static byte[] Create(string name, int creation, byte slot) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(slot).U8(0).ToArray();

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    public static async Task Run(GameData data, long now, Action<bool, string> check)
    {
        // ---------- The catalog ----------
        var worlds = WorldCatalog.Default();
        check(worlds.Worlds.Select(w => (w.Id, w.Race, w.Pair)).SequenceEqual([(1361, 1, 0), (1362, 1, 1), (2361, 2, 0), (2362, 2, 1)])
              && worlds.Worlds.Select(w => w.Name).SequenceEqual(WorldCatalog.DefaultNames)
              && worlds.Of(1362)!.New && worlds.Of(2362)!.New && !worlds.Of(1361)!.New && !worlds.Of(2361)!.New,
              "four worlds: Light 1361 (world 1, the characters of before) and 1362 (new), Dark 2361 (world 1) and 2362 (new); pairs 1361+2361 and 1362+2362");
        check(worlds.HomeOf(null, 1) == 1361 && worlds.HomeOf(null, 2) == 2361 && worlds.HomeOf(1362, 1) == 1362 && worlds.HomeOf(1362, 2) == 2361
              && worlds.HomeOf(4301, 1) == 1361 && worlds.First(2).Id == 2361,
              "a character's world: its stored world when it is a world of its race, else world 1 of its race (existing characters stay there)");
        check(worlds.MatchOf(1361) == 2361 && worlds.MatchOf(2362) == 1362 && worlds.ArenaPair(2361) == (1361, 2361) && worlds.ArenaPair(1362) == (1362, 2362),
              "the match server is the other race's world of the pair (OFFICIAL: a server of the other race), the arena pair one Light and one Dark server");
        var renamed = WorldCatalog.FromSettings("Lumenhold, Dawnspire ,Ravenmoor,Duskrend", null, "2362", 300);
        check(renamed.Worlds.Select(w => w.Name).SequenceEqual(["Lumenhold", "Dawnspire", "Ravenmoor", "Duskrend"]) && renamed.Of(2362)!.CreationClosed
              && renamed.Capacity == 300 && renamed.Worlds.Select(w => w.Id).SequenceEqual(WorldCatalog.DefaultIds),
              "the names are one setting (Aion:Worlds:Names, Light1,Light2,Dark1,Dark2); ids never change; Aion:Worlds:Closed closes a world to creation");
        check(Throws(() => WorldCatalog.FromSettings("A,B,C", null, null, null)) && Throws(() => new WorldCatalog([new(2361, 1, "X", 0), new(2362, 2, "Y", 0)]))
              && Throws(() => new WorldCatalog([new(1361, 1, "X", 0), new(1361, 1, "Y", 0), new(2361, 2, "Z", 0)]))
              && Throws(() => new WorldCatalog([new(1361, 1, "X", 0)])),
              "bad world settings stop the host: not four names, a Light world outside 1xxx, a repeated id, a race without a world");

        // ---------- The server list: population push ----------
        var quiet = worlds.Rows();
        check(quiet.Select(r => (r.Id, r.Group, r.Status, r.Load)).SequenceEqual([(1361, 1, (byte)0, (byte)0), (1362, 1, (byte)3, (byte)0), (2361, 2, (byte)0, (byte)0), (2362, 2, (byte)3, (byte)0)]),
              "an empty host lists the new worlds as BrandNew + Recommend (EServerTag 1|2) and the worlds of before untagged, Light first");
        var busy = worlds.Rows(id => id switch { 1361 => 10, 1362 => 400, 2361 => 0, _ => 1 });
        check(busy.Select(r => r.Status).SequenceEqual(new byte[] { 2, 1, 2, 1 }) && busy.Select(r => r.Load).SequenceEqual(new byte[] { 2, 80, 0, 0 }),
              "the push follows the players: the emptier world of each race is Recommended (the new world keeps New), congestion = online / capacity %");
        check(renamed.Rows().Single(r => r.Id == 2362).Status == (WorldCatalog.TagBrandNew | WorldCatalog.TagProhibitCreation)
              && renamed.Rows().Single(r => r.Id == 2361).Status == 0,
              "a world closed to creation carries ProhibitCreation and is never Recommended; its race's only open world gets no Recommend either");
        var directory = LoginPackets.WorldDirectory(worlds.Rows());
        var name = Encoding.UTF8.GetBytes(WorldCatalog.DefaultNames[0]);
        var row0 = new PacketWriter().U16(0x3909).U16(0).U8(4).U16(1361).U8(1).U16(1361).U16(0).U8(name.Length).Raw(name).U8(0x55).U8(0).U8(0).U8(0x50).U8(1).U8(0).ToArray();
        check(directory.AsSpan(0, row0.Length).SequenceEqual(row0) && directory[^2..].SequenceEqual(new byte[] { 1, 1 })
              && WorldCatalog.DefaultNames.All(n => directory.AsSpan().IndexOf(Encoding.UTF8.GetBytes(n)) > 0),
              "0x3909 lists the four worlds in the observed row layout (id, race, data id, name, running bits 0x55, tags, congestion, 0x50, group 1)");

        // ---------- The login port ----------
        var picks = new WorldChoices();
        var login = new LoginSession(new LoginSessionOptions
        {
            Worlds = worlds.Rows(), Directory = () => worlds.Rows(), WorldPicked = picks.Pick, ServerPicked = picks.PickServer, LastPickedServer = picks.Server,
        }, (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", Guid.NewGuid(), [("OnOne", 3, 1), ("OnTwo", 5, 1), ("OldDark", 7, 2)])
            { Servers = [null, 1362, 2361] }));
        var account = (await login.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), now)).Replies.Single();
        var listed = (await login.HandleAsync([0x0a, 0x39], now)).Replies.Single();
        check(listed.SequenceEqual(LoginPackets.Characters([new LoginCharacterRow(1361, "OnOne", 3), new LoginCharacterRow(1362, "OnTwo", 5), new LoginCharacterRow(2361, "OldDark", 7)])),
              "each character is listed on its own world (the client counts characters per server); a character without one on world 1 of its race");
        check(BitConverter.ToUInt16(account, account.Length - 12) == 1361 && BitConverter.ToUInt16(account, account.Length - 5) == 1361
              && BitConverter.ToUInt16(account, account.Length - 3) == 2361,
              "0x3906 opens the list on the first character's world, with the account's last Light and Dark worlds");
        var picked = (await login.HandleAsync([0x0c, 0x39, 0x52, 0x05], now)).Replies.Single();   // 0x0552 = 1362
        check(picked.SequenceEqual(LoginPackets.SelectResult(1362)) && picks.Server("42") == 1362 && picks.Race("42") == 1,
              "picking a world records the world itself (and its race) for the game port's lobby");
        var account2 = (await new LoginSession(new LoginSessionOptions { Worlds = worlds.Rows(), LastPickedServer = picks.Server },
            (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", Guid.NewGuid(), []))).HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), now)).Replies.Single();
        check(BitConverter.ToUInt16(account2, account2.Length - 12) == 1362, "the next login's list opens on the world picked last");
        var closedLogin = new LoginSession(new LoginSessionOptions { Worlds = renamed.Rows(), Directory = () => renamed.Rows() },
            (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("43", Guid.NewGuid(), [("Old", 9, 2)]) { Servers = [2361] }));
        await closedLogin.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), now);
        var refused = (await closedLogin.HandleAsync([0x0c, 0x39, 0x3a, 0x09], now)).Replies.Single();   // 0x093a = 2362
        check(refused.SequenceEqual(LoginPackets.SelectResult(2362, EResult.LoginServer_SelectServer_RestrictCreation))
              && BitConverter.ToUInt16(refused, 2) == 8968,
              "a world closed to creation refuses an account without a character there (SelectServer_RS LoginServer_SelectServer_RestrictCreation 8968)");

        // ---------- The game port: lobby, creation, appearance per world ----------
        // ArenaMatchingInfo_NT, OFFICIAL bytes (rec 20261106-105448 #610): servers 1316 Light and 2316 Dark, u16.
        var official = new WorldCatalog([new(1316, 1, "L", 0), new(2316, 2, "D", 0)]);
        var store = new FakeStore();
        var shared = new SharedWorld(data);
        GameSession Session(WorldCatalog catalog, int? server) =>
            new(data, new GameSessionOptions { World = shared, Worlds = catalog, WorldServer = _ => server }, new Random(5)) { Store = store };
        var first = Session(official, 1316);
        await first.HandleAsync(GameLogin, now);
        var made = await first.HandleAsync(Create("Pairone", 1, 0), now);
        check(made.Count == 1 && Op(made[0]) == 0x361c && BitConverter.ToUInt16(made[0], 2) == 0 && store.Created[^1].HomeServerId == 1316,
              "a character is created on the picked world (CreatePlayer.HomeServerId, game.characters.home_server_id)");
        await first.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[^1].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await first.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var arena = entry.FirstOrDefault(b => Op(b) == 0x400a);
        check(arena is not null && Convert.ToHexString(arena) == "0A40010102240501" + "0C0902",
              $"ArenaMatchingInfo_NT is the official bytes `0a40 01 01 02 2405 01 0c09 02` (one Light, one Dark server, u16 ids): {(arena is null ? "none" : Convert.ToHexString(arena))}");
        var appear = entry.FirstOrDefault(b => Op(b) == 0x3633);
        var appearName = appear is null ? -1 : appear.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Pairone"));
        var match = appear is null ? -1 : appear.AsSpan().IndexOf(new byte[] { 0x0c, 0x09 }.Concat(new byte[32]).ToArray());
        check(first.ServerId == 1316 && appearName > 0 && BitConverter.ToUInt16(appear!, appearName + 7) == 1316 && match > appearName,
              "MyCharAppear names the character's world as _server_id and the other race's pair world as _match_server_id (OFFICIAL placement: u16 before 32 zero bytes)");

        // The four worlds: the lobby lists the picked world's characters only, and each world has its own lobby slots.
        var four = WorldCatalog.Default();
        var store4 = new FakeStore();
        GameSession Four(int? server) =>
            new(data, new GameSessionOptions { World = shared, Worlds = four, WorldServer = _ => server }, new Random(5)) { Store = store4 };
        var one = Four(1361);
        await one.HandleAsync(GameLogin, now);
        var onOne = await one.HandleAsync(Create("Worldone", 1, 0), now);
        var two = Four(1362);
        await two.HandleAsync(GameLogin, now);
        var lobbyTwo = (await two.HandleAsync([0x25, 0x36], now)).Single();
        var onTwo = await two.HandleAsync(Create("Worldtwo", 1, 0), now);
        var lobbyTwoAfter = (await two.HandleAsync([0x25, 0x36], now)).Single();
        check(BitConverter.ToUInt16(onOne[0], 2) == 0 && lobbyTwo[4] == 0 && BitConverter.ToUInt16(onTwo[0], 2) == 0 && lobbyTwoAfter[4] == 1
              && store4.Created.Select(c => c.HomeServerId).SequenceEqual(new int?[] { 1361, 1362 }),
              "world 2's lobby does not list world 1's character, and its slot 0 is free (lobby slots per world, migration 093)");
        var oldLight = store4.States[0] with { };
        store4.States[0] = oldLight with { HomeServerId = null };
        var back = Four(1361);
        await back.HandleAsync(GameLogin, now);
        check((await back.HandleAsync([0x25, 0x36], now)).Single()[4] == 1, "a character without a stored world is listed on world 1 of its race (the characters of before)");
        var darkCreation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1)?.Race == 2);
        var wrongRace = await two.HandleAsync(Create("Darkonlight", darkCreation, 1), now);
        check(BitConverter.ToUInt16(wrongRace[0], 2) == GamePackets.CreateInvalidServerRace, "a Dark creation on a Light world is CharCreate_InvalidServerRace");
        var dark = Four(2361);
        await dark.HandleAsync(GameLogin, now);
        var darkMade = await dark.HandleAsync(Create("Darkworld", darkCreation, 0), now);
        check(BitConverter.ToUInt16(darkMade[0], 2) == 0 && store4.Created[^1].HomeServerId == 2361,
              "the same account plays Dark on a Dark world (one race per world, as official: the account's Light characters do not block it)");
        var closedStore = new FakeStore();
        var closed = new GameSession(data, new GameSessionOptions { World = shared, Worlds = renamed, WorldServer = _ => 2362 }, new Random(5)) { Store = closedStore };
        await closed.HandleAsync(GameLogin, now);
        var closedMade = await closed.HandleAsync(Create("Toolate", darkCreation, 0), now);
        check(BitConverter.ToUInt16(closedMade[0], 2) == EResult.LoginServer_SelectServer_RestrictCreation && closedStore.Created.Count == 0,
              "no creation on a world closed to new characters");

        // ---------- Map copies per world pair; shared inter-server maps ----------
        check(WorldCatalog.CrossServerMap(20) && WorldCatalog.CrossServerMap(22) && WorldCatalog.CrossServerMap(62) && WorldCatalog.CrossServerMap(600001)
              && !WorldCatalog.CrossServerMap(1000) && !WorldCatalog.CrossServerMap(1010) && !WorldCatalog.CrossServerMap(1110) && !WorldCatalog.CrossServerMap(21),
              "Map.dat WorldServerType: Abyss Reshanta (20, 22, 23), battlefields and inter-server dungeons are shared; the faction world maps are per world");
        check(four.LayerOf(1361, 1010) == 0 && four.LayerOf(1362, 1010) == 1 && four.LayerOf(2362, 1110) == 1 && four.LayerOf(1362, 20) == 0 && four.LayerOf(2362, 22) == 0,
              "layers: world 2 of each race has its own copy of the world maps, every world meets on the Abyss maps");
        var world = new SharedWorld(data);
        var layer0 = world.Map(1010);
        var layer1 = world.Map(1010, 0, 1);
        check(layer0 != layer1 && world.Map(1010, 0, 1) == layer1 && layer1.Layer == 1 && layer1.Instance == 0 && layer0.Layer == 0 && world.Map(1010, 77, 1).Layer == 0,
              "SharedWorld keeps one copy of a shared map per layer (instance 0, so field bosses and staff tools treat it as a world map); private copies ignore layers");
        world.Drop(layer1);
        check(world.Map(1010, 0, 1) == layer1, "a world copy stays when empty (it is not a private copy)");
        check(one.ServerId == 1204 && two.ServerId == 1204, "a session in the lobby keeps the host's server id (1204) until a character is selected");

        // ---------- Migration 093 ----------
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "database", "migrations"))) root = root.Parent;
        var migration = root is null ? "" : File.ReadAllText(Path.Combine(root.FullName, "database", "migrations", "093_character_worlds.sql"));
        check(migration.Contains("ADD COLUMN home_server_id integer") && migration.Contains("THEN 2361 ELSE 1361")
              && migration.Contains("character_lobby_slot_idx ON game.characters(account_id,world_id,home_server_id,lobby_slot)"),
              "migration 093: home_server_id (existing characters on 1361/2361 by race) and lobby slots unique per world");
    }
}
