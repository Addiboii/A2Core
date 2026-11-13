using Aion.Protocol.Native;

namespace Aion.World;

// Bookmarked content (the 0x93 run): BookmarkedContentList_NT 0x9330 at the world entry, Bookmark_RQ 0x932e -> Bookmark_RS 0x932f.
// OBSERVED (our recordings 2026-10-13 to 11-06, 34 entries): 0x9330 = count varint, then {id i32, type u8, time i64 unix ms} each
// ("309300" with none, 19; one and five entries), always right after 0xe237 and before the ticket list 0x610b.
// OBSERVED in the community fork's recordings (2026-11-07, seven quest bookmarks; none in ours): Bookmark_RS
// 2f93 0000 04 84000000 01 <time i64>: result u16 0, type u8 (EContentType, Quest 4), id i32, bookmarked bool, time.
// CANDIDATE: the request layout (usmap {type, id, bookmarked}; the client direction is ciphered), time 0 on removal, no limit.
// OURS: stored in the character's settings ("bookmarks"), in the order made.
public sealed partial class GameSession
{
    public const ushort BookmarkRq = 0x932e, BookmarkRs = 0x932f, BookmarkListNt = 0x9330;

    public sealed record BookmarkEntry(int Type, int Id, long Time);

    List<BookmarkEntry> bookmarks = [];

    public IReadOnlyList<BookmarkEntry> Bookmarks => bookmarks;

    /// <summary>BookmarkedContentList_NT 0x9330.</summary>
    byte[] BookmarkList()
    {
        var w = PacketWriter.Opcode(BookmarkListNt).Varint((uint)bookmarks.Count);
        foreach (var b in bookmarks) w.I32(b.Id).U8((byte)b.Type).I64(b.Time);
        return w.ToArray();
    }

    /// <summary>Bookmark_RQ {type u8, id i32, bookmarked bool}: adds or removes a bookmark (stored) and answers 0x932f.</summary>
    async Task<List<byte[]>> BookmarkAsync(byte[] plain, long now, CancellationToken ct)
    {
        var type = plain[2];
        var id = BitConverter.ToInt32(plain, 3);
        var on = plain[7] != 0;
        byte[] Reply(int result, long time) => PacketWriter.Opcode(BookmarkRs).U16(result).U8(type).I32(id).Bool(on).I64(time).ToArray();
        if (type == 0 || id <= 0) return [Reply(1, 0)];
        var existing = bookmarks.FindIndex(b => b.Type == type && b.Id == id);
        long time = 0;
        if (on)
        {
            if (existing >= 0) return [Reply(0, bookmarks[existing].Time)];
            time = now;
            bookmarks.Add(new BookmarkEntry(type, id, time));
        }
        else if (existing >= 0) bookmarks.RemoveAt(existing);
        else return [Reply(0, 0)];
        await SaveSettingAsync("bookmarks", bookmarks, ct);
        return [Reply(0, time)];
    }
}
