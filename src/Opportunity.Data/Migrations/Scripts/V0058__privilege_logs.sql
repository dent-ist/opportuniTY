-- V0058: privilege logs (E13-T03, #111).
-- Binding: Q-19 (generic document-by-document log by default), Q-20 (metadata-only, auto-generated log with an
-- optional per-document description from Privilege Description; CSV and XLSX; categorical logs deferred), Q-52
-- (documents the generator may not see are neither listed nor counted), ADR-002 §4 (a log reuses the production's frozen
-- set), ADR-014 (privilege logs are preserved records, kept by the RetainRecords profile), ADR-015 D7 (forced RLS).
--
-- privilege_log_template: per-workspace column templates (columns, Priv ID numbering, date format and zone, privacy
--   redactions, exclusion rules) as canonical JSON. Configuration: edited with If-Match, never deleted.
-- privilege_log: one generated version of a log. A log series is a source (a finalized production, optionally widened
--   by a review set, or a frozen set) with a template; each generation whose content differs from the series' latest
--   version becomes the next version. The version freezes everything its files are rendered from: the template as
--   used, the canonical metadata (source, columns, exclusion rules with their counts) and the entries, so rendering a
--   version again gives the same bytes; the CSV and XLSX SHA-256 are recorded to prove it. Immutable.
-- privilege_log_entry: the version's rows in log order with their rendered cells. A document appears at most once per
--   version (exactly-once, enforced by the unique key). Immutable.

CREATE TABLE opportunity.privilege_log_template (
    workspace_id            uuid        NOT NULL,
    template_id             uuid        NOT NULL,
    name                    text        NOT NULL,
    -- Canonical template JSON (Opportunity.Contracts.Api.PrivilegeLogTemplateDefinition).
    definition              text        NOT NULL,
    version                 bigint      NOT NULL DEFAULT 1,
    created_by              uuid        NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    modified_by             uuid        NOT NULL,
    modified_by_display     text        NOT NULL,
    modified_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT privilege_log_template_pk PRIMARY KEY (workspace_id, template_id),
    CONSTRAINT privilege_log_template_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT privilege_log_template_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name) AND name !~ '[[:cntrl:]]'),
    CONSTRAINT privilege_log_template_definition_ck CHECK (length(definition) BETWEEN 2 AND 65536),
    CONSTRAINT privilege_log_template_version_ck CHECK (version >= 1),
    CONSTRAINT privilege_log_template_display_ck CHECK (length(modified_by_display) BETWEEN 1 AND 512)
);

CREATE UNIQUE INDEX privilege_log_template_name_uq ON opportunity.privilege_log_template (workspace_id, lower(name));

SELECT opportunity.enable_workspace_rls('opportunity.privilege_log_template');
REVOKE DELETE, TRUNCATE ON opportunity.privilege_log_template FROM opportunity_app;

