-- QuickSlotInfo's last fields (usmap DevPacketData_Common_QuickSlotInfo): _skill_macro_no u8, _pc_social_action_id i32,
-- _toggle_yn u8. QuickSlotSet_RQ sends them; they were dropped, so a slot's toggle came back off after a relog (fix/quickbar).
-- Item slots name the item by its template (official QuickSlotList_NT: item uid always 0), so a slot survives its stack
-- running out, being merged or refilled; existing rows are normalised to that.
ALTER TABLE game.quick_slots ADD COLUMN skill_macro_no smallint NOT NULL DEFAULT 0 CHECK (skill_macro_no BETWEEN 0 AND 255);
ALTER TABLE game.quick_slots ADD COLUMN social_action_id integer NOT NULL DEFAULT 0;
ALTER TABLE game.quick_slots ADD COLUMN toggle boolean NOT NULL DEFAULT false;
UPDATE game.quick_slots SET item_uid = 0 WHERE slot_type = 1 AND item_uid <> 0;
