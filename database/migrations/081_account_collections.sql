-- Skins and rides (mounts/pets) belong to the account: every character of the account shares the skin collection and the ride
-- collection (USER 2026-11-08). What a character wears (character_skin_equips), its default ride and its ride crystals stay per
-- character. The existing per-character rows are merged per account: skin copies add up (they are what skin combining uses),
-- a ride keeps its highest level, its soul points add up and it is owned when any character owned it.
-- game.character_skins and game.character_rides are left as they were (no longer read or written) as a fallback.
CREATE TABLE game.account_skins (
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    skin_id integer NOT NULL CHECK (skin_id > 0),
    count integer NOT NULL DEFAULT 1 CHECK (count > 0),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, skin_id)
);
INSERT INTO game.account_skins(account_id, skin_id, count, acquired_at)
SELECT c.account_id, s.skin_id, sum(s.count), min(s.acquired_at)
FROM game.character_skins s JOIN game.characters c USING (character_id)
GROUP BY c.account_id, s.skin_id;

CREATE TABLE game.account_rides (
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    ride_id integer NOT NULL CHECK (ride_id > 0),
    level smallint NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 100),
    soul_points integer NOT NULL DEFAULT 0 CHECK (soul_points >= 0),
    owned boolean NOT NULL DEFAULT true,
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, ride_id)
);
INSERT INTO game.account_rides(account_id, ride_id, level, soul_points, owned, acquired_at)
SELECT c.account_id, r.ride_id, max(r.level), sum(r.soul_points), bool_or(r.owned), min(r.acquired_at)
FROM game.character_rides r JOIN game.characters c USING (character_id)
GROUP BY c.account_id, r.ride_id;

GRANT SELECT, INSERT, UPDATE, DELETE ON game.account_skins, game.account_rides TO aion_app;
