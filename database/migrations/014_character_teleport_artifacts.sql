-- Kibelisks (teleport artifacts) a character has bound to ("engraved"): TeleportArtifact ids as in
-- quest objectives (TeleportArtifact_1051035) and the engraved list 0x9232 sent at world entry.
CREATE TABLE game.character_teleport_artifacts (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    artifact_id integer NOT NULL CHECK (artifact_id > 0),
    engraved_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, artifact_id)
);
GRANT SELECT, INSERT ON game.character_teleport_artifacts TO aion_app;
