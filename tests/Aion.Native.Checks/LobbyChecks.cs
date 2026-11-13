using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// The character list (GameSession.Lobby.cs, LobbyZones): official LobbyCharDesc records rebuilt byte for byte (a Lobby_RS list and
/// both CharCreate_RS samples, rec 20261107-010735 / -010322), the location subzone of the official records' spots, and a stored
/// character listed after Lobby_RQ with its legion, location, creation date, combat power and wing.
/// </summary>
static class LobbyChecks
{
    // rec 20261107-010735 #7610 Lobby_RS: one pre-generated Elyos character (Lv 1, 8 worn items, zone 3040, CP 3422).
    const string OfficialLobby = "2636000001283600000c247178544339454f546659442a000000010201000000000000000c00000000000000000000000100000000000000000000000097e18db3a1010000d78488c7113b7a4784c1bb46e00b00008de88bb3a101000000000000000000000824c9f40800010000000000000000ee0000000000030000000000000000000000000ee9d6890c000200000000000000000000000000030000000000000000000000000071b7ed0e00030000000000000000ee000000000003000000000000000000000000003339e80e0004000000000000000000000000000300000000000000000000000000d3bfe90e00050000000000000000ee00000000000300000000000000000000000000113eef0e0006000000000000000000000000000300000000000000000000000000b1c4f00e00070000000000000000ee00000000000300000000000000000000000000514bf20e0008000000000000000000000000000300000000000000000000000000b06302000000280500000000a18567aea10100000000000000000000de0200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000005e0d00000000000000";
    // rec 20261107-010735 #10468 CharCreate_RS: a new Elyos character (no items listed, zone 3040).
    const string OfficialCreatedLight = "1c360000e927000009536f6e67344c6966651a00000001020100000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000e00b00003e048fb3a1010000000000000000000000b1630200000028050000000000000000000000000000000000000000c9020c646464646464146464646494ac0064646464646484709c64649e6464646464646464646400646464646464647064635f64987264854d64646464416464646464643d8eaa73a16428646464646428645c7aaa4d4a646464a264646464646464646464646400977c8864646472c06566020204020204646464797968681a0c0c3f242412121226262664ffffffffffff0000591c1c591c1c6e0000000000005a2a6800000064324c8d6d4700651f0400b40069d84141640064673b050e823c006e8484846ec8006d5c5c5cc8c8c86a0400008c000008c60000006e38c8006e7e7e7e6f6f006e000000c800006a38110d7a00c867170202c80000006e8383836f6f0066260a00646b0000007d646465070c0c6a6434376464646465703b347d8c2626788c65ffffff0000c8ffffff0000c8656565656565726b0101015f200e6464651e6520130d65210000000000000000000000010000000000000000";
    // rec 20261107-010322 #3182 CharCreate_RS: a new Asmodian character (the starter weapon, zone 4011).
    const string OfficialCreatedDark = "1c3600008e2a000009536f6e67344c6966651c00000002020100000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000ab0f0000696789b3a101000000000000000000000108039706000100000000000000000e0000000000030000000000000000000000000017cd0400000009090000000000000000000000000000000000000000c9020c6464646464646464646464986f00646464646464886a90648a9a6464646464646464646400646464646464647064635f64987264854d64646464416464646464643d8eaa73a16428646464646428645c7aaa4d4a646464a264646464646464646464646400977c6464646472c06566020204020204646464797968681a0c0c3f242412121226262664ffffffffffff0000591c1c591c1c6e0000000000005a2a6800000064324c8d6d4700651f0400b40069d84141640064673b050e823c006e8484846ec8006d5c5c5cc8c8c86a0400008c000008c60000006e38c8006e7e7e7e6f6f006e000000c800006a38110d7a00c867170202c80000006e8383836f6f0066260a00646b0000007d646465070c0c6a6434376464646465703b347d8c2626788c65ffffff0000c8ffffff0000c8656565656565726b0101015f200e6464651e6520130d652100000000000000000000030000000000000000";

    static void Same(Action<bool, string> check, string name, byte[] built, string official)
    {
        var ok = built.AsSpan().SequenceEqual(Convert.FromHexString(official));
        check(ok, ok ? name : $"{name}: built {Convert.ToHexString(built)} want {official.ToUpperInvariant()}");
    }

    static Vector3d PositionAt(byte[] body, int offset) =>
        new(BitConverter.ToSingle(body, offset), BitConverter.ToSingle(body, offset + 4), BitConverter.ToSingle(body, offset + 8));

