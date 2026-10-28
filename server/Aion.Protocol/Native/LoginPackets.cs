using System.Text;

namespace Aion.Protocol.Native;

/// <summary>A 0x390b character-list row (login port; a2kit layout, confirmed against the official plaintext).</summary>
public sealed record LoginCharacterRow(int ServerId, string Name, uint Level = 1, uint Experience = 0,
    uint Code = 5, byte Flags = 1, ulong LastPlayedUnixMs = 0);

/// <summary>A 0x3909 world-directory row. Group is the world's race (Common_ServerInfo _server_race: 1 Light, 2 Dark).</summary>
public sealed record WorldRow(int Id, int Group, string Name, byte Status = 0, byte Load = 0);

/// <summary>
/// Server bodies of the login port (3700 locally; 13700 officially). Request/response pairing
/// from the official captures: 0x3905 → 0x3906 account, 0x390a → 0x390b characters, 0x3908 →
/// 0x3909 worlds, 0x3902 → 0x3903 ping, 0x390c → 0x390d selection, 0x390e → 0x390f redirect.
/// Port of tools/login_server.py; parity checked against its output.
/// </summary>
public static class LoginPackets
{
    // Ids 1-5 and 101-105 are cooked placeholders. 1361 and 2361 are the first Europe-band ids
    // without a ServerName.dat row, so the client shows the wire name.
    public static readonly IReadOnlyList<WorldRow> DefaultWorlds =
    [
        new(1361, 1, "PotatoeWorld", 1, 50),
        new(2361, 2, "PotatoeWorld", 1, 50),
    ];

    /// <summary>
    /// 0x3906 account body; account ID decimal, GUID in canonical 36-character text form. After the last server, one
    /// packed boolean byte: bit 0 _tutorial_completed_light, bit 1 _tutorial_completed_dark (usmap Login_RS order;
    /// booleans share a byte as in our writer: a second byte here was read as the pre-generated nickname and the login
    /// failed, live 2026-10-28). The official sample had only bit 0, and with it picking the Dark world sent the client
    /// back to the login port (MoveToLoginServer 0x3617) for the race's prologue, which this server does not run.
    /// </summary>
    /// The first server is the last-accessed one, which the client's server list opens on; the last two are the last Light
    /// and Dark servers (usmap order, CANDIDATE: the official sample's Dark one, 03 09, is 2307, an official Dark server).
    public static byte[] Account(string accountId, Guid guid, int serverId, int? lightServerId = null, int darkServerId = 0x0903)
    {
        if (accountId.Length is < 1 or > 16 || !accountId.All(char.IsAsciiDigit)) throw new ArgumentException("Account ID must be a short decimal string");
        if (new[] { serverId, lightServerId ?? 0, darkServerId }.Any(id => id is < 0 or > 0xFFFF)) throw new ArgumentOutOfRangeException(nameof(serverId));
        var first = guid.ToString("D");
        var combined = accountId + ":" + first;
        return PacketWriter.Opcode(0x3906).U16(0).U8(0)
            .U8(first.Length).Raw(Encoding.ASCII.GetBytes(first))
            .U8(1).U16(0)
            .U8(combined.Length).Raw(Encoding.ASCII.GetBytes(combined))
            .U16(serverId).U8(0b11).U32(0).U16(lightServerId ?? serverId).U16(darkServerId).U8(3).ToArray();
    }

    /// <summary>
    /// 0x390b character list: u16 server | name | u32 level | u32 exp | u32 0 | u32 code | u8 flags |
    /// u64 last played ms | 8 zero bytes, with a group-mask byte after every fourth row's start.
    /// '$'-prefixed names are the official placeholder (hidden in the lobby UI).
    /// </summary>
    public static byte[] Characters(IReadOnlyList<LoginCharacterRow> rows)
    {
        if (rows.Count > 255) throw new ArgumentException("Character list must hold 0..255 rows");
        var w = PacketWriter.Opcode(0x390b).U16(0).U8(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var name = Encoding.UTF8.GetBytes(row.Name);
            if (row.ServerId is < 0 or > 0xFFFF || name.Length is < 1 or > 255) throw new ArgumentException($"Character {index} outside bounds");
            w.U16(row.ServerId).U8(name.Length).Raw(name).U32(row.Level).U32(row.Experience).U32(0)
                .U32(row.Code).U8(row.Flags).U64(row.LastPlayedUnixMs).Zeros(8);
            if (index % 4 == 0) w.U8(0);  // group mask, all-zero for our rows
        }
        return w.U8(1).U8(1).ToArray();
    }

    /// <summary>One '$'-named generic intro character (12 bytes, as in the 0x3615 sample that loaded with no create step).</summary>
    public static LoginCharacterRow PreGeneratedIntro(int serverId, long nowMs)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var name = "$" + string.Concat(Enumerable.Range(0, 11).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
        return new LoginCharacterRow(serverId, name, 1, 0, 42, 2, (ulong)nowMs);
    }

