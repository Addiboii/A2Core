-- Currencies stored as the official server keeps them (USER 2026-11-10: "Kina (All) counts bound + tradable, it and Quna are not
-- inventory items"; a2core-log/currencies-2026-11-10.md, server/Aion.World/Currencies.cs).
-- OBSERVED: every official login list (ItemDepotList_RS) carries Gold 930100001 and GoldNonbinding 930100031 as bag items, never
-- GoldCombined 930100030 ("Kina (All)", the total the client shows) and never a GameCash item 9302000xx (account currencies).
--  * game.grant_item: GoldCombined lands as bound Kina (Item.dat GoldCombined is NonExchangeable, as Gold); a GameCash family item
--    credits game.account_currencies of the character's account (no item row; returns the request id).
--  * Rows already held are folded the same way (each fold written to audit.events).

-- Item.dat 9302000xx -> ECurrencyCategory row of game.account_currencies. GameCash and GameCashNonbinding are both the account's
-- Quna (category 7, all tradable on this server); GameCashBinding 95, Activating 102, Pending 103, Mileage 6, Cash 9.
CREATE OR REPLACE FUNCTION game.account_currency_category(p_template integer) RETURNS smallint
LANGUAGE sql IMMUTABLE AS $$
    SELECT (CASE p_template
        WHEN 930200001 THEN 7 WHEN 930200006 THEN 7 WHEN 930200005 THEN 95 WHEN 930200008 THEN 102
        WHEN 930200007 THEN 103 WHEN 930200003 THEN 6 WHEN 930200004 THEN 9 END)::smallint
$$;
GRANT EXECUTE ON FUNCTION game.account_currency_category(integer) TO aion_app;

CREATE OR REPLACE FUNCTION game.grant_item(p_character uuid, p_build text, p_template integer,
                                          p_quantity bigint, p_request uuid) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
    item_id uuid := gen_random_uuid();
    first_id uuid;
    args jsonb := jsonb_build_object('character',p_character,'build',p_build,'template',p_template,'quantity',p_quantity);
    previous game.item_grants%ROWTYPE;
    max_stack integer;
    remaining bigint := p_quantity;
    room bigint;
    stack record;
    v_category smallint := game.account_currency_category(p_template);
BEGIN
    IF p_quantity IS NULL OR p_quantity <= 0 THEN
        RAISE EXCEPTION 'quantity must be positive';
    END IF;
    IF v_category IS NOT NULL THEN
        -- An account currency: the account's balance, once per request.
        IF EXISTS (SELECT 1 FROM audit.events WHERE action = 'currency_item_granted' AND character_id = p_character
                   AND details->>'request' = p_request::text) THEN
            RETURN p_request;
        END IF;
        INSERT INTO game.account_currencies(account_id, category, balance)
            SELECT ch.account_id, v_category, p_quantity FROM game.characters ch WHERE ch.character_id = p_character
            ON CONFLICT (account_id, category) DO UPDATE SET balance = game.account_currencies.balance + EXCLUDED.balance, updated_at = now();
        INSERT INTO audit.events(action,character_id,details)
            VALUES('currency_item_granted',p_character,jsonb_build_object('request',p_request,'template',p_template,'category',v_category,'quantity',p_quantity));
        RETURN p_request;
    END IF;
    IF p_template = 930100030 THEN
        p_template := 930100001;   -- Kina (All) is a total: the Kina lands as bound Kina
    END IF;
    INSERT INTO game.item_grants VALUES(p_request,args,item_id) ON CONFLICT DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO previous FROM game.item_grants WHERE request_id=p_request;
        IF previous.arguments IS DISTINCT FROM args THEN
            RAISE EXCEPTION 'idempotency key reused with different arguments';
        END IF;
        RETURN previous.instance_id;
    END IF;
    max_stack := game.item_stack_max(p_build, p_template);
    IF max_stack <> 1 THEN
        -- Top up the existing plain bag stacks, fullest first.
        FOR stack IN SELECT instance_id, quantity FROM game.item_instances
                     WHERE character_id = p_character AND build_id = p_build AND template_id = p_template AND location = 'bag'
                       AND upgrades = '{}'::jsonb AND rolled_stats = '{}'::jsonb AND (max_stack = 0 OR quantity < max_stack)
                     ORDER BY quantity DESC, created_at FOR UPDATE
        LOOP
            EXIT WHEN remaining <= 0;
            room := CASE WHEN max_stack = 0 THEN remaining ELSE LEAST(remaining, max_stack - stack.quantity) END;
            UPDATE game.item_instances SET quantity = quantity + room, revision = revision + 1 WHERE instance_id = stack.instance_id;
            first_id := COALESCE(first_id, stack.instance_id);
            remaining := remaining - room;
        END LOOP;
    END IF;
    -- New rows for what is left, a full stack per row.
    WHILE remaining > 0 LOOP
        room := CASE WHEN max_stack = 0 THEN remaining ELSE LEAST(remaining, max_stack) END;
        INSERT INTO game.item_instances(instance_id,character_id,build_id,template_id,quantity)
            VALUES(CASE WHEN first_id IS NULL THEN item_id ELSE gen_random_uuid() END,p_character,p_build,p_template,room);
        first_id := COALESCE(first_id, item_id);
        remaining := remaining - room;
    END LOOP;
    -- The grant record points at the stack the grant landed in (several requests may now share one stack).
    IF first_id <> item_id THEN
        UPDATE game.item_grants SET instance_id = first_id WHERE request_id = p_request;
    END IF;
    INSERT INTO audit.events(action,character_id,details)
        VALUES('item_granted',p_character,jsonb_build_object('request',p_request,'instance',first_id,'template',p_template,'quantity',p_quantity));
    RETURN first_id;
END $$;

-- The rows held so far (staff grants of 2026-11-05..09): Kina (All) into the character's bound Kina, GameCash items onto the account.
DO $fold$
DECLARE
    r record;
    keeper uuid;
BEGIN
    FOR r IN SELECT character_id, min(build_id) AS build_id, sum(quantity) AS quantity, count(*) AS n
             FROM game.item_instances WHERE template_id = 930100030 GROUP BY character_id
    LOOP
        SELECT instance_id INTO keeper FROM game.item_instances
            WHERE character_id = r.character_id AND template_id = 930100001 AND location = 'bag'
              AND upgrades = '{}'::jsonb AND rolled_stats = '{}'::jsonb
            ORDER BY quantity DESC, created_at LIMIT 1 FOR UPDATE;
        IF keeper IS NULL THEN
            INSERT INTO game.item_instances(character_id,build_id,template_id,quantity) VALUES(r.character_id,r.build_id,930100001,r.quantity);
        ELSE
            UPDATE game.item_instances SET quantity = quantity + r.quantity, revision = revision + 1 WHERE instance_id = keeper;
        END IF;
        DELETE FROM game.item_instances WHERE character_id = r.character_id AND template_id = 930100030;
        INSERT INTO audit.events(action,character_id,details)
            VALUES('currency_folded',r.character_id,jsonb_build_object('from',930100030,'to',930100001,'quantity',r.quantity,'rows',r.n));
    END LOOP;

    FOR r IN SELECT i.character_id, ch.account_id, i.template_id, game.account_currency_category(i.template_id) AS category,
                    sum(i.quantity) AS quantity, count(*) AS n
             FROM game.item_instances i JOIN game.characters ch USING (character_id)
             WHERE game.account_currency_category(i.template_id) IS NOT NULL
             GROUP BY i.character_id, ch.account_id, i.template_id
    LOOP
        INSERT INTO game.account_currencies(account_id, category, balance) VALUES(r.account_id, r.category, r.quantity)
            ON CONFLICT (account_id, category) DO UPDATE SET balance = game.account_currencies.balance + EXCLUDED.balance, updated_at = now();
        DELETE FROM game.item_instances WHERE character_id = r.character_id AND template_id = r.template_id;
        INSERT INTO audit.events(action,character_id,details)
            VALUES('currency_folded',r.character_id,jsonb_build_object('from',r.template_id,'category',r.category,'quantity',r.quantity,'rows',r.n));
    END LOOP;
END $fold$;
