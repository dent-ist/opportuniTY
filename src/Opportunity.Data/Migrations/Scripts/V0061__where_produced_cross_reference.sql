-- V0061: where-produced lookup and Bates cross-reference (E13-T04, #112).
-- The lookup reads the existing Bates → DocumentId cross-reference (production_document, V0038) of produced productions
-- and their volume runs (export.production_id, V0052) through existing indexes; downloads of the cross-reference are
-- audited (ADR-013).

INSERT INTO audit.audit_action (category, action) VALUES
    ('Production', 'CrossReferenceExported');
