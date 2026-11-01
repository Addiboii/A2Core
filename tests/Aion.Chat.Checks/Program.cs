using System.Text;
using System.Text.Json.Nodes;
using Aion.Application.Authentication;
using Aion.Application.Chat;
using Aion.Core.Accounts;
using Aion.Protocol.Lime;

var clock = new ManualClock();
var relay = new ChatRelay(clock);
var alice = new ChatIdentity("1099511629056", "1204", "en_US");
var bob = new ChatIdentity("1099511629057", "1204", "en_US");
var common = new ChatRoomKey("REGION", "1204", "RG");
var shout = new ChatRoomKey("REGION", "1204", "SH");
var whisperRoom = new ChatRoomKey("", "", "");  // sendWhisper carries no room
var aliceToken = relay.Login(alice);
var bobToken = relay.Login(bob);
var checks = 0;

Check(aliceToken.StartsWith("mtalk-") && aliceToken != bobToken, "distinct session tokens");
Check(relay.Find(aliceToken) == alice && relay.Find("mtalk-unknown") is null, "token lookup");
relay.Join(aliceToken, common);
relay.Join(bobToken, common);
relay.Join(aliceToken, shout);
var (guid, deliveries) = relay.Send(aliceToken, common, Message("test 123"));
Check(guid.Length == 32 && deliveries.Count == 2, "room message reaches both members");
Check(deliveries.All(d => d.Notification.Type == "PUBLISH" && d.Notification.SubType == "NORMAL"), "PUBLISH/NORMAL defaults");
Check(deliveries[0].Notification.GameUserId == alice.CharacterId, "sender is the verified identity");
Check(relay.Send(aliceToken, shout, Message("hi")).Deliveries.Single().SessionToken == aliceToken, "shout only to its members");
var announced = relay.Announce("The server restarts in 1 minute.");
Check(announced.Count == 2 && announced.All(d => d.Notification.UserName == "Server" && d.Notification.Content == "The server restarts in 1 minute."),
      "an announcement reaches every session that joined a room, from Server");
var whisper = relay.Whisper(aliceToken, common, Message("psst") with { ReceiverCharacterId = bob.CharacterId });
Check(whisper.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { aliceToken, bobToken }.Order()), "whisper to receiver and sender");
Rejected(() => relay.Send("mtalk-unknown", common, Message("x")), "unauthenticated send");
Rejected(() => relay.Send(bobToken, common, Message("")), "empty message");
Rejected(() => relay.Send(bobToken, common, Message(new string('a', 101))), "over 100 characters");
Rejected(() => relay.Send(bobToken, common, Message("bad\u0007bell")), "control character");
for (var i = 0; i < 5; i++) relay.Send(bobToken, common, Message("burst " + i));
Rejected(() => relay.Send(bobToken, common, Message("sixth")), "rate limit five per five seconds");
clock.Advance(TimeSpan.FromSeconds(6));
Check(relay.Send(bobToken, common, Message("later")).Deliveries.Count == 2, "rate limit window expires");
clock.Advance(TimeSpan.FromSeconds(6));
var (_, answer) = await relay.CommandAsync(aliceToken, common, Message("/gm help"), (sender, _) => Task.FromResult("Available: " + sender.CharacterId));
Check(answer.Single().SessionToken == aliceToken && answer[0].Notification.Content == "Available: " + alice.CharacterId
    && answer[0].Notification.UserName == "GM", "command answers only the sender");
await RejectedAsync(() => relay.CommandAsync(aliceToken, common, Message(""), (_, _) => Task.FromResult("x")), "commands are validated like messages");
relay.Leave(bobToken, common);
Rejected(() => relay.Send(bobToken, common, Message("x")), "send after leave is rejected");
Check(relay.Send(aliceToken, common, Message("after leave")).Deliveries.Count == 1, "leave stops delivery");