    /// <summary>A LobbyCharDesc as GamePackets.WriteLobbyCharacter writes it (no dye slots, no item counts).</summary>
    public static LobbyCharacter Read(PacketReader r)
    {
        var c = new LobbyCharacter
        {
            Key = r.I32(), Nickname = r.String(), PcId = r.I32(), Race = r.U8(), Gender = r.U8(), Level = r.I32(), ConquerorLevel = r.I32(),
            EquipItemLevel = r.I32(), Experience = r.I64(), CombatLevel = r.I32(), CombatExperience = r.I64(), GuildName = r.String(),
            LogoutTime = r.I64(), Position = r.Vec3(), ZoneId = r.I32(), RegTime = r.I64(), PendingDeleteTime = r.I64(),
        };
        var equips = new List<EquipBroad>();
        for (var count = r.Varint(); count > 0; count--)
        {
            int item = r.I32(); byte enchant = r.U8(), slot = r.U8(); long expire = r.I64(); bool expired = r.Bool(); int skin = r.I32();
            if (r.Varint() != 0) throw new ProtocolException("dye slots are not read");
            var godstones = new List<int>();
            for (var n = r.Varint(); n > 0; n--) godstones.Add(r.I32());
            bool display = r.Bool(), defaultMesh = r.Bool(), over = r.Bool();
            equips.Add(new EquipBroad(item, enchant, slot, expire, expired, skin, godstones, display, defaultMesh, over, (int)r.Varint()));
        }
        c = c with { Equips = equips, DbId = r.I64(), BlockReasonNo = r.I32(), BlockEndTime = r.I64(), WingId = r.I32(), WingSkinId = r.I32(),
            CustomizingData = r.Bytes(), VoiceType = r.U8(), PreGenerated = r.Bool() };
        if (r.Varint() != 0) throw new ProtocolException("item counts are not read");
        return c with { LastNicknameChangeTime = r.I64(), SlotNo = r.U8(), CombatPower = r.I64() };
    }

    public static void Run(Action<bool, string> check)
    {
        // Lobby_RS 0x3626: result u16, one record, extended slots 0. Position at byte 69, the 350 customizing bytes at 400.
        var lobby = Convert.FromHexString(OfficialLobby);
        EquipBroad Worn(int item, byte slot) => new(item, 0, slot, Godstones: [0, 0, 0], FxBit: slot == 1 ? 14 : 0);
        Same(check, "0x3626 Lobby_RS list as official (rec 010735 #7610: zone 3040, combat level 1, block end = now - 24 h, CP 3422, godstones [0,0,0], fx 14 on the weapon only)",
            GamePackets.LobbyCharacters([new LobbyCharacter
            {
                Key = 13864, Nickname = "$qxTC9EOTfYD", PcId = 42, Race = 1, Gender = 2, Level = 1, EquipItemLevel = 12, CombatLevel = 1,
                LogoutTime = 1794013782423, Position = PositionAt(lobby, 69), ZoneId = 3040, RegTime = 1794013653133,
                Equips = [Worn(150260004, 1), Worn(210360041, 2), Worn(250460017, 3), Worn(250100019, 4), Worn(250200019, 5),
                    Worn(250560017, 6), Worn(250660017, 7), Worn(250760017, 8)],
                DbId = 371546969258222512, BlockEndTime = 1793927382433, CustomizingData = lobby[400..750], SlotNo = 0, CombatPower = 3422,
            }]), OfficialLobby);
        // CharCreate_RS 0x361c: result u16 (we sent an i32), logout 0, the race's creation subzone, combat level 1.
        var light = Convert.FromHexString(OfficialCreatedLight);
        Same(check, "0x361c CharCreate_RS as official (rec 010735 #10468, Elyos: result u16, zone 3040, logout 0, no items)",
            GamePackets.CharacterCreated("Song4Life", 2, 26, 1, 10217, 371546969258222513, light[128..457], 0, 1, 1794013856830), OfficialCreatedLight);
        var dark = Convert.FromHexString(OfficialCreatedDark);
        Same(check, "0x361c CharCreate_RS as official (rec 010322 #3182, Asmodian: zone 4011, the starter weapon with fx 0)",
            GamePackets.CharacterCreated("Song4Life", 2, 28, 2, 10894, 651051621132061975, dark[162..491], 0, 3, 1794013489001,
                equips: [new EquipBroad(110560008, 0, 1, Godstones: [0, 0, 0], FxBit: 0)]), OfficialCreatedDark);
        check(LobbyCharacterRoundTrip(lobby), "the checks' LobbyCharDesc reader reads the official list back to the same bytes");

        // The location: the subzone holding the last spot (OBSERVED zones of the official records on these maps).
        var spots = new (int Map, Vector3d At, int Zone, string What)[]
        {
            (1000, new(9924.3, -40024.0, 21037.0), 3011, "Poeta, Migzatu Brigade Base"),
            (1110, new(67550.0, -65928.6, 10601.0), 1067, "Altgard, Guide Rest Stop"),
            (1110, new(68391.2, -67317.1, 10610.0), 1067, "Altgard, Guide Rest Stop (second visit)"),
            (1110, new(-96062.5, -98688.0, 33459.0), 1004, "Altgard, Galorik's Inspection Area"),
            (210016, new(-5997.1, -10837.6, 1209.7), 10070, "Clione Lake Cave"),
        };
        foreach (var (map, at, zone, what) in spots)
            check(LobbyZones.ZoneOf(map, at) == zone, $"lobby location of an official spot: {what} = subzone {zone} (got {LobbyZones.ZoneOf(map, at)})");
        check(LobbyZones.ZoneOf(100001, new(-69897.7, 64059.1, 24032.8)) == 3014 && LobbyZones.ZoneOf(110001, new(-169539.4, -8181.6, 7528.7)) == 4003,
            "an instance layer without areas shows its Map.dat default subzone (CANDIDATE: official 3040 / 4025 are sub-areas we lack)");
        check(LobbyZones.ZoneOf(null, null, 1) == 3000 && LobbyZones.ZoneOf(0, null, 2) == 4000, "no saved map: the race's home map (Poeta 3000 / Ishalgen 4000)");
    }