    /// <summary>Group mask for up to four rows from <paramref name="index"/>: two bits per row (0x01, 0x05, ... 0x55).</summary>
    public static byte OnlineGroupMask(int rowCount, int index)
    {
        if (index % 4 != 0 || rowCount - index < 1) throw new ArgumentException("Group mask is only carried on every fourth row");
        var mask = 0;
        for (var slot = 0; slot < Math.Min(4, rowCount - index); slot++) mask |= 1 << (2 * slot);
        return (byte)mask;
    }

    public static byte[] WorldDirectory(IReadOnlyList<WorldRow> worlds)
    {
        if (worlds.Count is < 1 or > 255) throw new ArgumentException("World directory must hold 1..255 rows");
        var w = PacketWriter.Opcode(0x3909).U16(0).U8(worlds.Count);
        for (var index = 0; index < worlds.Count; index++)
        {
            var world = worlds[index];
            var name = Encoding.UTF8.GetBytes(world.Name);
            if (world.Id is < 0 or > 0xFFFF || world.Group is not (1 or 2) || name.Length is < 1 or > 255)
                throw new ArgumentException($"World row {index} outside observed bounds");
            w.U16(world.Id).U8(world.Group).U16(world.Id).U16(0).U8(name.Length).Raw(name);
            if (index % 4 == 0) w.U8(OnlineGroupMask(worlds.Count, index));
            w.U8(world.Status).U8(world.Load).U8(0x50).U8(1).U8(0);
        }
        return w.U8(1).U8(1).ToArray();
    }

    public static byte[] SelectResult(int worldId) => PacketWriter.Opcode(0x390d).U16(0).U8(0).U16(worldId).ToArray();

    /// <summary>0x390f handoff: the game server the client connects to next.</summary>
    public static byte[] Redirect(int worldId, string address, int port)
    {
        var encoded = Encoding.ASCII.GetBytes(address);
        if (encoded.Length is < 7 or > 15) throw new ArgumentException("Destination address length outside observed bounds");
        if (port is < 1 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(port));
        return PacketWriter.Opcode(0x390f).U16(0).U16(worldId).Bytes(encoded).U16(port).ToArray();
    }

    /// <summary>0x3903 lobby ping: echo the client counter plus the server clock.</summary>
    public static byte[] Ping(ulong clientCounter, long nowMs) => PacketWriter.Opcode(0x3903).U16(0).U64(clientCounter).I64(nowMs).ToArray();
}

/// <summary>
/// Credentials from the client's NPLogin requests: 0x3905 (six u8-length strings; the authn
/// token is the second) and 0x3613 (four strings + nine zero bytes: "account:grant", "",
/// authn token, ""). Values are sensitive; <see cref="ToString"/> redacts them.
/// </summary>
public sealed record ClientIdentity(string AuthnToken, string AccountSession = "")
{
    public override string ToString() => "ClientIdentity [credentials redacted]";

    public static ClientIdentity Parse(ReadOnlySpan<byte> body, bool game)
    {
        var (opcode, count) = game ? (0x3613, 4) : (0x3905, 6);
        if (body.Length < 2 || (body[0] | body[1] << 8) != opcode) throw new ProtocolException("Unexpected identity opcode");
        var strict = new UTF8Encoding(false, true);
        var offset = 2;
        var strings = new string[count];
        for (var n = 0; n < count; n++)
        {
            if (offset >= body.Length) throw new ProtocolException("Truncated identity");
            int size = body[offset++];
            if (size > 127 || offset + size > body.Length) throw new ProtocolException("Unsupported identity string length");
            strings[n] = strict.GetString(body.Slice(offset, size));
            offset += size;
        }
        if (!game)
        {
            if (offset != body.Length || strings[0] != "" || strings[3] != "" || strings[4] != "") throw new ProtocolException("Unsupported login identity shape");
            return new ClientIdentity(strings[1]);
        }
        if (body.Length - offset != 9 || body[offset..].ContainsAnyExcept((byte)0) || strings[1] != "" || strings[3] != "")
            throw new ProtocolException("Unsupported game identity shape");
        var parts = strings[0].Split(':');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 16 || !parts[0].All(char.IsAsciiDigit)) throw new ProtocolException("Invalid account session");
        if (!Guid.TryParseExact(parts[1], "D", out var grant) || grant.ToString("D") != parts[1].ToLowerInvariant()) throw new ProtocolException("Invalid account grant");
        return new ClientIdentity(strings[2], strings[0]);
    }

    public (long WireAccountId, string Grant) Session()
    {
        var parts = AccountSession.Split(':');
        return (long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]);
    }
}
