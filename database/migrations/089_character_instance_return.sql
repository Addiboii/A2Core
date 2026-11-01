-- Field map and position the player entered an instance or quest dungeon from (LastPosition exit after clear or leave).
ALTER TABLE game.characters
    ADD COLUMN return_map integer,
    ADD COLUMN return_x double precision,
    ADD COLUMN return_y double precision,
    ADD COLUMN return_z double precision;