CREATE TABLE opportunity.privilege_log (
    workspace_id            uuid        NOT NULL,
    log_id                  uuid        NOT NULL,
    -- (source, review set, template): the versions of one log share it.
    series_key              text        NOT NULL,
    version                 integer     NOT NULL,
    -- 1 Production, 2 Snapshot (frozen set).
    source_kind             smallint    NOT NULL,
    production_id           uuid        NULL,
    -- The production's frozen set, or the frozen set the log was generated from.
    snapshot_id             uuid        NOT NULL,
    -- A production log's review set: its members coded Withhold that were not produced are logged as withheld.
    scope_snapshot_id       uuid        NULL,
    -- The template as used (no foreign key: templates are configuration; the version keeps its own copy).
    template_id             uuid        NULL,
    template_name           text        NOT NULL,
    template_definition     text        NOT NULL,
    -- Canonical metadata JSON: source, columns, privacy handling, exclusion rules with their counts, totals.
    metadata                text        NOT NULL,
    content_sha256          bytea       NOT NULL,
    csv_sha256              bytea       NOT NULL,
    csv_bytes               bigint      NOT NULL,
    xlsx_sha256             bytea       NOT NULL,
    xlsx_bytes              bigint      NOT NULL,
    entry_count             integer     NOT NULL,
    withheld_count          integer     NOT NULL,
    redacted_count          integer     NOT NULL,
    excluded_count          integer     NOT NULL,
    -- Fields the columns and exclusion rules read: a reader for whom one is hidden does not see the log.
    field_ids               integer[]   NOT NULL DEFAULT '{}',
    generated_by            uuid        NOT NULL,
    generated_by_display    text        NOT NULL,
    generated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT privilege_log_pk PRIMARY KEY (workspace_id, log_id),
    CONSTRAINT privilege_log_series_version_uq UNIQUE (workspace_id, series_key, version),
    CONSTRAINT privilege_log_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT privilege_log_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT privilege_log_scope_snapshot_fk FOREIGN KEY (workspace_id, scope_snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT privilege_log_source_ck CHECK (CASE source_kind
        WHEN 1 THEN production_id IS NOT NULL
        WHEN 2 THEN production_id IS NULL AND scope_snapshot_id IS NULL
        ELSE false END),
    CONSTRAINT privilege_log_shape_ck CHECK (version >= 1 AND length(series_key) BETWEEN 1 AND 300
        AND length(template_name) BETWEEN 1 AND 200 AND length(template_definition) BETWEEN 2 AND 65536
        AND length(metadata) BETWEEN 2 AND 262144
        AND octet_length(content_sha256) = 32 AND octet_length(csv_sha256) = 32 AND octet_length(xlsx_sha256) = 32
        AND csv_bytes >= 0 AND xlsx_bytes >= 0
        AND entry_count >= 0 AND withheld_count >= 0 AND redacted_count >= 0 AND excluded_count >= 0
        AND withheld_count + redacted_count <= entry_count),
    CONSTRAINT privilege_log_display_ck CHECK (length(generated_by_display) BETWEEN 1 AND 512)
);

CREATE INDEX privilege_log_generated_ix ON opportunity.privilege_log (workspace_id, generated_at DESC, log_id);
CREATE INDEX privilege_log_production_ix ON opportunity.privilege_log (workspace_id, production_id) WHERE production_id IS NOT NULL;
CREATE INDEX privilege_log_snapshot_ix ON opportunity.privilege_log (workspace_id, snapshot_id);

SELECT opportunity.enable_workspace_rls('opportunity.privilege_log');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.privilege_log FROM opportunity_app;

CREATE TABLE opportunity.privilege_log_entry (
    workspace_id            uuid        NOT NULL,
    log_id                  uuid        NOT NULL,
    ordinal                 integer     NOT NULL,
    document_id             uuid        NOT NULL,
    -- 1 Withheld, 2 Redacted (privilege), 3 Redacted (privacy only; listed only when the template includes them).
    treatment               smallint    NOT NULL,
    cells                   text[]      NOT NULL,
    CONSTRAINT privilege_log_entry_pk PRIMARY KEY (workspace_id, log_id, ordinal),
    CONSTRAINT privilege_log_entry_document_uq UNIQUE (workspace_id, log_id, document_id),
    CONSTRAINT privilege_log_entry_log_fk FOREIGN KEY (workspace_id, log_id)
        REFERENCES opportunity.privilege_log (workspace_id, log_id),
    CONSTRAINT privilege_log_entry_ck CHECK (ordinal >= 1 AND treatment BETWEEN 1 AND 3 AND cardinality(cells) BETWEEN 1 AND 100)
);

SELECT opportunity.enable_workspace_rls('opportunity.privilege_log_entry');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.privilege_log_entry FROM opportunity_app;

-- Privilege logs are preserved records (ADR-014 §2.3): a preservation lock (legal hold, V0048) refuses their deletion.
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['privilege_log', 'privilege_log_entry']
    LOOP
        EXECUTE format(
            'CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.%I '
            || 'REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard()', t);
        EXECUTE format(
            'CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.%I '
            || 'FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard()', t);
    END LOOP;
END
$$;

-- The RetainRecords deletion profile keeps privilege logs (ADR-014 §5, Q-23), with the frozen sets they reference.
CREATE OR REPLACE FUNCTION opportunity.workspace_purge_retained(p_table text)
    RETURNS text
    LANGUAGE sql
    IMMUTABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT CASE p_table
        WHEN 'production' THEN 'true'
        WHEN 'production_document' THEN 'true'
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
        WHEN 'redaction_set' THEN
            'EXISTS (SELECT FROM opportunity.production_document pd WHERE pd.workspace_id = $1 AND pd.redaction_set_id = t.redaction_set_id)'
    END;
$$;

INSERT INTO audit.audit_action (category, action) VALUES
    ('Privilege', 'LogDownloaded'),
    ('Privilege', 'LogTemplate.Created'),
    ('Privilege', 'LogTemplate.Modified');
