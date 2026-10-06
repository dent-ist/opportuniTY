-- V0041: field and coding layout administration (E04-T06, #44).
-- field_definition.version / coding_layout.version: the ETag of the admin API (ADR-019 §2.7). Every change to a field,
-- its choices or a layout increments it; If-Match is checked under the row lock in the transaction of the change.
-- Field and layout changes are audited in that same transaction (ADR-013 §2.1).

ALTER TABLE opportunity.field_definition ADD COLUMN version bigint NOT NULL DEFAULT 1;

ALTER TABLE opportunity.coding_layout ADD COLUMN version bigint NOT NULL DEFAULT 1;

INSERT INTO audit.audit_action (category, action) VALUES
    ('Workspace', 'Field.Created'),
    ('Workspace', 'Field.Modified'),
    ('Workspace', 'Field.Retired'),
    ('Workspace', 'CodingLayout.Created'),
    ('Workspace', 'CodingLayout.Modified'),
    ('Workspace', 'CodingLayout.Deleted');
