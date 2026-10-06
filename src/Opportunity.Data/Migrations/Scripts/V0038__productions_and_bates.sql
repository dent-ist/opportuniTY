-- V0038: productions with frozen, versioned specifications (E12-T02) and Bates allocation with integrity guarantees
-- (E12-T03).
-- Binding: Q-08 (productions are reproducible: frozen specification, membership and Bates assignment, recorded in a
-- manifest with software versions); Q-54 (Bates numbers of a never-produced draft may be reused; once produced, never
-- reissued; both audited); ADR-002 §9 (a production is a materialized snapshot plus frozen production state; the
-- snapshot is kept for the life of the matter through the job's target_snapshot_id); ADR-010 §4/§6 (allocation runs as a
-- Production job of SnapshotRange chunks over the production order, fence F3 per chunk); ADR-013 (Production.* audit);
-- ADR-015 D7 (forced RLS).
--
-- Lifecycle: Draft (specification and frozen set editable) → Finalized (produced: frozen forever) → Voided (withdrawn;
-- its numbers stay retired). A Draft may be Discarded (its reserved numbers are released, Q-54). Bates numbers are
-- allocated for a Draft by a job: the planning transaction orders the members (families adjacent), computes how many
-- numbers each consumes and reserves the production's range in bates_range under a per-(workspace, prefix) advisory
-- lock after checking it overlaps no other live range; chunks then write each document's numbers.

