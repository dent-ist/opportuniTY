-- V0051: confidentiality designations of productions (E12-T04).
-- Binding: Q-08 (a production records the coding it used: each member's designation is frozen at finalization, so
-- the stamp on its pages and the load-file value come from one stored value); Q-11 (Confidentiality Designation is a
-- security-affecting field; AEO visibility comes from the restriction classes bound to its choices, #51); Q-52
-- (listings and reports leave out documents the caller may not view); ADR-013 (Production.* audit); ADR-015 D7 (RLS).
--
-- The specification names the designation field, its levels (lowest first, each with the legend stamped) and the
-- family rule (highest in family, or per document). A person may override one member's designation for a draft with a
-- reason (audited). Finalization computes every member's designation from the coding store under the privilege gate
-- and freezes it here; afterwards a re-designation report compares the frozen value with what the rule gives now.

ALTER TABLE opportunity.production_document
    ADD COLUMN designation_choice_id integer NULL,
    ADD COLUMN designation text NULL,
    -- 0 None, 1 Document, 2 Family, 3 Override (Opportunity.Core.Productions.DesignationSource); NULL until finalized.
    ADD COLUMN designation_source smallint NULL,
    ADD CONSTRAINT production_document_designation_ck CHECK ((designation_source IS NULL) = (designation IS NULL)
        AND (designation_source IS NULL OR designation_source BETWEEN 0 AND 3)
        AND length(designation) <= 100
        AND (designation_source IS DISTINCT FROM 0 OR designation = '' OR designation IS NULL));

-- One override per production member; the reason is required. Kept for the life of the matter like the production.
CREATE TABLE opportunity.production_designation_override (
    workspace_id    uuid        NOT NULL,
    production_id   uuid        NOT NULL,
    document_id     uuid        NOT NULL,
    field_id        integer     NOT NULL,
    -- NULL: produced without a designation.
    choice_id       integer     NULL,
    reason          text        NOT NULL,
    created_by      uuid        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT production_designation_override_pk PRIMARY KEY (workspace_id, production_id, document_id),
    CONSTRAINT production_designation_override_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT production_designation_override_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT production_designation_override_choice_fk FOREIGN KEY (workspace_id, field_id, choice_id)
        REFERENCES opportunity.choice (workspace_id, field_id, choice_id),
    CONSTRAINT production_designation_override_reason_ck CHECK (length(btrim(reason)) BETWEEN 1 AND 2000)
);

SELECT opportunity.enable_workspace_rls('opportunity.production_designation_override');
REVOKE TRUNCATE ON opportunity.production_designation_override FROM opportunity_app;

-- Overrides are part of the production: they change only while it is a draft.
CREATE FUNCTION opportunity.production_designation_override_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
DECLARE
    v_status smallint;
    v_row opportunity.production_designation_override;
BEGIN
    v_row := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    SELECT p.status INTO v_status
      FROM opportunity.production p
     WHERE p.workspace_id = v_row.workspace_id AND p.production_id = v_row.production_id;
    IF v_status IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'The designation overrides of production % are frozen', v_row.production_id
            USING ERRCODE = 'integrity_constraint_violation',
                  HINT = 'E12-T04: a finalized production is frozen; create a new production version instead.';
    END IF;
    RETURN v_row;
END
$$;

CREATE TRIGGER production_designation_override_guard
    BEFORE INSERT OR UPDATE OR DELETE ON opportunity.production_designation_override
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.production_designation_override_guard();

INSERT INTO audit.audit_action (category, action) VALUES
    ('Production', 'DesignationOverridden'),
    ('Production', 'DesignationOverrideRemoved'),
    ('Production', 'DesignationsFrozen'),
    ('Production', 'RedesignationExported');
