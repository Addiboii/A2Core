# Progression systems (user queue Q1) - research notes, WIP 2026-11-09

No server code written yet. These are the findings to start from (branch feature/progression).

## What the user's menu names are in the client
| Menu | Client name | Tables / code |
|---|---|---|
| Transcendence | PartyDungeonChallenge = party dungeons at Hard (Dungeon.dat PartyChallengeTier, ClearRankGroup PartyChallenge_01, e.g. 600063 Arkanis_G_01) | DungeonClearRank, ContentsTicket 60005301/60006301 (4 max, +2 daily) |
| Ascension trial | AwakenDungeon = Dungeon.dat type Awaken 910011..910067 (Easy..Extreme, 6-7 levels per group) | ContentsTicket 80000001 (3 per week, Wednesday) |
| Arcana | card items, EEquipSlot kArcana 41-50 | ArcanaCreate, ArcanaEnchant, ArcanaEnchantEffectMainStat/Substat, ArcanaStatFilter |
| Daily dungeon | Dungeon.dat type Daily 900001 DungeonWave_0001 (Unknown Fissure), 900002, 900003 | ContentsTicket 101 (14/week), 102/103; ContentsSweepTicket |
| Substance morph | Conversion (ConversionRecipe, ConversionTabUI, ConversionComponent*) | Craft.cs already sends MyConversionRecipeList 0x8d8c |
| Essence extraction | GatherSkill - SKIPPED (moved to feature/flight-gathering) | |
| Rank | SeasonRanking* tables | |
| Market | Trade* | TradeTab, TradeItemCategory, TradeRecommend |
| Shops | GameShop ShopType CoinShop: coin_shop_abyss/nightmare/L_battlepass/D_battlepass/arena/festa/season/battlefield; shop_L/D_Guild_01, shop_A_Token_Conversion are not on any NPC in tools/data/shops.json | |
Party/Awaken/Daily dungeon maps are already served (tools/map_events.py); clear rewards RewardLight/Dark already paid.

## Live opcodes (dev opcode = usmap order 0x3600+, live = dev + run offset)
- 0x37xx block: +0x1ebe. Arcana: ArcanaEnchant_RQ/RS 0x564f/0x5650, Extract 0x5651/0x5652, ExtractPoint_NT 0x5653, Create 0x5654/0x5655, CreateCoolTime_NT 0x5656, QuickEquip 0x5657/0x5658.
- 0x3axx DungeonRoom block: +0x5c3f (OBSERVED: our trace c2s 0x9700 = DungeonRoomList_RQ {dungeon i32, level u8} 'FF270900 01'; 0x9712 = DungeonRoomInstantJoin_RQ 'FF270900 01 02 01 03'; official s2c 0x9701 List_RS, 0x9702 RoomInfo_NT, 0x970b AddMember_NT, 0x9713 InstantJoin_RS '0000 00000000', 0x9715 Ready_RS '0000 01', 0x9716 Ready_NT, 0x971d Leave_RS, 0x972a Kickout_NT).
  Room/member structs use presence masks and packed bools (member starts 0c/1c; room_key is i32; u16 server ids) - the usmap generic rules do not fit; see official bodies in captures 20261106-115817 etc.
- 0x3bxx up to ~0x3b9d: +0xa6fb (e2 run). DungeonEnter_RQ 0xe232 {dungeon i32} -> RS 0xe233 {result, dungeon}: OUR traces show the user pressing it, UNANSWERED (900001 daily dungeon, 21 Abyss). UpdateCharAscension_NT 0xe225.
  Market: TradeExchangeLimit_NT 0xe25d, TradeServerInfo_NT 0xe263 (OBSERVED), TradeSell 0xe264/0xe265 (RS OBSERVED x5), TradeCancelSale 0xe266/0xe267, TradeRenewSale 0xe268/0xe269, TradeExpireSale 0xe26a/0xe26b, TradePurchase 0xe26c/0xe26d, TradeSold_NT 0xe26e (OBSERVED x3), TradeDeliveryComplete 0xe26f/0xe270 (RS OBSERVED x3), TradeGetSummary 0xe271/0xe272, RankingPointUpdate_NT 0xe273.
- 0x3bxx from ~0x3b9e: +0xa762 (e3 run). AwakenDungeonClear_NT 0xe33f (CANDIDATE), RankingScoreContents_NT 0xe33e (OBSERVED empty).
- 0x39xx (0x8d run): +0x53c5. RetrieveLatestSeasonRanking_RQ 0x8d54 (our trace '548D1504', unanswered) -> RS 0x8d55 (official s2c exists); MyPrevSeasonRanking_NT 0x8d58; ConversionStart_RQ 0x8d86 (our trace, unanswered) -> RS 0x8d87, Cancel 0x8d88/0x8d89, Completed_NT 0x8d8b, MyConversionRecipeList_NT 0x8d8c, ConversionLimitList 0x8d8e/0x8d8f (our trace 0x8d8e '8E8D00').
- 0x8c run: +0x52d2. CoinShopOpen_RQ 0x8c06 (our trace '068C341B0F00' unanswered; official RS 0x8c07 '0000 351b0f00 00 4bb2bfb0a1010000'), CoinShopBuyGoods_RQ 0x8c08 (our trace) -> RS 0x8c09 (official '0006 371b0f00 01 40eb191e 0a000000').
- 0x61 run: +0x28b7. ContentsTickets_NT 0x610b (static hex at entry), ContentsTicketUpdate_NT 0x610c (OBSERVED '00 04 <id i32> <count> <reason>', '00 0c 04000000 0d 05 03' after a boss challenge clear).
- 0x92 run: +0x57e7 -> PartyDungeonChallenge_NT 0x924e, UpdatePartyDungeonChallenge_NT 0x924f, PartyDungeonClear_NT 0x9250 (CANDIDATE, never observed).

