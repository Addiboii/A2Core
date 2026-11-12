using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// What a character keeps over a logout (GameSession.LogoutState.cs): a timed buff comes back with the time it had left, which
/// does not run while logged out (OBSERVED in the community recordings 2026-11-04 to 06; reports/movement.md).
/// </summary>
static class LogoutStateChecks
{
    const int Steak = 510715007;   // White Dragon's Chopped Steak: HPMax +140 for 300 s (food buff)
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var store = new FakeStore();
        var session = await created(store);
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", Steak, 1, "bag", null, 1, Empty, Empty, 0x7301));
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.InventoryChangesAsync(default);
        var unfed = session.Resource.HpMax;
        await session.HandleAsync(new PacketWriter().U16(0x5612).I64(0x7301).ToArray(), now);
        var food = session.ActiveBuffs.Except([10002]).Single();
        Check(session.Resource.HpMax == unfed + 140, "the food buff is on (HPMax +140)");

        // Logout 100 s later (Logout_RQ saves first), back a day later: 200 s are left.
        await session.HandleAsync(new PacketWriter().U16(0x3630).ToArray(), now + 100_000);
        Check(store.Settings.ContainsKey("logout_state"), "the logout stores the buffs (settings logout_state)");
        var later = now + 86_400_000;
        var again = await created(store);
        await again.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), later);
        var entry = await again.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), later);
        Check(again.ActiveBuffs.Contains(food) && again.Resource.HpMax == unfed + 140
              && entry.Any(b => b[0] == 0x2a && b[1] == 0x38 && b.AsSpan().IndexOf(BitConverter.GetBytes(food)) > 0),
              "after a day offline the buff is back (0x382a at entry, HPMax +140): its time did not run while logged out");
        await again.TickAsync(later + 199_000);
        Check(again.ActiveBuffs.Contains(food), "199 s after the entry it still lasts (200 s were left)");
        await again.TickAsync(later + 200_100);
        Check(!again.ActiveBuffs.Contains(food) && again.Resource.HpMax == unfed, "and ends after the 200 s it had left");
        return checks;
    }
}
