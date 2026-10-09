-- V0052: production volume outputs (E12-T05, #104).
-- Binding: Q-08 (a production is reproducible: every volume run of a finalized production reads only what was frozen
-- with it, so the same production writes byte-identical files); Q-21 (TIFF G4 / JPEG by file type, natives with slip
-- sheets, placeholders, text, DAT/OPT); Q-22 and ADR-012 §4-§5 (redactions are burned into images; a redacted document
-- is never produced natively and its text is never the original); ADR-010 (chunked, resumable jobs with fences);
-- ADR-011 (outputs in object storage, downloaded through the protected-content gateway); ADR-015 D7 (RLS).
--
-- A volume run is an export row with production_id set: the export pipeline's tables (outcome rows, registered files,
-- claims, report) hold it, its job is a Production job with ProductionVolumeChunk chunks, and export listings leave it
-- out. Finalization also freezes each member's page set and its redaction version in the production's Redaction Set, so
-- a later redaction edit or re-render never changes what a run of this production burns.

ALTER TABLE opportunity.job DROP CONSTRAINT job_operation_kind_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_operation_kind_ck CHECK (operation_kind IN (
    'ImportChunk', 'BulkCodingChunk', 'RelationshipChunk', 'IndexChunk', 'ReindexChunk', 'ExportChunk',
    'ProductionChunk', 'RenderChunk', 'SearchTermReportChunk', 'ProductionVolumeChunk'));

ALTER TABLE opportunity.export
    ADD COLUMN production_id uuid NULL,
    ADD CONSTRAINT export_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id);

CREATE INDEX export_production_ix ON opportunity.export (workspace_id, production_id, created_at DESC, export_id)
    WHERE production_id IS NOT NULL;

-- 10 PagePart (a chunk's produced-page records), 11 Verification (what the burn-in verification reads; never delivered).
ALTER TABLE opportunity.export_file DROP CONSTRAINT export_file_ck;
ALTER TABLE opportunity.export_file ADD CONSTRAINT export_file_ck CHECK (length(path) BETWEEN 1 AND 1000 AND path !~ '[[:cntrl:]]'
    AND kind BETWEEN 1 AND 11 AND octet_length(sha256) = 32 AND size_bytes >= 0 AND length(content_type) <= 200
    AND (chunk_sequence IS NULL OR chunk_sequence >= 1) AND (ordinal IS NULL OR ordinal >= 1));

ALTER TABLE opportunity.production_document
    ADD COLUMN page_set_id uuid NULL,
    ADD COLUMN redaction_set_id uuid NULL,
    ADD COLUMN redaction_version bigint NULL,
    ADD COLUMN redaction_count integer NULL,
    ADD CONSTRAINT production_document_redaction_set_fk FOREIGN KEY (workspace_id, redaction_set_id)
        REFERENCES opportunity.redaction_set (workspace_id, redaction_set_id),
    ADD CONSTRAINT production_document_redaction_ck CHECK ((redaction_set_id IS NULL) = (redaction_version IS NULL)
        AND (redaction_version IS NULL) = (redaction_count IS NULL) AND redaction_version >= 0 AND redaction_count >= 0);

INSERT INTO audit.audit_action (category, action) VALUES
    ('Production', 'VolumeCompleted');