## Plan per system (not started)
1+2+4: answer DungeonEnter_RQ 0xe232 with EnterDungeon (daily/awaken tickets, ticket updates 0x610c); DungeonRoom list/instant-join/create -> enter the Hard party dungeon with the party; clear record + rank saved in character settings.
3 Arcana, 5 Conversion, 7 Rank, 9 Market (needs a migration: listings table), 10 Coin shops (GameShopGoodsList of the coin shops; ShopTables + ShopBuyAsync).
Tool used: scratch decode of official bodies with tools/wire_probe.py Rules (does not fit masked structs).

## Status 2026-11-10 (branch feature/progression, merged with upgrade/stage1-step10 0563a7e)
| System | State | Code |
|---|---|---|
| Tickets (all dungeons) | DONE: 0x610b with used counts, 0x610c updates (OBSERVED form, reason 3 = used), table refills (daily/weekly 07:00 UTC), setting `contents_tickets` | GameSession.Tickets.cs, tools/contents_ticket_data.py |
| Daily dungeon, Ascension trial (Awaken), Abyss button | DONE: DungeonEnter_RQ 0xe232 -> RS 0xe233 {result, dungeon} (usmap, NEVER RECORDED, CANDIDATE), Enter tickets 101 / 80000001 taken | GameSession.Tickets.cs |
| Boss challenge ticket | DONE: ticket 4 checked at entry, taken on the clear (OBSERVED) | GameSession.BossChallenge.cs |
| Transcendence | PARTIAL: InstantJoin_RQ 0x9712 / Create_RQ 0x9703 -> OBSERVED `0000 00000000`, the party moves into the PartyDungeonLevel dungeon; Leave_RQ -> `0000`. Room list/room messages/ready/start not served (masked bodies not decoded; a room start never recorded). Transcendence tickets not taken (no table link, deduction unrecorded) | GameSession.DungeonRooms.cs |
| Coin shops | DONE: 0x8c06/0x8c07 and 0x8c08/0x8c09 in the OBSERVED forms; 8 shops, 358 goods; limits share the NPC shop purchase rows | GameSession.CoinShops.cs, tools/coin_shop_data.py |
| Rank | PARTIAL: RetrieveLatestSeasonRanking 0x8d54 -> 0x8d55 OBSERVED layout, empty list (no season points kept) | GameSession.ClientRequests.cs |
| Arcana | NOT STARTED: no official Arcana reply recorded (0x5650/0x5652/0x5655/0x5658 never seen) | - |
| Substance morph (Conversion) | NOT STARTED: ConversionStart_RS 0x8d87 / Completed_NT 0x8d8b / LimitList_RS 0x8d8f never recorded; result maps are masked dicts | - |
| Crafting | already served before this branch (GameSession.Craft.cs) | - |
| Market | NOT STARTED: OBSERVED only TradeSell_RS 0xe265 (0000, sale GUID string, 40 zero bytes, 1c30), TradeSold_NT 0xe26e, TradeDeliveryComplete_RS 0xe270 (0000, GUID, 1c30); search/summary/purchase replies unrecorded; needs a listings table (migration) | - |

## Status 2026-11-10 (branch feature/systems-3, Group 3 audit: a2core-log/systems-audit-2026-11-10.md)
| System | State | Code |
|---|---|---|
| Market | PARTLY: game messages served, sales saved (migration 088). Sell_RS / Sold_NT / DeliveryComplete_RS in the OBSERVED forms; Cancel/Renew/Expire/Purchase/Summary CANDIDATE. BLOCKED: search, price lists, my sales, earnings = the 8087 web API trade/v1.0/* (JSON shapes unknown) | GameSession.Market.cs, PostgresGameplayStore.Systems3.cs |
| Transcendence rooms | DONE in the OBSERVED layout (room/member masks and packed bools decoded): list, create, join, quick join, ready, start (all members into one copy), leave, kick, settings. Applications, invitations, slot moves, retries not served; tickets still not taken | GameSession.DungeonRooms.cs |
| Arcana | PARTLY (CANDIDATE): create (cost, cooltime), enchant (exp, levels, Kina), quick equip. Picked stats not on the card, enchant stats not in the character stats, extract refused (no point table) | GameSession.Arcana.cs, Systems3Tables.cs, tools/systems3_data.py |
| Rank | DONE for BossChallenge / PartyChallenge / Awaken in the OBSERVED layout from stored points (clear points 1000 x level CANDIDATE); Abyss, arena, Conqueror, Suppression empty; grades 0 | GameSession.Ranking.cs |
| Substance morph | PARTLY (CANDIDATE): start + completion at once, limits; replacement / multi-select recipes refused | GameSession.Conversion.cs |
| Daily dungeon / Ascension trial | re-checked against Dungeon.dat: entries and tickets as before; clear notices added; Awaken DeathLimit (5/2 in Dungeon.dat) still not enforced | GameSession.Tickets.cs |
| Essence extraction | re-checked: GatherSkill 21 / GatherSkillEffect 72 / GatherMastery 100 rows = the client tables | GameSession.Gather.cs |
