-- V0013: per-user UI preferences (E15-T03): keyboard bindings, theme and density, pane sizes and, later, grid views.
-- Installation-level like app_user: a preference belongs to the user, not to a workspace, so there is no workspace_id
-- and no RLS. Values are opaque JSON owned by the web client; they must never hold workspace data (ADR-018 §5).

CREATE TABLE opportunity.user_preference (
    user_id                 uuid        NOT NULL,
    pref_key                text        NOT NULL,
    value                   jsonb       NOT NULL,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT user_preference_pk PRIMARY KEY (user_id, pref_key),
    CONSTRAINT user_preference_user_fk FOREIGN KEY (user_id) REFERENCES opportunity.app_user (user_id) ON DELETE CASCADE,
    CONSTRAINT user_preference_key_ck CHECK (pref_key ~ '^[A-Za-z][A-Za-z0-9_.-]{0,99}$'),
    -- The API caps a value at 16 KiB of JSON text; this is the storage backstop.
    CONSTRAINT user_preference_value_ck CHECK (pg_column_size(value) <= 65536)
);

COMMENT ON TABLE opportunity.user_preference IS '@global per-user UI preferences (E15-T03); never workspace data';