var notification = deliveries[0].Notification;
var envelope = JsonNode.Parse(LimeWire.NotificationEnvelope(notification))!;
Check((string?)envelope["type"] == "NOTI" && (string?)envelope["method"] == "GAME", "NOTI envelope with method GAME");
Check((string?)envelope["jsonData"]?["content"] == "test 123", "envelope carries the notification");
Check((string?)envelope["jsonData"]?["gameRoomKeyInfo"]?["roomKey"] == "RG", "room key serialized camelCase");
var login = LimeWire.LoginReply(aliceToken, alice, "ws://127.0.0.1:8091/stomp");
Check((int?)login["error"] == 0 && (string?)login["subscriptionInfo"]?["subscribeUrl"] == "ws://127.0.0.1:8091/stomp", "login reply");
var request = LimeWire.ReadLogin(JsonNode.Parse("{\n\t\"authnToken\": \"t\",\n\t\"characterId\": \"1\",\n\t\"serverKey\": \"1204\"\n}"));
Check(request.CharacterId == "1" && request.ServerKey == "1204", "reads the client's tab-indented JSON");

var frame = StompFrame.Create("SEND", new Dictionary<string, string> { ["destination"] = "/x" }, "body");
var buffer = new StringBuilder("\n" + Encoding.UTF8.GetString(frame.Encode()) + Encoding.UTF8.GetString(frame.Encode()) + "\nCONN");
var frames = StompFrame.ReadAll(buffer);
Check(frames.Count == 2 && frames.All(f => f.Command == "SEND" && f.Header("destination") == "/x" && f.Body == "body"), "STOMP round trip with heart-beats");
Check(buffer.ToString() == "\nCONN", "partial frame stays buffered");
Check(await new DevelopmentChatLoginVerifier().VerifyAsync(request, default) is { CharacterId: "1" }, "development verifier");
Check(await new RejectingChatLoginVerifier().VerifyAsync(request, default) is null, "default verifier rejects");

// ---------- Development identities: rooms, whispers by name, room rules, sanitizing ----------
clock.Advance(TimeSpan.FromSeconds(6));
relay.Join(bobToken, common);
var hello = relay.Send(bobToken, common, Message("hello") with { UserName = "Bobby" });
Check(hello.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { aliceToken, bobToken }.Order())
    && hello.Deliveries[0].Notification.UserName == "Bobby", "two identities in the same room receive each other's message");
var byName = relay.Whisper(aliceToken, whisperRoom, Message("psst") with { TargetCharacterName = "[GM] bobby" });
Check(byName.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { aliceToken, bobToken }.Order())
    && byName.Deliveries[0].Notification.ReceiverUserName == "Bobby", "development whisper by displayed name");
Rejected(() => relay.Whisper(aliceToken, whisperRoom, Message("psst") with { ReceiverUserName = "Nobody" }), "development whisper to an offline name");
Rejected(() => relay.Whisper(aliceToken, whisperRoom, Message("psst")), "whisper without a target");
Rejected(() => relay.Join(aliceToken, new ChatRoomKey("GUILD", "1204", "GU1")), "guild rooms need membership");
Rejected(() => relay.Join(aliceToken, new ChatRoomKey("REGION", "1361", "RG")), "rooms of another server key");
Rejected(() => relay.Join(aliceToken, new ChatRoomKey("WORLD", "1204", "9999")), "world room is the server key");
var worldRoom = new ChatRoomKey("WORLD", "1204", "1204");
Rejected(() => relay.Send(aliceToken, worldRoom, Message("x")), "send needs room membership");
relay.Join(aliceToken, worldRoom);
Check(relay.Send(aliceToken, worldRoom, Message("world")).Deliveries.Count == 1, "world room joinable");
var forged = relay.Send(aliceToken, common, Message("x") with
{
    Type = "BAN", SubType = "<b>", Optional = "{\"DBId\":\"1\",\"ForceAlert\":true,\"Race\":\"Light\"}",
}).Deliveries[0].Notification;
var forgedOptional = JsonNode.Parse(forged.Optional)!;
Check(forged.Type == "PUBLISH" && forged.SubType == "NORMAL" && (string?)forgedOptional["DBId"] == alice.CharacterId
    && (bool?)forgedOptional["ForceAlert"] == false && (string?)forgedOptional["Race"] == "Light", "client type, subtype and optional are sanitized");
Check(ChatRelay.Optional("not json", alice) == "{}" && ChatRelay.Optional(new string('[', 3000), alice) == "{}", "bad optional replaced");

