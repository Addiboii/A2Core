-- Entry tickets (ContentsTicket.dat ids, tools/data/expedition.json): the count a character had at updated_at. The
-- server adds the daily / weekly / cycle recharges passed since then when it loads the row (ContentTicketTables).
-- No row: the ticket is full. (Idea from A2Core PR #4, Artezianec.)
CREATE TABLE game.character_content_tickets (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    ticket_id integer NOT NULL,
    remaining integer NOT NULL CHECK (remaining >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, ticket_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_content_tickets TO aion_app;
