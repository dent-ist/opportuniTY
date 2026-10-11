-- V0063: ethical-wall proof, access reviews and verifiable audit exports (E14-T06, #120; legal finding 12, SEC-13).
--
-- * Two audit actions record the generation of the reports: Security.WallProofGenerated and
--   Security.AccessReviewGenerated (Audit.Exported is already in the V0010 taxonomy).
-- * The application role may read the purge records (audit.chain_gap) of its own workspace's chain, so an audit export
--   of a range the retention purge has thinned still accounts for every position. Row-level security confines it to the
--   chain of app.workspace_id, as for audit.audit_event and audit.checkpoint (V0010, V0056); the sealer and the
--   retention role keep reading every chain. The records stay append-only for every role (V0056 triggers).

INSERT INTO audit.audit_action (category, action) VALUES
    ('Security', 'WallProofGenerated'),
    ('Security', 'AccessReviewGenerated');

GRANT SELECT ON audit.chain_gap TO opportunity_app;

ALTER TABLE audit.chain_gap ENABLE ROW LEVEL SECURITY;
CREATE POLICY chain_gap_scope_read ON audit.chain_gap
    AS PERMISSIVE FOR SELECT TO PUBLIC
    USING (chain_id = NULLIF(current_setting('app.workspace_id', true), '')::uuid);
CREATE POLICY chain_gap_chain_read ON audit.chain_gap
    AS PERMISSIVE FOR SELECT TO opportunity_audit_sealer, opportunity_audit_retention
    USING (true);
