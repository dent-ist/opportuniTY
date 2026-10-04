-- V0026: overlay provenance (E08-T07). Every document an overlay import changes gets one row with the old and new value
-- of each column and metadata key it changed and the DocumentVersion the change created. Audit references these rows
-- from the chunk's Import.Overlaid event instead of carrying metadata values itself (ADR-013 §6.2, §7), like coding
-- values live in coding_event. Append-only; retained with the workspace.
CREATE TABLE opportunity.document_overlay_event (
    workspace_id        uuid        NOT NULL,
    import_batch_id     uuid        NOT NULL,
    row_no              bigint      NOT NULL,
    document_id         uuid        NOT NULL,
    document_version    bigint      NOT NULL,
    job_id              uuid        NOT NULL,
    actor_id            uuid        NOT NULL,
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    changes             jsonb       NOT NULL,
    CONSTRAINT document_overlay_event_pk PRIMARY KEY (workspace_id, import_batch_id, row_no),
    CONSTRAINT document_overlay_event_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT document_overlay_event_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_overlay_event_changes_ck CHECK (jsonb_typeof(changes) = 'object'),
    CONSTRAINT document_overlay_event_version_ck CHECK (document_version > 1)
);

COMMENT ON TABLE opportunity.document_overlay_event IS
    'Old and new values of each document an overlay import changed (E08-T07); referenced by the Import.Overlaid audit event.';

SELECT opportunity.enable_workspace_rls('opportunity.document_overlay_event');

CREATE INDEX document_overlay_event_document_ix ON opportunity.document_overlay_event (workspace_id, document_id, document_version);

REVOKE UPDATE, DELETE ON opportunity.document_overlay_event FROM opportunity_app;

INSERT INTO audit.audit_action (category, action) VALUES ('Import', 'Overlaid');
