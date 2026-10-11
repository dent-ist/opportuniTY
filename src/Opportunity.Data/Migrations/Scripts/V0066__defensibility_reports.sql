-- V0066: defensibility report pack (E14-T05, #119; legal finding 10, baseline §22 and §28).
--
-- * One audit action records each generation of a defensibility report or pack: Audit.DefensibilityReportGenerated
--   (details: the report, its as-of point and its content fingerprint). A stored object whose bytes no longer match its
--   registered hash during a chain-of-custody re-verification raises the existing Integrity.HashMismatch.
-- * A search term report's run records the index lag at execution (the reports must note it): the committed changes
--   search did not reflect yet and the age of the oldest of them when the run started. Runs before V0066 have none.

INSERT INTO audit.audit_action (category, action) VALUES
    ('Audit', 'DefensibilityReportGenerated');

ALTER TABLE opportunity.search_term_report
    ADD COLUMN index_pending_changes bigint NULL,
    ADD COLUMN index_lag_ms bigint NULL,
    ADD CONSTRAINT search_term_report_index_lag_ck CHECK (
        (index_pending_changes IS NULL) = (index_lag_ms IS NULL) AND index_pending_changes >= 0 AND index_lag_ms >= 0);
