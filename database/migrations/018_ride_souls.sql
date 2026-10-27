-- Soul points collect for rides not owned yet (soul items, Item.dat GetVehicleSoul); a ride is acquired
-- with VehicleList NeedSoul souls (CharRideAcquire_RQ) or its pet item, and levels up with VehicleLevel
-- NeedSoulCount souls (RideLevelUp_RQ). Rows from 017 were all acquired rides.
ALTER TABLE game.character_rides ADD COLUMN owned boolean NOT NULL DEFAULT true;
