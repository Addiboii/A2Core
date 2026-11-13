-- Item storage audit 2026-11-11 (a2core-log/item-storage-audit-2026-11-11.md, server/Aion.World/ItemRules.cs).
-- game.item_stacks (migration 034, Item.dat MaxStackCount) against the client table of build 6f7fea7b... (Table_Item, 9261 items):
-- 2016 stacking Usable/Misc items, all with the same limit, except Box_Material_Drop_A_c_01_b 533700077 "Shulak Loot Bundle
-- (Bound)", MaxStackCount 999, missing: every one granted became a row of its own. Equipment (1) and currencies (no limit) come from
-- game.item_stack_max's ItemType rules. The 9 rows of 034 that this build no longer has (503700081/082, 530716070/071, 535310001,
-- 662560017..020) are kept: no catalog row of this build reaches them.
-- Rows already held apart are merged by a2core-log/item-cleanup-2026-11-11.sql (run by hand), not here.
INSERT INTO game.item_stacks(template_id, max_stack) VALUES (533700077, 999)
    ON CONFLICT (template_id) DO UPDATE SET max_stack = EXCLUDED.max_stack;
