-- Soul crystals (ESoulCrystalType 1 Feral .. 5 Special, 6 All): from crystal items (Item.dat
-- GetVehicleSoulCrystal) or crystallized soul points (RideSoulCrystallize_RQ); they stand in for missing
-- soul points of rides of their type when a ride is acquired or levelled (CANDIDATE rule).
CREATE TABLE game.character_ride_crystals (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    crystal_type smallint NOT NULL CHECK (crystal_type BETWEEN 1 AND 6),
    amount integer NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (character_id, crystal_type)
);
GRANT SELECT, INSERT, UPDATE ON game.character_ride_crystals TO aion_app;
