-- V0054: automatic burn-in verification of production volumes (E12-T06, #105).
-- Binding: ADR-012 §5 and Q-22 (redactions are burned into images; a redacted document is never produced natively and
-- its text is never the original); Q-08 (every run of a production writes the same files, the verification report
-- included); ADR-015 D7 (RLS: the columns live on the existing tenant tables).
--
-- Every volume run verifies each redacted and withheld member before it may complete: the pages that carry redactions
-- are read back and checked pixel by pixel in the render sandbox, and text and native are compared with what may be
-- shipped. The chunks write their rows as parts (kind 12), the finalization assembles them into the burn-in QC report
-- (a Verification file, never delivered) and records the totals here. A run with a failed check ends as Failed with its
-- report: its volume is never delivered.

-- 12 VerificationPart (a chunk's burn-in verification rows; never delivered).
ALTER TABLE opportunity.export_file DROP CONSTRAINT export_file_ck;
ALTER TABLE opportunity.export_file ADD CONSTRAINT export_file_ck CHECK (length(path) BETWEEN 1 AND 1000 AND path !~ '[[:cntrl:]]'
    AND kind BETWEEN 1 AND 12 AND octet_length(sha256) = 32 AND size_bytes >= 0 AND length(content_type) <= 200
    AND (chunk_sequence IS NULL OR chunk_sequence >= 1) AND (ordinal IS NULL OR ordinal >= 1));

-- 1 Passed, 2 Failed; set once with the run's completion or rejection.
ALTER TABLE opportunity.export
    ADD COLUMN verification_status smallint NULL,
    ADD COLUMN verification_documents bigint NULL,
    ADD COLUMN verification_pages bigint NULL,
    ADD COLUMN verification_boxes bigint NULL,
    ADD COLUMN verification_failures bigint NULL,
    ADD COLUMN verification_report_sha256 bytea NULL,
    ADD CONSTRAINT export_verification_ck CHECK (
        (verification_status IS NULL AND verification_documents IS NULL AND verification_pages IS NULL AND verification_boxes IS NULL
            AND verification_failures IS NULL AND verification_report_sha256 IS NULL)
        OR (verification_status IN (1, 2) AND production_id IS NOT NULL AND verification_documents >= 0 AND verification_pages >= 0
            AND verification_boxes >= 0 AND verification_failures >= 0 AND octet_length(verification_report_sha256) = 32
            AND (verification_status = 1) = (verification_failures = 0))),
    -- A run whose verification failed never completes (runs completed before V0054 have no verification).
    ADD CONSTRAINT export_volume_verified_ck CHECK (status <> 2 OR verification_status IS DISTINCT FROM 2);