    static bool LobbyCharacterRoundTrip(byte[] official)
    {
        var r = new PacketReader(official) { Offset = 2 };
        if (r.U16() != 0) return false;
        var count = r.Varint();
        var records = new List<LobbyCharacter>();
        for (var i = 0; i < count; i++) records.Add(Read(r));
        return GamePackets.LobbyCharacters(records, r.U8()).AsSpan().SequenceEqual(official) && r.End;
    }

    /// <summary>A stored character listed after Lobby_RQ: legion, location, creation date, the saved combat power and wing.</summary>
    public static async Task RunSession(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var store = new FakeStore();
        var session = await storedWorldSession("Listed", store);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(now - 86_400_000L * 3);
        store.States[0] = store.States[0] with
        {
            Progress = store.States[0].Progress with { MapId = 1110, Position = StoredCharacters.PositionJson(new Vector3d(67550.0, -65928.6, 10601.0)) },
            Created = created, LastOnline = DateTimeOffset.FromUnixTimeMilliseconds(now - 60_000),
        };
        store.GuildRow = new GuildView(7, "Adventalis", 1, 0, 1, 0, "", "", "", DateTimeOffset.UnixEpoch, []);
        var empty = JsonSerializer.SerializeToElement(new { });
        store.Items.AddRange([new ItemState(Guid.NewGuid(), "b", 110160001, 1, "equipment", 1, 1, empty, empty, 9001),
            new ItemState(Guid.NewGuid(), "b", 110160001, 1, "bag", 0, 1, empty, empty, 9002)]);
        var inWorld = session.InWorld;
        var power = session.CombatPower();
        var first = (await session.HandleAsync([0x25, 0x36], now)).Single();
        var saved = store.Settings.TryGetValue(GameSession.LobbySetting, out var view) ? view : default;
        check(saved.ValueKind == JsonValueKind.Object && saved.GetProperty("cp").GetInt64() == power && saved.GetProperty("wing").GetInt32() == 0,
            $"Lobby_RQ (a forced checkpoint) saves the character list's combat power and shown wing (setting \"lobby\": {(saved.ValueKind == JsonValueKind.Undefined ? "none" : saved.GetRawText())}, in world {inWorld}, errors {string.Join(",", session.PersistenceErrors)})");
        var listed = Read(new PacketReader(first) { Offset = 5 });
        check(listed.CombatPower == saved.GetProperty("cp").GetInt64() && listed.GuildName == "Adventalis" && listed.ZoneId == 1067
              && listed.RegTime == created.ToUnixTimeMilliseconds() && listed.LogoutTime == now - 60_000 && listed.CombatLevel == 1
              && listed.BlockEndTime == now - 86_400_000L,
            "the list shows the legion, the location subzone (Guide Rest Stop 1067), creation date, last login, combat power, combat level 1 and block end now - 24 h");
        check(listed.Equips.Count == 1 && listed.Equips[0].ItemId == 110160001 && listed.Equips.All(e => e.Godstones is [0, 0, 0] && e.FxBit == (e.VisualSlot == 1 ? 14 : 0)),
            "listed items carry godstones [0,0,0] and fx 14 on the weapon only, as official");
        // A wing saved by an earlier session shows with its skin (the official list: wing 40200100 with skin 40203300).
        store.Settings[GameSession.LobbySetting] = JsonSerializer.SerializeToElement(new { cp = 75329, wing = 40200100, wing_skin = 40203300 });
        var second = Read(new PacketReader((await session.HandleAsync([0x25, 0x36], now)).Single()) { Offset = 5 });
        check(second.WingId == 40200100 && second.WingSkinId == 40203300 && second.CombatPower == 75329,
            "the saved wing, wing skin and combat power are listed (the lobby needs no world state)");
    }
}
