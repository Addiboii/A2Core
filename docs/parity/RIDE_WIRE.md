# Ride mount wire layouts (usmap + live ActionProtocol)

Reference build: usmap `AION2-5.3.2.0-2.0.6.0`. Opcode run `0x9000`–`0x9006` (ride ActionProtocol).

## Why i32 key / i32 result broke mount (#242 regression)

`DevPacketData_GameServer_CharGetOnRide_RS` inherits `ActionProtocol_RS` (_key) then adds _result, _ride_data_id, move fields. The **usmap** types are `IntProperty`, but the **live client** matches other ActionProtocol replies (e.g. `0x3743` GlideStart_RS): **varint _key**, **u16 _result**.

Sending i32+i32 (as in build `4b5b340`) produces bytes like:

```text
0290 A1860100 00000000 30040000 010000
     ^^^^^^^^ ^^^^^^^^ ^^^^^^^^
     key i32  result i32 (misaligned vs client decoder)
```

The client stays in `ETransferVehicleState::WaitGetOnComplete` (6), retries `0x9001`, then dismounts on combat skill (`0x9006`).

Correct **0x9002** for key `100001`, result `0`, ride `1072`, move model Run (1), sub Stop (0):

```text
0290 A18D06 0000 30040000 010000
     varint   u16  i32 ride   move tail
```

Correct **0x9003** (no _result; **one** varint key, not i32 key + i32 ride id):

```text
0390 A18D06 30040000 010000
```

## 0x9002 CharGetOnRide_RS (field order)

| Offset | Field | Wire |
|--------|--------|------|
| 0 | opcode | u16 LE `0x9002` |
| 2 | `_key` | varint (ActionProtocol_RS) |
| | `_result` | u16 LE (`0` = success; client names `kCharGetOnRide_Success`) |
| | `_ride_data_id` | i32 LE |
| | `_move_state` | u8 (`EMoveStateModelType`; use Run=1 while seated) |
| | `_move_substate` | u8 (`EMoveStateType` substate) |
| | `_is_sprinting` | bool u8 |

## 0x9003 CharGetOnRide_NT

Same as RS after `_result`: varint `_key` | i32 `_ride_data_id` | move tail.

## 0x9000 RideInitData_NT (schema order)

Flattened usmap / `tools/packet_schema.py`:

1. `_default_ride_data_id` — i32 LE  
2. `_acquired_ride_map` — varint count, then pairs: i32 key, `RideInfo` { i32 `_ride_data_id`, i32 `_level` }  
3. `_acquired_ride_soul_point_map` — varint count, i32 → i32  
4. `_acquired_ride_soul_crystal_map` — varint count, u8 `ESoulCrystalType` → i32  
5. `_acquired_ride_collection_list` — varint count, i32[]  
6. `_creature_board_map` — varint count, u8 `ECreatureType` → `CreatureBoardInfo`  
7. `_creature_board_selected_panel_no` — u8  

Example prefix (default ride **1072**, one acquired ride level 1, empty soul/crystal/collection, one board entry — truncated):

```text
0090 30040000 01 30040000 30040000 01000000 00 00 00 01 06 ...
     default    map count   key    RideInfo id+level   souls crystals coll boards...
```

Maps use **varint** entry counts (not i32). Malformed init can make the client ignore owned rides before `0x9001`.

## Client ride flow (usmap names)

- `AionCharacterAction_GetOnRide` — client action when mounting  
- `ETransferVehicleState::WaitGetOnComplete` (6) — waits for server ack after `0x9001`  
- Result name constants: `kCharGetOnRide_Success`, `kCharGetOnRide_Fail_*` (zone, combat, already riding, …)  
- Passive skill **1004** “Mount” exists in `class-skills.json`; live mount in captures is **`0x9001` directly** without a preceding `0x3800` SkillStart from the server. Summon cast bar, if shown, is driven client-side until RS/NT complete the transfer state.

## 0x9001 CharGetOnRide_RQ

i32 `_ride_data_id` only (after opcode).