CREATE TABLE opportunity.production (
    workspace_id            uuid        NOT NULL,
    production_id           uuid        NOT NULL,
    -- Versions of one production share the lineage (the first version's id); a finalized version never changes.
    lineage_id              uuid        NOT NULL,
    version                 integer     NOT NULL DEFAULT 1,
    name                    text        NOT NULL,
    snapshot_id             uuid        NOT NULL,
    -- Canonical specification JSON, kept as text so its SHA-256 is stable byte for byte.
    specification           text        NOT NULL,
    specification_sha256    bytea       NOT NULL,
    bates_prefix            text        NOT NULL,
    -- Uniqueness key: the prefix compared without case.
    bates_prefix_key        text        NOT NULL,
    bates_suffix            text        NOT NULL DEFAULT '',
    bates_padding           smallint    NOT NULL,
    bates_start             bigint      NOT NULL,
    -- 1 Draft, 2 Finalized, 3 Voided, 4 Discarded.
    status                  smallint    NOT NULL DEFAULT 1,
    row_version             bigint      NOT NULL DEFAULT 1,
    -- Bates allocation: 0 None, 1 Allocating, 2 Allocated, 3 Failed.
    bates_state             smallint    NOT NULL DEFAULT 0,
    bates_job_id            uuid        NULL,
    bates_reason            text        NULL,
    -- Set by the planning transaction (the reserved range) and the completed allocation.
    bates_first             bigint      NULL,
    bates_last              bigint      NULL,
    bates_documents         bigint      NULL,
    bates_units             bigint      NULL,
    assignments_sha256      bytea       NULL,
    integrity               jsonb       NULL,
    -- Canonical manifest JSON written by the finalization.
    manifest                text        NULL,
    manifest_sha256         bytea       NULL,
    -- Claim of the worker that plans or completes the allocation.
    claimed_by              text        NULL,
    claimed_until           timestamptz NULL,
    created_by              uuid        NOT NULL,
    created_by_display      text        NOT NULL,
    created_by_groups       text[]      NOT NULL DEFAULT '{}',
    created_at              timestamptz NOT NULL DEFAULT now(),
    modified_at             timestamptz NOT NULL DEFAULT now(),
    finalized_at            timestamptz NULL,
    finalized_by            uuid        NULL,
    voided_at               timestamptz NULL,
    voided_by               uuid        NULL,
    void_reason             text        NULL,
    discarded_at            timestamptz NULL,
    CONSTRAINT production_pk PRIMARY KEY (workspace_id, production_id),
    CONSTRAINT production_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT production_lineage_fk FOREIGN KEY (workspace_id, lineage_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT production_bates_job_fk FOREIGN KEY (workspace_id, bates_job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT production_lineage_version_uq UNIQUE (workspace_id, lineage_id, version),
    CONSTRAINT production_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT production_spec_ck CHECK (length(specification) BETWEEN 2 AND 262144 AND octet_length(specification_sha256) = 32),
    CONSTRAINT production_bates_format_ck CHECK (length(bates_prefix) BETWEEN 1 AND 30 AND bates_prefix_key = upper(bates_prefix)
        AND length(bates_suffix) <= 30 AND bates_padding BETWEEN 1 AND 12 AND bates_start >= 1),
    CONSTRAINT production_version_ck CHECK (version >= 1 AND (version = 1) = (lineage_id = production_id)),
    CONSTRAINT production_status_ck CHECK (status BETWEEN 1 AND 4 AND bates_state BETWEEN 0 AND 3 AND row_version >= 1),
    CONSTRAINT production_bates_ck CHECK ((bates_state = 0) = (bates_job_id IS NULL) AND length(bates_reason) <= 2000
        AND (bates_first IS NULL) = (bates_last IS NULL) AND (bates_first IS NULL OR bates_last >= bates_first - 1)
        AND (assignments_sha256 IS NULL OR octet_length(assignments_sha256) = 32)
        AND (integrity IS NULL OR jsonb_typeof(integrity) = 'object')),
    CONSTRAINT production_finalized_ck CHECK ((status IN (2, 3)) = (finalized_at IS NOT NULL AND finalized_by IS NOT NULL
        AND manifest IS NOT NULL AND coalesce(octet_length(manifest_sha256), 0) = 32)
        AND (status <> 2 OR bates_state = 2)),
    CONSTRAINT production_voided_ck CHECK ((status = 3) = (voided_at IS NOT NULL AND voided_by IS NOT NULL
        AND length(btrim(void_reason)) BETWEEN 1 AND 2000)),
    CONSTRAINT production_discarded_ck CHECK ((status = 4) = (discarded_at IS NOT NULL)),
    CONSTRAINT production_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200),
    CONSTRAINT production_created_by_display_ck CHECK (length(created_by_display) BETWEEN 1 AND 512)
);

CREATE INDEX production_created_ix ON opportunity.production (workspace_id, created_at DESC, production_id);
CREATE INDEX production_allocating_ix ON opportunity.production (workspace_id, created_at) WHERE bates_state = 1;
CREATE INDEX production_bates_job_ix ON opportunity.production (workspace_id, bates_job_id) WHERE bates_job_id IS NOT NULL;

SELECT opportunity.enable_workspace_rls('opportunity.production');
-- Productions are records of the matter (Q-23): never deleted by the application.
REVOKE DELETE, TRUNCATE ON opportunity.production FROM opportunity_app;

-- E12-T02: once finalized, the specification, membership, Bates numbers and manifest never change. The only change a
-- finalized production accepts is being voided; voided and discarded productions accept none. Drafts change freely.
CREATE FUNCTION opportunity.production_frozen_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.status = 1 THEN
        RETURN NEW;
    END IF;
    IF OLD.status = 2 AND NEW.status = 3
       AND (NEW.name, NEW.snapshot_id, NEW.specification, NEW.specification_sha256, NEW.bates_prefix, NEW.bates_suffix,
            NEW.bates_padding, NEW.bates_start, NEW.bates_state, NEW.bates_first, NEW.bates_last, NEW.bates_documents,
            NEW.bates_units, NEW.assignments_sha256, NEW.manifest, NEW.manifest_sha256, NEW.finalized_at, NEW.finalized_by,
            NEW.lineage_id, NEW.version)
           IS NOT DISTINCT FROM
           (OLD.name, OLD.snapshot_id, OLD.specification, OLD.specification_sha256, OLD.bates_prefix, OLD.bates_suffix,
            OLD.bates_padding, OLD.bates_start, OLD.bates_state, OLD.bates_first, OLD.bates_last, OLD.bates_documents,
            OLD.bates_units, OLD.assignments_sha256, OLD.manifest, OLD.manifest_sha256, OLD.finalized_at, OLD.finalized_by,
            OLD.lineage_id, OLD.version) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Production % is % and cannot be changed', OLD.production_id,
        CASE OLD.status WHEN 2 THEN 'finalized' WHEN 3 THEN 'voided' ELSE 'discarded' END
        USING ERRCODE = 'integrity_constraint_violation',
              HINT = 'E12-T02: a finalized production is frozen; create a new production version instead.';
END
$$;

CREATE TRIGGER production_frozen_guard
    BEFORE UPDATE ON opportunity.production
    FOR EACH ROW
    WHEN (OLD.status <> 1)
    EXECUTE FUNCTION opportunity.production_frozen_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- One row per production member in production order (sequence 1…N): snapshot order with each family moved to its
-- first member's position (family_sequence within). units/first_offset/chunk_sequence come from the planner; the chunk
-- writes the numbers and the ProdBeg/End/Attach labels. The row is the Bates → DocumentId cross-reference for the life
-- of the matter (rows of discarded drafts stay, but their numbers are no longer issued).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.production_document (
    workspace_id        uuid        NOT NULL,
    production_id       uuid        NOT NULL,
    sequence            bigint      NOT NULL,
    document_id         uuid        NOT NULL,
    snapshot_ordinal    bigint      NOT NULL,
    family_key          uuid        NOT NULL,
    family_sequence     integer     NOT NULL,
    -- Document version at the freeze (the snapshot's baseline version, ADR-002 §9 DocumentVersionAtFreeze).
    document_version    bigint      NOT NULL,
    page_count          integer     NOT NULL,
    file_extension      text        NULL,
    -- 1 Image, 2 Native, 3 Placeholder (Opportunity.Core.Productions.ProductionOutputKind).
    output              smallint    NOT NULL,
    units               integer     NOT NULL,
    first_offset        bigint      NOT NULL,
    chunk_sequence      integer     NOT NULL,
    beg_number          bigint      NULL,
    end_number          bigint      NULL,
    prod_beg_bates      text        NULL,
    prod_end_bates      text        NULL,
    prod_beg_attach     text        NULL,
    prod_end_attach     text        NULL,
    CONSTRAINT production_document_pk PRIMARY KEY (workspace_id, production_id, sequence),
    CONSTRAINT production_document_document_uq UNIQUE (workspace_id, production_id, document_id),
    CONSTRAINT production_document_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT production_document_ck CHECK (sequence >= 1 AND snapshot_ordinal >= 1 AND page_count >= 0 AND output BETWEEN 1 AND 3
        AND units >= 1 AND first_offset >= 0 AND chunk_sequence >= 1 AND length(file_extension) <= 100
        AND (beg_number IS NULL) = (end_number IS NULL) AND (beg_number IS NULL OR end_number = beg_number + units - 1)
        AND (beg_number IS NULL) = (prod_beg_bates IS NULL)
        AND (prod_beg_bates IS NULL) = (prod_end_bates IS NULL)
        AND (prod_beg_bates IS NULL) = (prod_beg_attach IS NULL)
        AND (prod_beg_bates IS NULL) = (prod_end_attach IS NULL))
);

-- Bates → document (lookup by number within a production whose range holds it) and document → productions.
CREATE INDEX production_document_bates_ix
    ON opportunity.production_document (workspace_id, production_id, beg_number) WHERE beg_number IS NOT NULL;
CREATE INDEX production_document_document_ix ON opportunity.production_document (workspace_id, document_id);
CREATE INDEX production_document_family_ix ON opportunity.production_document (workspace_id, production_id, family_key);

SELECT opportunity.enable_workspace_rls('opportunity.production_document');
REVOKE TRUNCATE ON opportunity.production_document FROM opportunity_app;

-- Membership and numbers of a production are frozen with it: rows change only while it is a draft.
CREATE FUNCTION opportunity.production_document_frozen_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
DECLARE
    v_status smallint;
BEGIN
    SELECT p.status INTO v_status
      FROM opportunity.production p
     WHERE p.workspace_id = OLD.workspace_id AND p.production_id = OLD.production_id;
    IF v_status IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'The members of production % are frozen', OLD.production_id
            USING ERRCODE = 'integrity_constraint_violation',
                  HINT = 'E12-T02: a finalized production is frozen; create a new production version instead.';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$;

CREATE TRIGGER production_document_frozen_guard
    BEFORE UPDATE OR DELETE ON opportunity.production_document
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.production_document_frozen_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- The Bates ledger: every range ever reserved, per workspace and prefix key. A range is Reserved by a draft's
-- allocation, Produced when the draft is finalized, Voided with its production, or Released when a never-produced draft
-- is discarded or re-allocated (Q-54: only then may its numbers be reused). Live ranges (Reserved, Produced, Voided) of
-- one prefix never overlap: writers take pg_advisory_xact_lock on (workspace, prefix key) before checking and
-- reserving, and the integrity check verifies it after every allocation.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.bates_range (
    workspace_id        uuid        NOT NULL,
    range_id            uuid        NOT NULL,
    production_id       uuid        NOT NULL,
    bates_prefix_key    text        NOT NULL,
    first_number        bigint      NOT NULL,
    last_number         bigint      NOT NULL,
    -- 1 Reserved, 2 Produced, 3 Voided, 4 Released.
    state               smallint    NOT NULL DEFAULT 1,
    reserved_at         timestamptz NOT NULL DEFAULT now(),
    state_changed_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT bates_range_pk PRIMARY KEY (workspace_id, range_id),
    CONSTRAINT bates_range_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT bates_range_ck CHECK (first_number >= 1 AND last_number >= first_number AND state BETWEEN 1 AND 4
        AND length(bates_prefix_key) BETWEEN 1 AND 30)
);

CREATE INDEX bates_range_prefix_ix ON opportunity.bates_range (workspace_id, bates_prefix_key, first_number) WHERE state <> 4;
CREATE INDEX bates_range_production_ix ON opportunity.bates_range (workspace_id, production_id);
-- One live range per production.
CREATE UNIQUE INDEX bates_range_live_uq ON opportunity.bates_range (workspace_id, production_id) WHERE state <> 4;

SELECT opportunity.enable_workspace_rls('opportunity.bates_range');
REVOKE DELETE, TRUNCATE ON opportunity.bates_range FROM opportunity_app;

-- Q-54: Reserved → Produced or Released; Produced → Voided; nothing else, and numbers never move.
CREATE FUNCTION opportunity.bates_range_transition_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF (NEW.workspace_id, NEW.range_id, NEW.production_id, NEW.bates_prefix_key, NEW.first_number, NEW.last_number)
           IS DISTINCT FROM (OLD.workspace_id, OLD.range_id, OLD.production_id, OLD.bates_prefix_key, OLD.first_number, OLD.last_number)
       OR NOT ((OLD.state = 1 AND NEW.state IN (1, 2, 4)) OR (OLD.state = 2 AND NEW.state IN (2, 3)) OR (OLD.state = NEW.state)) THEN
        RAISE EXCEPTION 'Bates range % cannot change from state % to %', OLD.range_id, OLD.state, NEW.state
            USING ERRCODE = 'integrity_constraint_violation',
                  HINT = 'Q-54: produced Bates numbers are never released or reissued.';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER bates_range_transition_guard
    BEFORE UPDATE ON opportunity.bates_range
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.bates_range_transition_guard();

INSERT INTO audit.audit_action (category, action) VALUES
    ('Production', 'Modified'),
    ('Production', 'Discarded'),
    ('Production', 'BatesAllocated'),
    ('Production', 'Verified');
