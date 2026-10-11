-- V0060: reviewer attestation and protective-order acknowledgment (E20-T03, #168; legal finding 14, ADR-013 §5,
-- ADR-014 §5). Workspace administrators publish the text members must accept (an Exhibit A undertaking, confidentiality
-- or conflicts attestation) as numbered, immutable versions; a member who has not accepted the current version reaches
-- no workspace content: PEP-1 answers 403 acknowledgment-required on every workspace route except the ones that read
-- and accept the text (and the workspace descriptor). Publishing a new version requires everyone to accept again.
--
-- acknowledgment_version: one row per published version. text_sha256 is the SHA-256 (hex) of the UTF-8 canonical text
--   title || LF LF || body (LF line endings); the check constraint recomputes it, so a stored hash always matches the
--   stored text. Rows are never changed or deleted by the application.
-- acknowledgment: one row per user and accepted version, with the hash of the text accepted (the composite foreign key
--   keeps it equal to the version's hash) and the id of the Security.AcknowledgmentAccepted audit event written in the
--   same transaction. user_id and published_by reference the installation-level app_user without a foreign key (ADR-005 P3).
--
-- Both are acknowledgment rosters, which the RetainRecords deletion profile keeps (ADR-014 §5), so
-- workspace_purge_retained is replaced with the two tables added; the PurgeAll profile removes them.

CREATE TABLE opportunity.acknowledgment_version (
    workspace_id    uuid        NOT NULL,
    version         integer     NOT NULL,
    title           text        NOT NULL,
    body            text        NOT NULL,
    text_sha256     text        NOT NULL,
    published_by    uuid        NOT NULL,
    published_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT acknowledgment_version_pk PRIMARY KEY (workspace_id, version),
    CONSTRAINT acknowledgment_version_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT acknowledgment_version_hash_uq UNIQUE (workspace_id, version, text_sha256),
    CONSTRAINT acknowledgment_version_version_ck CHECK (version >= 1),
    CONSTRAINT acknowledgment_version_title_ck CHECK (
        length(title) BETWEEN 1 AND 200 AND title = btrim(title) AND title !~ '[[:cntrl:]]'),
    CONSTRAINT acknowledgment_version_body_ck CHECK (
        length(body) BETWEEN 1 AND 65536 AND body = btrim(body) AND translate(body, E'\n\t', '') !~ '[[:cntrl:]]'),
    CONSTRAINT acknowledgment_version_hash_ck CHECK (
        text_sha256 = encode(sha256(convert_to(title || E'\n\n' || body, 'UTF8')), 'hex'))
);

SELECT opportunity.enable_workspace_rls('opportunity.acknowledgment_version');

CREATE TABLE opportunity.acknowledgment (
    workspace_id    uuid        NOT NULL,
    user_id         uuid        NOT NULL,
    version         integer     NOT NULL,
    text_sha256     text        NOT NULL,
    accepted_at     timestamptz NOT NULL DEFAULT now(),
    audit_event_id  uuid        NOT NULL,
    CONSTRAINT acknowledgment_pk PRIMARY KEY (workspace_id, user_id, version),
    CONSTRAINT acknowledgment_version_fk FOREIGN KEY (workspace_id, version, text_sha256)
        REFERENCES opportunity.acknowledgment_version (workspace_id, version, text_sha256)
);

SELECT opportunity.enable_workspace_rls('opportunity.acknowledgment');

CREATE INDEX acknowledgment_by_version_ix ON opportunity.acknowledgment (workspace_id, version, accepted_at);

-- Evidence of who undertook what: append-only for the application. Only the deletion purge (SECURITY DEFINER, refused
-- while a preservation lock is active) removes rows, and only under the PurgeAll profile.
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.acknowledgment_version, opportunity.acknowledgment FROM opportunity_app;

-- Preserved records (ADR-014 §2.3): no role deletes or truncates them while the workspace is under a legal hold.
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.acknowledgment_version
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.acknowledgment_version
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.acknowledgment
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.acknowledgment
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();

INSERT INTO audit.audit_action (category, action) VALUES
    ('Security', 'AcknowledgmentPublished'),
    ('Security', 'AcknowledgmentRosterExported');

CREATE OR REPLACE FUNCTION opportunity.workspace_purge_retained(p_table text)
    RETURNS text
    LANGUAGE sql
    IMMUTABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT CASE p_table
        WHEN 'production' THEN 'true'
        WHEN 'production_document' THEN 'true'
        WHEN 'production_qc_run' THEN 'true'
        WHEN 'production_qc_exception' THEN 'true'
        WHEN 'bates_range' THEN 'true'
        WHEN 'privilege_log' THEN 'true'
        WHEN 'privilege_log_entry' THEN 'true'
        WHEN 'export' THEN 't.production_id IS NOT NULL'
        WHEN 'export_document' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'export_file' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'stored_object' THEN
            'EXISTS (SELECT FROM opportunity.export_file f JOIN opportunity.export e ON e.workspace_id = f.workspace_id AND e.export_id = f.export_id '
            || 'WHERE f.workspace_id = $1 AND f.object_id = t.object_id AND e.production_id IS NOT NULL)'
        WHEN 'document_set_snapshot' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT l.snapshot_id FROM opportunity.privilege_log l WHERE l.workspace_id = $1 '
            || 'UNION SELECT l.scope_snapshot_id FROM opportunity.privilege_log l WHERE l.workspace_id = $1 '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'document_set_snapshot_page' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT l.snapshot_id FROM opportunity.privilege_log l WHERE l.workspace_id = $1 '
            || 'UNION SELECT l.scope_snapshot_id FROM opportunity.privilege_log l WHERE l.workspace_id = $1 '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'job' THEN
            '(EXISTS (SELECT FROM opportunity.production p WHERE p.workspace_id = $1 AND p.bates_job_id = t.job_id) '
            || 'OR EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL AND e.job_id = t.job_id))'
        WHEN 'acknowledgment_version' THEN 'true'
        WHEN 'acknowledgment' THEN 'true'
        WHEN 'redaction_set' THEN
            'EXISTS (SELECT FROM opportunity.production_document pd WHERE pd.workspace_id = $1 AND pd.redaction_set_id = t.redaction_set_id)'
    END;
$$;
