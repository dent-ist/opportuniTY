-- V0050: review batches (E10-T05, #93; baseline §14, Q-53, familiarity guide §5.3). Domain and API only; the batching UI
-- is E10-T06. A Batch Set is cut from a materialized ReviewBatch snapshot (ADR-002) into batches of at most N documents,
-- keeping families (and optionally email threads) together; membership is frozen at creation. Batches move Available →
-- Checked out (one reviewer) → Completed; every checkout is kept so a reviewer's calls can be attributed to the batch
-- through CodingEvent provenance (first-pass vs QC conflicts).

ALTER TABLE opportunity.document_set_snapshot DROP CONSTRAINT document_set_snapshot_purpose_ck;
ALTER TABLE opportunity.document_set_snapshot ADD CONSTRAINT document_set_snapshot_purpose_ck
    CHECK (purpose IN ('BulkCoding', 'Export', 'Production', 'Report', 'ReviewBatch'));

CREATE TABLE opportunity.review_batch_set (
    workspace_id            uuid        NOT NULL,
    batch_set_id            uuid        NOT NULL,
    name                    text        NOT NULL,
    batch_prefix            text        NOT NULL,
    max_batch_size          integer     NOT NULL,
    keep_families_together  boolean     NOT NULL,
    keep_threads_together   boolean     NOT NULL,
    -- 1 FirstPass, 2 Qc (the QC set names the first-pass set it checks).
    review_pass             smallint    NOT NULL,
    qc_of_batch_set_id      uuid        NULL,
    -- Optional IdP group whose members may check batches out (managers may always assign).
    reviewer_group          text        NULL,
    snapshot_id             uuid        NOT NULL,
    batch_count             integer     NOT NULL,
    document_count          bigint      NOT NULL,
    created_by              uuid        NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT review_batch_set_pk PRIMARY KEY (workspace_id, batch_set_id),
    CONSTRAINT review_batch_set_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT review_batch_set_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT review_batch_set_qc_fk FOREIGN KEY (workspace_id, qc_of_batch_set_id)
        REFERENCES opportunity.review_batch_set (workspace_id, batch_set_id),
    CONSTRAINT review_batch_set_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT review_batch_set_prefix_ck CHECK (batch_prefix ~ '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,49}$'),
    CONSTRAINT review_batch_set_size_ck CHECK (max_batch_size BETWEEN 1 AND 10000),
    CONSTRAINT review_batch_set_pass_ck CHECK (
        (review_pass = 1 AND qc_of_batch_set_id IS NULL) OR (review_pass = 2 AND qc_of_batch_set_id IS NOT NULL)),
    CONSTRAINT review_batch_set_group_ck CHECK (length(reviewer_group) BETWEEN 1 AND 256),
    CONSTRAINT review_batch_set_counts_ck CHECK (batch_count >= 0 AND document_count >= 0)
);

SELECT opportunity.enable_workspace_rls('opportunity.review_batch_set');

-- Batch names (<prefix>_0001) are unique in the workspace.
CREATE UNIQUE INDEX review_batch_set_prefix_uq ON opportunity.review_batch_set (workspace_id, lower(batch_prefix));
CREATE INDEX review_batch_set_snapshot_ix ON opportunity.review_batch_set (workspace_id, snapshot_id);
CREATE INDEX review_batch_set_qc_ix ON opportunity.review_batch_set (workspace_id, qc_of_batch_set_id)
    WHERE qc_of_batch_set_id IS NOT NULL;

CREATE TABLE opportunity.review_batch (
    workspace_id    uuid        NOT NULL,
    batch_id        uuid        NOT NULL,
    batch_set_id    uuid        NOT NULL,
    ordinal         integer     NOT NULL,
    name            text        NOT NULL,
    document_count  integer     NOT NULL,
    -- 1 Available, 2 CheckedOut, 3 Completed.
    status          smallint    NOT NULL DEFAULT 1,
    -- The reviewer holding the batch (CheckedOut) or who completed it (Completed).
    assignee_id     uuid        NULL,
    status_changed_at timestamptz NOT NULL DEFAULT now(),
    status_changed_by uuid      NULL,
    version         bigint      NOT NULL DEFAULT 1,
    CONSTRAINT review_batch_pk PRIMARY KEY (workspace_id, batch_id),
    CONSTRAINT review_batch_set_fk FOREIGN KEY (workspace_id, batch_set_id)
        REFERENCES opportunity.review_batch_set (workspace_id, batch_set_id),
    CONSTRAINT review_batch_ordinal_uq UNIQUE (workspace_id, batch_set_id, ordinal),
    -- FK target that pins a member row to its set.
    CONSTRAINT review_batch_member_uq UNIQUE (workspace_id, batch_set_id, batch_id),
    CONSTRAINT review_batch_ordinal_ck CHECK (ordinal >= 1),
    CONSTRAINT review_batch_count_ck CHECK (document_count >= 1),
    CONSTRAINT review_batch_status_ck CHECK (status BETWEEN 1 AND 3 AND (status = 1) = (assignee_id IS NULL)),
    CONSTRAINT review_batch_version_ck CHECK (version >= 1)
) WITH (fillfactor = 80);

SELECT opportunity.enable_workspace_rls('opportunity.review_batch');

CREATE INDEX review_batch_assignee_ix ON opportunity.review_batch (workspace_id, assignee_id, status) WHERE assignee_id IS NOT NULL;
CREATE INDEX review_batch_status_ix ON opportunity.review_batch (workspace_id, batch_set_id, status, ordinal);

-- Frozen membership: a document is in at most one batch of a set; never updated or deleted by the application.
CREATE TABLE opportunity.review_batch_document (
    workspace_id    uuid        NOT NULL,
    batch_set_id    uuid        NOT NULL,
    document_id     uuid        NOT NULL,
    batch_id        uuid        NOT NULL,
    position        integer     NOT NULL,
    CONSTRAINT review_batch_document_pk PRIMARY KEY (workspace_id, batch_set_id, document_id),
    CONSTRAINT review_batch_document_position_uq UNIQUE (workspace_id, batch_id, position),
    CONSTRAINT review_batch_document_batch_fk FOREIGN KEY (workspace_id, batch_set_id, batch_id)
        REFERENCES opportunity.review_batch (workspace_id, batch_set_id, batch_id),
    CONSTRAINT review_batch_document_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT review_batch_document_position_ck CHECK (position >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.review_batch_document');

-- "Which batches hold this document" (conflicts, later: exclude already-batched documents).
CREATE INDEX review_batch_document_document_ix ON opportunity.review_batch_document (workspace_id, document_id);

REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.review_batch_document FROM opportunity_app;

-- Who held a batch when: one row per checkout (self check-out or manager assignment), closed at check-in or
-- reassignment. A reviewer's calls on a batch are their CodingEvents on its documents inside a checkout window.
CREATE TABLE opportunity.review_batch_checkout (
    workspace_id    uuid        NOT NULL,
    batch_id        uuid        NOT NULL,
    checkout_id     uuid        NOT NULL,
    user_id         uuid        NOT NULL,
    checked_out_at  timestamptz NOT NULL,
    checked_out_by  uuid        NOT NULL,
    checked_in_at   timestamptz NULL,
    checked_in_by   uuid        NULL,
    -- 1 Returned, 2 Completed, 3 Reassigned.
    end_kind        smallint    NULL,
    CONSTRAINT review_batch_checkout_pk PRIMARY KEY (workspace_id, batch_id, checkout_id),
    CONSTRAINT review_batch_checkout_batch_fk FOREIGN KEY (workspace_id, batch_id)
        REFERENCES opportunity.review_batch (workspace_id, batch_id),
    CONSTRAINT review_batch_checkout_end_ck CHECK (
        (checked_in_at IS NULL AND checked_in_by IS NULL AND end_kind IS NULL)
        OR (checked_in_at >= checked_out_at AND checked_in_by IS NOT NULL AND end_kind BETWEEN 1 AND 3))
);

SELECT opportunity.enable_workspace_rls('opportunity.review_batch_checkout');

CREATE UNIQUE INDEX review_batch_checkout_open_uq ON opportunity.review_batch_checkout (workspace_id, batch_id)
    WHERE checked_in_at IS NULL;
CREATE INDEX review_batch_checkout_user_ix ON opportunity.review_batch_checkout (workspace_id, user_id, checked_out_at);

REVOKE DELETE, TRUNCATE ON opportunity.review_batch_checkout FROM opportunity_app;

INSERT INTO audit.audit_action (category, action) VALUES
    ('Coding', 'ReviewBatchSet.Created'),
    ('Coding', 'ReviewBatch.CheckedOut'),
    ('Coding', 'ReviewBatch.CheckedIn'),
    ('Coding', 'ReviewBatch.Assigned');

-- Review batches are review records (who was given which documents, checked out when): a preservation lock (legal
-- hold, V0048) freezes them like the other preserved records (ADR-014 §2.3).
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['review_batch_set', 'review_batch', 'review_batch_document', 'review_batch_checkout']
    LOOP
        EXECUTE format(
            'CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.%I '
            || 'REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard()', t);
        EXECUTE format(
            'CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.%I '
            || 'FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard()', t);
    END LOOP;
END
$$;
