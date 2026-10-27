-- Ascension grade (usmap EAscensionGrade 1..6). Characters start at 1, the value in the
-- official level-one MyCharAppear; flight needs a higher grade (the client's dev menu uses
-- SetAscension 3). CANDIDATE: the spawn byte after reputation is CharDetail._ascension.
-- Changed only by staff (admin command "ascension"); world saves never write it.
ALTER TABLE game.characters ADD COLUMN ascension smallint NOT NULL DEFAULT 1 CHECK (ascension BETWEEN 1 AND 6);