// ---------- Account identities: game ticket + character in the world ----------
var accountA = Guid.NewGuid();
var accountB = Guid.NewGuid();
var accountC = Guid.NewGuid();
var elyra = new ChatCharacter(Guid.NewGuid(), "Elyra", "[GM] Elyra");
var bram = new ChatCharacter(Guid.NewGuid(), "Bram", "Bram");
var world = new FakeDirectory();
world.Enter(accountA, elyra, "11");
world.Enter(accountB, bram, "12");
world.Enter(accountC, new ChatCharacter(Guid.NewGuid(), "AltOne", "AltOne"), "21");
world.Enter(accountC, new ChatCharacter(Guid.NewGuid(), "AltTwo", "AltTwo"), "22");
var ticketA = new string('a', 64);
var ticketB = new string('b', 64);
var ticketC = new string('c', 64);
var ticketIdle = new string('d', 64);
var handoff = new FakeHandoff(new() { [ticketA] = accountA, [ticketB] = accountB, [ticketC] = accountC, [ticketIdle] = Guid.NewGuid() });
var verifier = new AccountChatLoginVerifier(handoff, world, "1204", 1204, TimeSpan.Zero);
var elyraId = await verifier.VerifyAsync(Login(ticketA, "11"), default);
var bramId = await verifier.VerifyAsync(Login(ticketB, "11"), default);  // claims Elyra's character ID
Check(elyraId is { Authenticated: true, Character.Name: "Elyra" } && elyraId.AccountId == accountA, "account login binds to the in-world character");
Check(bramId is { Character.Name: "Bram" } && bramId.AccountId == accountB, "a claimed character ID cannot take another account's character");
Check(await verifier.VerifyAsync(Login("local-dev-0123", "11"), default) is null, "development token rejected in account mode");
Check(await verifier.VerifyAsync(Login(new string('e', 64), "11"), default) is null, "unknown ticket rejected");
Check(await verifier.VerifyAsync(Login(ticketIdle, "11"), default) is null, "account without a character in the world rejected");
Check(await verifier.VerifyAsync(Login(ticketA, "11", "1361"), default) is null, "other server key rejected");
Check(await verifier.VerifyAsync(Login(ticketA, "1; DROP"), default) is null, "malformed character ID rejected");
Check((await verifier.VerifyAsync(Login(ticketC, "22"), default))?.Character?.Name == "AltTwo"
    && await verifier.VerifyAsync(Login(ticketC, "99"), default) is null, "several characters in the world: the wire ID picks one");
Check(handoff.Worlds.Count > 0 && handoff.Worlds.All(w => w == 1204), "tickets are checked for this world");

var accounts = new ChatRelay(clock, world);
var elyraToken = accounts.Login(elyraId!);
var bramToken = accounts.Login(bramId!);
accounts.Join(elyraToken, common);
accounts.Join(bramToken, common);
var said = accounts.Send(bramToken, common, Message("hi all") with { UserName = "[GM] Elyra" });
Check(said.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { elyraToken, bramToken }.Order()),
    "account identities in the same room receive each other's message");
Check(said.Deliveries[0].Notification.UserName == "Bram", "impersonation rejected: the sender name comes from the world");
Check(accounts.Send(elyraToken, common, Message("hello")).Deliveries[0].Notification.UserName == "[GM] Elyra", "staff tag shown from the world");
var toGm = accounts.Whisper(bramToken, whisperRoom, Message("help please") with { TargetCharacterName = "[GM] Elyra", UserName = "Elyra" });
Check(toGm.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { elyraToken, bramToken }.Order())
    && toGm.Deliveries[0].Notification is { ReceiverUserName: "Elyra", UserName: "Bram" }, "whisper by displayed name reaches the stored name");
Check(accounts.Whisper(elyraToken, whisperRoom, Message("ok") with { ReceiverUserName = "bram" }).Deliveries.Count == 2, "whisper by receiver user name, any case");
Rejected(() => accounts.Whisper(bramToken, whisperRoom, Message("x") with { TargetCharacterName = "Nobody" }), "whisper to an offline character");
Rejected(() => accounts.Whisper(bramToken, whisperRoom, Message("x") with { ReceiverCharacterId = "11" }), "account whispers are not routed by client-chosen IDs");
var (_, gmAnswer) = await accounts.CommandAsync(bramToken, common, Message("//help") with { UserName = "[GM] Elyra" },
    (_, name) => Task.FromResult("as " + name));
