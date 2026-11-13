using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Inspect another player (ViewChar), player emotes (PcSocialActionPlay) and the content sweeps: layouts and evidence in
// Aion.Protocol/Native/InspectPackets.cs.
public sealed partial class GameSession
{
    /// <summary>
    /// ViewChar_RQ: the inspect window of a player online on this host (their worn items, guild, titles, daevanion boards, combat
    /// power). An offline or unknown character is refused (Community_Fail_NotFoundCharacter; CANDIDATE: the official reply also
    /// shows offline characters, online 0 in its one sample - the store keeps no other account's worn items for us to read).
    /// </summary>
    List<byte[]> ViewChar(byte[] plain)
    {
        if (InspectPackets.ParseViewChar(plain) is not { } target) return [];
        var session = target.DbId == DbId ? this : World?.Online.Values.Select(p => p.Session).FirstOrDefault(s => s.resources is not null && s.DbId == target.DbId);
        if (session is null) return [InspectPackets.ViewChar(EResult.Community_Fail_NotFoundCharacter, new ViewCharInfo())];
        return [InspectPackets.ViewChar(0, session.ViewCharInfo())];
    }

    /// <summary>This character as the inspect window shows it.</summary>
    internal ViewCharInfo ViewCharInfo()
    {
        var profile = Profile();
        var skins = VisualEquips(all: true).GroupBy(e => e.VisualSlot).ToDictionary(g => g.Key, g => g.First());
        var items = inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail is not null).OrderBy(i => i.Slot).Select(i =>
        {
            var visual = (byte)VisualSlot.GetValueOrDefault(i.Slot);
            var shown = visual != 0 ? skins.GetValueOrDefault(visual) : null;
            return new ViewCharItem(i.ItemId, i.Detail!, visual, shown?.SkinId ?? 0, i.Maker, i.ExpireTime, Display: shown?.Display ?? true);
        }).ToList();
        var boards = daevanionNodes.Select(id => Daevanion.NodeOf(id)?.Board ?? 0).Where(b => b != 0).GroupBy(b => b).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count())).ToList();
        return new ViewCharInfo
        {
            Name = CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), PcId = profile?.PcId ?? 1, Race = (byte)(profile?.Race ?? 1),
            Gender = (byte)(character?.Gender ?? 1), Level = level, EquipItemLevel = EquipItemLevel(), BornServerId = ServerId, CurrentServerId = ServerId,
            Online = true, TitleId = ShownTitle, Guild = MyGuildTag(), Items = items, CustomizingData = character?.CustomizingData ?? [], Boards = boards,
            Titles = titleEquips.Where(t => t.Key is >= 1 and <= 3 && t.Value != 0).OrderBy(t => t.Key).Select(t => ((byte)t.Key, t.Value)).ToList(),
            CombatPower = CombatPower(),
        };
    }

    /// <summary>
    /// PcSocialActionPlay_RQ: an emote. The reply (result 0, the request's social action) to the player and the notice to those who
    /// see them; a dead player is refused (SocialAction_Dead). CANDIDATE (no official sample).
    /// </summary>
    List<byte[]> SocialActionPlay(byte[] plain)
    {
        var action = plain.AsSpan(2);
        if (dead) return [InspectPackets.SocialActionPlayReply(playerKey, EResult.SocialAction_Dead, action)];
        if (worldPlayer is { Map: { } map } me) map.Broadcast(me, [InspectPackets.SocialActionPlayNotice(playerKey, action)]);
        return [InspectPackets.SocialActionPlayReply(playerKey, 0, action)];
    }

    /// <summary>
    /// ContentsSweep_RQ / BossChallengeSweepWithoutPlay_RQ: not supported yet, refused as the client knows it
    /// (ContentsSweep_NoSweepData, no reward). The sweep tickets (ContentsSweepTicket: the wave dungeon, two daily dungeons, the
    /// tier-1 boss challenge) stay in the bag.
    /// </summary>
    List<byte[]> ContentsSweep(byte[] plain) =>
        [InspectPackets.ContentsSweepReply(EResult.ContentsSweep_NoSweepData, plain.Length >= 3 ? plain[2] : 0)];

    List<byte[]> BossChallengeSweep(byte[] plain) => InspectPackets.ParseBossChallengeSweep(plain) is { } sweep
        ? [InspectPackets.BossChallengeSweepReply(EResult.ContentsSweep_NoSweepData, sweep.Count, sweep.Group, sweep.Level)]
        : [InspectPackets.BossChallengeSweepReply(EResult.ContentsSweep_NoSweepData, 0, 0, 0)];
}
