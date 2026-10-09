-- V0053: workspace data keys for envelope encryption of object storage (E05-T09, #54; ADR-011 §6, ADR-015 D10.3).
-- Each workspace has numbered data keys; exactly one is Active and encrypts new objects, Retired ones keep decrypting
-- the objects they wrote (an object's envelope header names its key version, stored_object.key_id records it as
-- 'wdk-v<n>'). A data key is stored only wrapped by a key-encryption key (KEK) of the key provider: the installation
-- KEK, or the workspace's dedicated KEK. Rotating a KEK rewraps these rows (the rewrap job) and never touches objects;
-- destroying a workspace's keys (crypto-shredding, E20-T02) erases wrapped_key and makes every copy of its objects,
-- including backups, unreadable once the dedicated KEK is gone. Rows are kept as the record of what was destroyed.

CREATE TABLE opportunity.workspace_data_key (
    workspace_id    uuid        NOT NULL,
    key_version     integer     NOT NULL,
    -- 1 Active, 2 Retired, 3 Destroyed.
    state           smallint    NOT NULL,
    kek_id          text        NOT NULL,
    kek_version     integer     NOT NULL,
    -- Opaque output of the key provider; NULL once destroyed.
    wrapped_key     bytea       NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    rewrapped_at    timestamptz NULL,
    destroyed_at    timestamptz NULL,
    CONSTRAINT workspace_data_key_pk PRIMARY KEY (workspace_id, key_version),
    CONSTRAINT workspace_data_key_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_data_key_version_ck CHECK (key_version >= 1 AND kek_version >= 1),
    CONSTRAINT workspace_data_key_state_ck CHECK (state BETWEEN 1 AND 3),
    CONSTRAINT workspace_data_key_kek_ck CHECK (kek_id ~ '^[a-z0-9][a-z0-9-]{0,63}$'),
    CONSTRAINT workspace_data_key_material_ck CHECK (
        (state = 3 AND wrapped_key IS NULL AND destroyed_at IS NOT NULL)
        OR (state IN (1, 2) AND wrapped_key IS NOT NULL AND octet_length(wrapped_key) BETWEEN 16 AND 4096 AND destroyed_at IS NULL))
);

SELECT opportunity.enable_workspace_rls('opportunity.workspace_data_key');

CREATE UNIQUE INDEX workspace_data_key_active_uq ON opportunity.workspace_data_key (workspace_id) WHERE state = 1;

COMMENT ON TABLE opportunity.workspace_data_key IS
    'Wrapped per-workspace data keys of envelope-encrypted objects (E05-T09). Never holds plaintext keys.';

-- A destroyed key stays destroyed, and key destruction is refused while the workspace is under a preservation lock
-- (legal hold, V0048): crypto-shredding is deletion of every preserved object at once.
CREATE FUNCTION opportunity.workspace_data_key_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF OLD.state = 3 THEN
        RAISE EXCEPTION 'workspace data key % v% is destroyed and cannot change', OLD.workspace_id, OLD.key_version
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.state = 3 THEN
        PERFORM opportunity.assert_workspace_not_preserved(OLD.workspace_id, TG_TABLE_NAME);
    END IF;
    IF NEW.workspace_id <> OLD.workspace_id OR NEW.key_version <> OLD.key_version OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'workspace data key identity cannot change' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER workspace_data_key_guard
    BEFORE UPDATE ON opportunity.workspace_data_key
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.workspace_data_key_guard();

CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.workspace_data_key
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.workspace_data_key
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();

-- ADR-011 §2.3 as amended: an envelope object's data key is the workspace data key its header names (key_id), not a
-- per-object wrapped key, so wrapped_dek stays NULL for Envelope objects too (reserved). Provider-SSE objects never
-- carry one.
ALTER TABLE opportunity.stored_object DROP CONSTRAINT stored_object_encryption_ck;
ALTER TABLE opportunity.stored_object ADD CONSTRAINT stored_object_encryption_ck CHECK (
    encryption_scheme IN (1, 2) AND (encryption_scheme = 2 OR wrapped_dek IS NULL)) NOT VALID;
ALTER TABLE opportunity.stored_object VALIDATE CONSTRAINT stored_object_encryption_ck;
