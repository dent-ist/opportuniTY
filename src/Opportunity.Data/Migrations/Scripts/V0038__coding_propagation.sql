-- V0038: family and duplicate coding propagation (E09-T05, #88; Q-14, Q-48).
-- coding_event.origin_event_id: a propagated change references the CodingEvent of the originating edit on the source
-- document (no foreign key: coding_event is range-partitioned and its key includes occurred_at).
-- coding_layout_field.apply_to_family_default: Q-48 "apply to family by default" (never for security-affecting fields;
-- the layout validator enforces it).
-- coding_propagation_preview: what a preview showed, so the apply writes exactly that (targets, values, origins) or
-- refuses it as stale. Short-lived (10 minutes to apply; rows older than a day are removed opportunistically).

ALTER TABLE opportunity.coding_event ADD COLUMN origin_event_id uuid NULL;

ALTER TABLE opportunity.coding_layout_field ADD COLUMN apply_to_family_default boolean NOT NULL DEFAULT false;

CREATE TABLE opportunity.coding_propagation_preview (
    workspace_id        uuid        NOT NULL,
    preview_id          uuid        NOT NULL,
    created_by          uuid        NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    source_document_id  uuid        NOT NULL,
    scope               smallint    NOT NULL,
    -- [{fieldId, value, changedAtVersion, originEventId}] in field order: the source's coding the preview read.
    operations          jsonb       NOT NULL,
    security_affecting  boolean     NOT NULL,
    target_ids          uuid[]      NOT NULL,
    -- DocumentVersion of each target when previewed (interactive mode only; empty for job mode).
    target_versions     bigint[]    NOT NULL,
    mode                smallint    NOT NULL,
    threshold           integer     NOT NULL,
    CONSTRAINT coding_propagation_preview_pk PRIMARY KEY (workspace_id, preview_id),
    CONSTRAINT coding_propagation_preview_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT coding_propagation_preview_scope_ck CHECK (scope BETWEEN 1 AND 3),
    CONSTRAINT coding_propagation_preview_mode_ck CHECK (mode BETWEEN 1 AND 2),
    CONSTRAINT coding_propagation_preview_operations_ck CHECK (jsonb_typeof(operations) = 'array'),
    CONSTRAINT coding_propagation_preview_versions_ck CHECK (
        cardinality(target_versions) = 0 OR cardinality(target_versions) = cardinality(target_ids))
);

SELECT opportunity.enable_workspace_rls('opportunity.coding_propagation_preview');

CREATE INDEX coding_propagation_preview_created_ix ON opportunity.coding_propagation_preview (workspace_id, created_at);

REVOKE ALL ON opportunity.coding_propagation_preview FROM opportunity_readonly;
