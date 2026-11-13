using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Mail (the client's Post system; migration 030 game.character_posts). The 0xe2 run (catalogue index + 0xdd07, anchored
// by the OBSERVED LoadPost 0xe214/0xe215): RecvPost_NT 0xe216, ReadPost 0xe217/0xe218, DeletePost 0xe219/0xe21a,
// TakePost 0xe21b/0xe21c. Common_Post = {EPostType u8, EPostSaveTab u8, Post.dat id i32, dbid i64, msg params string,
// read, taken, sent i64, expire i64, attachments [{item i32, count i64, expire i64}], reason u16} (usmap order; OBSERVED
// with one letter 2026-11-06 084658 i7000: the reason is two bytes and the parameters are JSON
// {"_params":[{"_index":0,"_value":"10045"}]}). Staff mail uses Post.dat 106 (title "{0}", body "{1}", sender the client's
// own name string); its parameters are stored joined by '|' (OURS) and sent as that JSON.
// feature/systems-2: Post.dat is system mail only (no player-to-player send request exists in the client's protocol). A load
// shows at most GlobalSetting post_load_count (100) posts, newest first, and none past its expiry; refusals use the client's
// EResult Post_NotFound (8978) instead of 1.
public sealed partial class GameSession
{
    public const ushort PostLoadRq = 0xe214, PostLoadRs = 0xe215, PostRecvNt = 0xe216, PostReadRq = 0xe217, PostReadRs = 0xe218,
        PostDeleteRq = 0xe219, PostDeleteRs = 0xe21a, PostTakeRq = 0xe21b, PostTakeRs = 0xe21c;
    public const int StaffPostDataId = 106, PostTabSystem = 1, PostTabReward = 2, PostTypeGame = 1;
    const byte IncreasePostTake = 21;   // EIncreaseReason kPostAttachment (read from the usmap of client 2.0.6; no recording)

    /// <summary>A storage-backed value; a database error yields the fallback (as Stored sends nothing).</summary>
    async Task<T> StoredValue<T>(Func<Task<T>> action, T fallback)
    {
        try { return await action(); }
        catch (Exception error) when (error is ArgumentException or ProtocolException or InvalidOperationException or GameplayException
                                          or System.Text.Json.JsonException or KeyNotFoundException or System.Data.Common.DbException)
        {
            PersistenceErrors.Add(error.GetType().Name);
            return fallback;
        }
    }

    static PacketWriter WritePost(PacketWriter w, PostRecord post)
    {
        w.U8((byte)post.Type).U8((byte)post.Tab).I32(post.DataId).I64(post.Id).String(PostParams(post.Params)).Bool(post.Read).Bool(post.Taken)
            .I64(post.Sent.ToUnixTimeMilliseconds()).I64(post.Expire.ToUnixTimeMilliseconds()).Varint((uint)post.Attachments.Count);
        foreach (var (item, count) in post.Attachments) w.I32(item).I64(count).I64(0);
        return w.U16(post.Reason);
    }

    /// <summary>The stored '|'-joined parameters as the client's JSON {"_params":[{"_index":i,"_value":"..."}]} (OBSERVED form).</summary>
    internal static string PostParams(string stored)
    {
        if (stored.StartsWith('{')) return stored;
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteStartArray("_params");
            var values = stored.Length == 0 ? [] : stored.Split('|');
            for (var i = 0; i < values.Length; i++)
            {
                json.WriteStartObject();
                json.WriteNumber("_index", i);
                json.WriteString("_value", values[i]);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>0xe214 LoadPost_RQ {tab u8, unread filter, last account dbid, last game dbid}: the tab's mail (both tabs for tab 0).</summary>
    async Task<List<byte[]>> LoadPostsAsync(byte[] plain, CancellationToken ct)
    {
        var tab = plain[2];
        var unreadOnly = plain.Length > 3 && plain[3] != 0;
        var posts = Store is { } store && CharacterId is { } character ? await StoredValue(() => store.PostsAsync(character, ct), (IReadOnlyList<PostRecord>)[]) : [];
        var now = DateTimeOffset.FromUnixTimeMilliseconds(lastNow == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastNow);
        var live = posts.Where(p => (tab == 0 || p.Tab == tab) && (p.Expire.ToUnixTimeMilliseconds() <= 0 || p.Expire > now)).ToList();
        var limit = (int)Math.Max(1, Systems2Tables.Embedded.Global("post_load_count"));
        var shown = live.Where(p => !unreadOnly || !p.Read).OrderByDescending(p => p.Sent).Take(limit).ToList();
        var w = PacketWriter.Opcode(PostLoadRs).U16(0).U8(tab).I32(live.Count).I32(live.Count(p => !p.Read)).Varint((uint)shown.Count);
        foreach (var post in shown) WritePost(w, post);
        return [w.ToArray()];
    }

    static List<(byte Type, long Id)> PostPairs(byte[] plain)
    {
        var pairs = new List<(byte, long)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 64; i++) pairs.Add((reader.U8(), (long)reader.U64()));
        }
        catch (ProtocolException) { }
        return pairs;
    }

    static byte[] PostPairsReply(ushort opcode, int result, IReadOnlyList<(byte Type, long Id)> pairs)
    {
        var w = PacketWriter.Opcode(opcode).U16(result).Varint((uint)pairs.Count);
        foreach (var (type, id) in pairs) w.U8(type).I64(id);
        return w.ToArray();
    }

    /// <summary>0xe217 ReadPost_RQ / 0xe219 DeletePost_RQ {[{type u8, dbid i64}]}: marked read, or deleted (attachments must be taken first).</summary>
    async Task<List<byte[]>> MarkPostsAsync(byte[] plain, bool delete, CancellationToken ct)
    {
        var pairs = PostPairs(plain);
        var opcode = delete ? PostDeleteRs : PostReadRs;
        if (pairs.Count == 0 || Store is not { } store || CharacterId is not { } character) return [PostPairsReply(opcode, EResult.Post_NotFound, pairs)];
        var ok = await Stored(async () => await store.MarkPostsAsync(character, pairs.Select(p => p.Id).ToList(), delete, ct) ? [[]] : []);
        return [PostPairsReply(opcode, ok.Count == 0 ? EResult.Post_NotFound : 0, pairs)];
    }

    /// <summary>0xe21b TakePost_RQ {type u8, dbid i64}: the attachments into the bag, once.</summary>
    async Task<List<byte[]>> TakePostAsync(byte[] plain, CancellationToken ct)
    {
        var type = plain[2];
        var id = BitConverter.ToInt64(plain, 3);
        byte[] Reply(int result) => PacketWriter.Opcode(PostTakeRs).U16(result).U8(type).I64(id).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(EResult.Post_NotFound)];
        // The attachments need bag room (GameSession.BagRoom.cs); the mail keeps them otherwise.
        var posts = await StoredValue(() => store.PostsAsync(character, ct), (IReadOnlyList<PostRecord>)[]);
        if (posts.FirstOrDefault(p => p.Id == id) is { Taken: false } post && !BagRoomFor(post.Attachments))
            return [Reply(EResult.Item_Common_Fail_NotEnoughSlotCountContainer)];
        var taken = await Stored(async () => await store.TakePostAsync(character, id, ct) ? [[]] : []);
        if (taken.Count == 0) return [Reply(EResult.Post_NotFound)];
        return [Reply(0), .. await InventoryChangesAsync(ct, IncreasePostTake, 0)];
    }

    /// <summary>
    /// Staff mail (builder Mail): to a character by name, online or not, with a title, a body and attachments; an online
    /// recipient gets RecvPost_NT at once. Returns false when the name is unknown.
    /// </summary>
    public async Task<bool> SendMailAsync(string name, string title, string body, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        if (Store is not { } store) return false;
        var online = World?.FindOnline(name);
        var target = online?.CharacterId ?? await StoredValue(() => store.CharacterByNameAsync(name, ct), null);
        if (target is not { } to) return false;
        var tab = items.Count > 0 ? PostTabReward : PostTabSystem;
        var post = await StoredValue(() => store.SendPostAsync(to, StaffPostDataId, $"{title}|{body}", items, tab, ct), null);
        if (post is null) return false;
        online?.Send(WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray());
        return true;
    }
}
