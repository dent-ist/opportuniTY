-- V0017: the dispatcher's publish claim on job_chunk (E06-T04).
-- Binding: ADR-001 §6.1 (JobChunk rows are claimed like SearchOutbox rows, through ClaimOwner/ClaimExpiresAt columns;
-- their Status stays Pending/RetryWait while claimed), ADR-010 §2 (the claim is not a chunk status), §6 (per-job and
-- per-workspace concurrency, index backpressure). Expand-only: nullable columns, no backfill.

ALTER TABLE opportunity.job_chunk
    ADD COLUMN claim_owner      text        NULL,
    ADD COLUMN claim_expires_at timestamptz NULL,
    -- Last confirmed publish; a Dispatched chunk no worker claimed for long is published again (lost message).
    ADD COLUMN dispatched_at    timestamptz NULL,
    ADD CONSTRAINT job_chunk_claim_ck CHECK ((claim_owner IS NULL) = (claim_expires_at IS NULL)),
    ADD CONSTRAINT job_chunk_claim_owner_ck CHECK (length(claim_owner) BETWEEN 1 AND 200);

-- Dispatch candidates: the next chunks of a job by sequence.
CREATE INDEX job_chunk_dispatch_ix ON opportunity.job_chunk (workspace_id, job_id, chunk_sequence) WHERE status IN (1, 4);
-- In-flight chunks per job (concurrency limits) and Dispatched chunks to re-dispatch.
CREATE INDEX job_chunk_in_flight_ix ON opportunity.job_chunk (workspace_id, job_id)
    WHERE status IN (2, 3) OR claim_owner IS NOT NULL;