Check(gmAnswer.Single().Notification.Content == "as Bram", "commands run as the verified character, not the claimed name");
// Legion chat: a GUILD room needs a legion; a message in it reaches the sender's legion members only (the room key is not relied on).
clock.Advance(TimeSpan.FromSeconds(6));
var legionRoom = new ChatRoomKey("GUILD", "1204", "GU7");
Rejected(() => accounts.Join(elyraToken, legionRoom), "a character without a legion cannot join a legion room");
world.Enter(accountA, elyra with { GuildId = 7 }, "11");
world.Enter(accountB, bram with { GuildId = 7 }, "12");
var altOne = (await world.InWorldAsync(accountC, default)).First(p => p.Character.Name == "AltOne").Character;
world.Enter(accountC, altOne with { GuildId = 8 }, "21");
var altToken = accounts.Login((await verifier.VerifyAsync(Login(ticketC, "21"), default))!);
accounts.Join(elyraToken, legionRoom);
accounts.Join(bramToken, legionRoom);
accounts.Join(altToken, legionRoom);
var legionSaid = accounts.Send(elyraToken, legionRoom, Message("legion hello"));
Check(legionSaid.Deliveries.Select(d => d.SessionToken).Order().SequenceEqual(new[] { elyraToken, bramToken }.Order()),
    "a legion message reaches the legion's members in the room, not another legion's");
world.Enter(accountB, bram, "12");
Rejected(() => accounts.Send(bramToken, legionRoom, Message("still here?")), "a character that left its legion cannot speak in the legion room");
world.Enter(accountB, bram with { GuildId = 7 }, "12");
// Moderation: a muted account is refused (room and whisper), commands still run; delivered messages are logged.
var moderation = new FakeModeration();
accounts.Moderation = moderation;
accounts.Send(elyraToken, common, Message("logged line"));
Check(moderation.Log.Any(l => l.Sender == "Elyra" && l.Content == "logged line" && l.Channel == "REGION/RG" && l.AccountId == accountA), "room messages go to the chat log");
accounts.Whisper(elyraToken, whisperRoom, Message("secret") with { ReceiverUserName = "Bram" });
Check(moderation.Log.Any(l => l is { Channel: "WHISPER", Receiver: "Bram", Content: "secret" }), "whispers go to the chat log with the receiver");
moderation.Muted.Add(accountB);
clock.Advance(TimeSpan.FromSeconds(6));
Rejected(() => accounts.Send(bramToken, common, Message("let me talk")), "a muted account cannot chat in rooms");
Rejected(() => accounts.Whisper(bramToken, whisperRoom, Message("x") with { TargetCharacterName = "Elyra" }), "a muted account cannot whisper");
Check((await accounts.CommandAsync(bramToken, common, Message("//report spam"), (_, _) => Task.FromResult("sent"))).Deliveries.Single().Notification.Content == "sent",
    "a muted account can still use commands (//report)");
Check(!moderation.Log.Any(l => l.Content == "let me talk"), "refused messages are not logged");
accounts.Moderation = null;
world.Leave(elyra);
Rejected(() => accounts.Whisper(bramToken, whisperRoom, Message("x") with { TargetCharacterName = "Elyra" }), "whisper to a character that left the world");
Rejected(() => accounts.Send(elyraToken, common, Message("x")), "a character out of the world cannot chat");
Check(await verifier.VerifyAsync(Login(ticketA, "11"), default) is null, "no chat login after leaving the world");
// Character switch: the client keeps its old chat token; the session follows the account's new character.
var zakl = new ChatCharacter(Guid.NewGuid(), "Zakl", "[GM] Zakl");
world.Enter(accountA, zakl, "13");
clock.Advance(TimeSpan.FromSeconds(6));
Check(accounts.Send(elyraToken, common, Message("after switch")).Deliveries[0].Notification.UserName == "[GM] Zakl",
    "after a character switch the old chat session speaks as the new character");
