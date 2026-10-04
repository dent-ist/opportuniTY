-- V0021: OPT image cross-references of an import and their page reconciliation (E08-T05).
-- Binding: ADR-012 §1.6 (one Imported PageSet per document, one Page per OPT row or multi-page TIFF frame, the original
-- raster as PageImage(Original), ImagesIncomplete when a page image is missing); ADR-011 (the OPT as delivered lives
-- under ws/{id}/imports/{importId}/source/{sha256}; images under ws/{id}/docs/{documentId}/image/{sha256}; rows hold no
-- load-file paths beyond the staged OPT rows of the import); ADR-015 D7 (forced RLS), D15 (import-root jail).
--
-- Lifecycle: the API stores the OPT next to the DAT (or alone, for an OPT-only image load against existing documents);
-- the preparation pass stages every OPT row, matches each document break to a DAT row (or, OPT-only, to itself) and
-- reports the OPT rows that belong to no loadable document; each import chunk then links the images of its rows.

ALTER TABLE opportunity.import_batch
    ADD COLUMN opt_file_name   text    NULL,
    ADD COLUMN opt_object_key  text    NULL,
    ADD COLUMN opt_sha256      bytea   NULL,
    ADD COLUMN opt_size        bigint  NULL,
    -- An OPT-only load: no DAT; the OPT's documents replace the pages of existing documents (ticket review E08-T05).
    ADD COLUMN images_only     boolean NOT NULL DEFAULT false,
    ADD COLUMN opt_rows_total  bigint  NULL,
    ADD CONSTRAINT import_batch_opt_ck CHECK (
        (opt_object_key IS NULL) = (opt_file_name IS NULL)
        AND (opt_object_key IS NULL) = (opt_sha256 IS NULL)
        AND (opt_object_key IS NULL) = (opt_size IS NULL)
        AND (opt_object_key IS NULL OR (
            starts_with(opt_object_key, 'ws/' || replace(workspace_id::text, '-', '') || '/imports/')
            AND length(opt_file_name) BETWEEN 1 AND 255 AND octet_length(opt_sha256) = 32 AND opt_size >= 0))
        AND (NOT images_only OR opt_object_key IS NOT NULL)
        AND (opt_rows_total IS NULL OR opt_rows_total >= 0));

-- ---------------------------------------------------------------------------------------------------------------
-- The OPT rows of an import as the preparation pass read them. doc_no numbers the documents in OPT order (a row with
-- DocBreak 'Y' starts the next one; rows before the first break have doc_no 0). row_no is the data row whose document
-- the OPT document belongs to (OPT-only loads: the document number itself), null for orphans; a chunk links the
-- images of its own rows.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_batch_image (
    workspace_id        uuid    NOT NULL,
    import_batch_id     uuid    NOT NULL,
    opt_row             bigint  NOT NULL,
    line_no             bigint  NOT NULL,
    doc_no              bigint  NOT NULL,
    is_break            boolean NOT NULL,
    image_key           text    NOT NULL,
    volume              text    NOT NULL,
    path                text    NOT NULL,
    page_count          integer NULL,
    -- Why the row could not be read (too few columns, no image key or path); its page is reported missing.
    problem             text    NULL,
    -- Break rows: the normalized key matched against the DAT (control number or Beg Bates) or existing documents.
    match_key           text    NULL,
    row_no              bigint  NULL,
    CONSTRAINT import_batch_image_pk PRIMARY KEY (workspace_id, import_batch_id, opt_row),
    CONSTRAINT import_batch_image_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_batch_image_ck CHECK (opt_row >= 1 AND line_no >= 1 AND doc_no >= 0 AND (row_no IS NULL OR row_no >= 1)
        AND (page_count IS NULL OR page_count >= 0) AND length(image_key) <= 1000 AND length(volume) <= 1000
        AND length(path) <= 4000 AND length(problem) <= 1000 AND length(match_key) <= 1000)
);

CREATE INDEX import_batch_image_row_ix ON opportunity.import_batch_image (workspace_id, import_batch_id, row_no, opt_row)
    WHERE row_no IS NOT NULL;
CREATE INDEX import_batch_image_doc_ix ON opportunity.import_batch_image (workspace_id, import_batch_id, doc_no);

SELECT opportunity.enable_workspace_rls('opportunity.import_batch_image');
REVOKE DELETE, TRUNCATE ON opportunity.import_batch_image FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- First row of each normalized Beg Bates value of the DAT, when the import matches OPT documents by Beg Bates instead of
-- Control Number (profile images.matchBy = begBates).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_batch_bates (
    workspace_id        uuid    NOT NULL,
    import_batch_id     uuid    NOT NULL,
    beg_bates_norm      text    NOT NULL,
    row_no              bigint  NOT NULL,
    CONSTRAINT import_batch_bates_pk PRIMARY KEY (workspace_id, import_batch_id, beg_bates_norm),
    CONSTRAINT import_batch_bates_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_batch_bates_row_ck CHECK (row_no >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.import_batch_bates');
REVOKE UPDATE, TRUNCATE ON opportunity.import_batch_bates FROM opportunity_app;

-- An OPT-only load matched by Beg Bates finds existing documents by their received Beg Bates (case-insensitive default).
CREATE INDEX document_beg_bates_ix ON opportunity.document (workspace_id, upper(btrim(beg_bates)))
    WHERE beg_bates IS NOT NULL;

-- ---------------------------------------------------------------------------------------------------------------
-- Issues now name the load file they refer to: 1 the DAT (row = data row), 2 the OPT (row = 1-based OPT row).
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE opportunity.import_row_issue
    ADD COLUMN source smallint NOT NULL DEFAULT 1,
    ADD CONSTRAINT import_row_issue_source_ck CHECK (source BETWEEN 1 AND 2),
    DROP CONSTRAINT import_row_issue_pk,
    ADD CONSTRAINT import_row_issue_pk PRIMARY KEY (workspace_id, import_batch_id, source, row_no, issue_no);

DROP INDEX opportunity.import_row_issue_severity_ix;
CREATE INDEX import_row_issue_severity_ix
    ON opportunity.import_row_issue (workspace_id, import_batch_id, severity, source, row_no, issue_no);
