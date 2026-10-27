-- Rides (pets/mounts, VehicleList ids): acquired by using a pet item (Item.dat UseEffect GetVehicle),
-- usually from a pet chest. A pet item for a ride already owned adds a soul point instead. Sent at
-- world entry as 0x9000 RideInitData_NT; default_ride is the ride CharRideDefaultSet chose.
CREATE TABLE game.character_rides (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    ride_id integer NOT NULL CHECK (ride_id > 0),
    level smallint NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 100),
    soul_points integer NOT NULL DEFAULT 0 CHECK (soul_points >= 0),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, ride_id)
);
ALTER TABLE game.characters ADD COLUMN default_ride integer CHECK (default_ride IS NULL OR default_ride > 0);
GRANT SELECT, INSERT, UPDATE ON game.character_rides TO aion_app;
