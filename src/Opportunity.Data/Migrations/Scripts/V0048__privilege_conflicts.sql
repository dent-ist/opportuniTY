-- V0048: family and duplicate privilege conflicts (E13-T02, #110; §14, Q-14, Q-52).
-- Detection is a read over existing tables (document family/duplicate keys, Privilege Status choices by system_key, the
-- coding current state), served by the existing indexes: document_coding_choice_choice_ix seeds the candidates,
-- document_family_ix and document_duplicate_group_ix group them. A production finalized over unresolved conflicts records
-- the override in its manifest and audits Privilege.ConflictOverride (already in the taxonomy since V0010).
-- This migration only adds the audit action of the report's CSV download through the protected-content gateway.

INSERT INTO audit.audit_action (category, action) VALUES
    ('Privilege', 'ConflictReportExported');