Check((await accounts.CommandAsync(elyraToken, common, Message("//admin"), (id, name) => Task.FromResult(id.Character!.Name + "/" + name)))
    .Deliveries.Single().Notification.Content == "Zakl/Zakl", "commands after a switch run as the new character");
world.Leave(zakl);
var readWhisper = LimeWire.ReadWhisper(JsonNode.Parse(
    """{"gameUserKey":{"characterId":"12"},"gameMessageInfo":{"content":"x"},"validationInfo":{"targetCharacterName":"[GM] Elyra"}}"""));
Check(readWhisper is { TargetCharacterName: "[GM] Elyra", ReceiverCharacterId: "12", Content: "x" }, "reads sendWhisper");

Console.WriteLine($"{checks} chat checks passed.");

ChatMessage Message(string content) => new("Player1", "", "", content, "{}");

ChatLoginRequest Login(string ticket, string characterId, string serverKey = "1204") =>
    new(ticket, "AION2", "device", "GAME", characterId, serverKey, "en_US");

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Check failed: " + name);
    checks++;
}

async Task RejectedAsync(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (ChatRejectedException)
    {
        checks++;
        return;
    }
    throw new Exception("Expected rejection: " + name);
}

void Rejected(Action action, string name)
{
    try
    {
        action();
    }
    catch (ChatRejectedException)
    {
        checks++;
        return;
    }
    throw new Exception("Expected rejection: " + name);
}

sealed class ManualClock : TimeProvider
{
    DateTimeOffset now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

sealed class FakeDirectory : IChatDirectory
{
    readonly Dictionary<Guid, (Guid Account, ChatCharacter Character, string WireId)> present = [];
    public void Enter(Guid account, ChatCharacter character, string wireId) => present[character.Id] = (account, character, wireId);
    public void Leave(ChatCharacter character) => present.Remove(character.Id);
    public Task<IReadOnlyList<(ChatCharacter Character, string WireId)>> InWorldAsync(Guid account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(ChatCharacter Character, string WireId)>>(
            present.Values.Where(p => p.Account == account).Select(p => (p.Character, p.WireId)).ToList());
    public ChatCharacter? Current(Guid character, Guid account) =>
        present.TryGetValue(character, out var p) && p.Account == account ? p.Character : null;
    public ChatCharacter? CurrentOfAccount(Guid account) =>
        present.Values.Where(p => p.Account == account).ToList() is [var only] ? only.Character : null;
}

sealed class FakeHandoff(Dictionary<string, Guid> tickets) : IClientHandoff
{
    public List<int> Worlds { get; } = [];
    public Task<ClientLoginGrant?> BeginLoginAsync(string ticket, int worldId, CancellationToken ct)
    {
        Worlds.Add(worldId);
        return Task.FromResult(tickets.TryGetValue(ticket, out var account) ? new ClientLoginGrant(account, 1, Guid.NewGuid().ToString(), worldId) : null);
    }
    public Task<Guid?> ResolveTicketAsync(string ticket, int worldId, CancellationToken ct)
    {
        Worlds.Add(worldId);
        return Task.FromResult(tickets.TryGetValue(ticket, out var account) ? account : (Guid?)null);
    }
    public Task<ClientLaunch?> AuthorizeLaunchAsync(string sessionToken, int worldId, CancellationToken ct) => throw new NotSupportedException();
    public Task<GameTicket?> RedeemLaunchAsync(string authorization, CancellationToken ct) => throw new NotSupportedException();
    public Task<Guid?> ResolveLaunchAccountAsync(string authorization, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<VerifiedAccount?> CompleteLoginAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct) => throw new NotSupportedException();
}

sealed class FakeModeration : IChatModeration
{
    public HashSet<Guid> Muted { get; } = [];
    public List<Aion.Application.Gameplay.ChatLogEntry> Log { get; } = [];
    public string? MuteMessage(Guid account) => Muted.Contains(account) ? "You are muted." : null;
    public void Logged(Aion.Application.Gameplay.ChatLogEntry entry) => Log.Add(entry);
}
