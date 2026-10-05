-- V0030: the refresh-aware ("visible") search watermark per workspace (E07-T08, ADR-001 §7.3/§7.4, baseline §28).
-- indexed_through_generation N means: every search work record (SearchOutbox row, IndexChunkTask) with a
-- SearchGeneration <= N was applied to OpenSearch AND a refresh that started after it was applied succeeded on every
-- shard copy, so a reader opened afterwards sees it. Only the watermark ticker (dispatcher host) raises it, with
-- GREATEST, so it never moves backwards; it is never set from a write acknowledgement alone. A separate row from
-- workspace_search_generation keeps the ticker off the counter row, whose lock work-creating transactions hold until
-- they commit (ADR-001 §7.1). Reindex tasks carry no generation and never move it; it continues unchanged across an
-- alias switch (§7.5). A missing row reads as 0.

CREATE TABLE opportunity.workspace_search_watermark (
    workspace_id                uuid        NOT NULL,
    indexed_through_generation  bigint      NOT NULL,
    -- Database time the refresh that last raised the watermark was observed.
    refreshed_at                timestamptz NOT NULL,
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workspace_search_watermark_pk PRIMARY KEY (workspace_id),
    CONSTRAINT workspace_search_watermark_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_search_watermark_generation_ck CHECK (indexed_through_generation >= 0)
);

SELECT opportunity.enable_workspace_rls('opportunity.workspace_search_watermark');
REVOKE DELETE, TRUNCATE ON opportunity.workspace_search_watermark FROM opportunity_app;

-- Lag (ADR-001 §7.4): CommittedAt of the oldest work record above the visible watermark, whatever its status (an
-- applied but not yet refreshed record is still unreflected). The existing partial indexes cover non-applied rows only.
-- Plain CREATE INDEX: CONCURRENTLY is not available on partitioned parents, and both tables hold days of work only.
CREATE INDEX search_outbox_generation_ix ON opportunity.search_outbox (workspace_id, search_generation);
CREATE INDEX index_chunk_task_generation_ix ON opportunity.index_chunk_task (workspace_id, search_generation)
    WHERE search_generation IS NOT NULL;
