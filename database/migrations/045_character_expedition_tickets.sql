-- Expedition entry tickets (ContentsTicket.dat): used counts per reset period (weekly keys from ShopTables.Period).
CREATE TABLE game.character_expedition_tickets (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    ticket_id integer NOT NULL,
    period text NOT NULL,
    count integer NOT NULL CHECK (count > 0),
    PRIMARY KEY (character_id, ticket_id, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_expedition_tickets TO aion_app;
