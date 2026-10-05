-- V0031: dead-letter records (ADR-010 §7.3, deferred by #61 in Q-72). The dispatcher's dead-letter recorder copies
-- every message that reaches a dead-letter queue (or an area's parking queue) into PostgreSQL: message id, queue,
-- routing key, AMQP and envelope headers, death reason and count, the transport's error, and the body cut to a size
-- limit. A message whose envelope names an existing workspace is a workspace row (RLS); any other is installation-level
-- (dead_letter_installation). Both are idempotent by message id and kept 30 days (DeadLetterRecorder:Retention).
-- subject_id is the work row the payload names (job chunk or index task): the job failures listing shows a workspace
-- record while that row is Failed (or when the message names none), so a replay clears it there too.
-- Diagnostics only: nothing is re-published from here (replay resets PostgreSQL state, ADR-010 §7.4).
-- Bodies and headers can carry search text or identifiers: opportunity_readonly gets no access (Q-16).

CREATE TABLE opportunity.dead_letter (
    workspace_id        uuid        NOT NULL,
    message_id          text        NOT NULL,
    queue               text        NOT NULL,
    exchange            text        NOT NULL,
    routing_key         text        NOT NULL,
    job_id              uuid        NULL,
    subject_id          uuid        NULL,
    message_type        text        NULL,
    correlation_id      text        NULL,
    death_reason        text        NOT NULL,
    death_count         integer     NOT NULL,
    error_type          text        NULL,
    error               text        NULL,
    first_death_at      timestamptz NULL,
    headers             jsonb       NOT NULL,
    body                bytea       NOT NULL,
    body_size           integer     NOT NULL,
    recorded_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT dead_letter_pk PRIMARY KEY (workspace_id, message_id),
    CONSTRAINT dead_letter_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT dead_letter_message_id_ck CHECK (length(message_id) BETWEEN 1 AND 64),
    CONSTRAINT dead_letter_names_ck CHECK (length(queue) <= 255 AND length(exchange) <= 255 AND length(routing_key) <= 255
        AND length(death_reason) BETWEEN 1 AND 100 AND coalesce(length(message_type), 0) <= 255
        AND coalesce(length(correlation_id), 0) <= 255 AND coalesce(length(error_type), 0) <= 500),
    CONSTRAINT dead_letter_error_ck CHECK (coalesce(length(error), 0) <= 4000),
    CONSTRAINT dead_letter_count_ck CHECK (death_count >= 1),
    CONSTRAINT dead_letter_body_ck CHECK (length(body) <= 65536 AND body_size >= length(body)),
    CONSTRAINT dead_letter_headers_ck CHECK (jsonb_typeof(headers) = 'object' AND pg_column_size(headers) <= 65536)
);

SELECT opportunity.enable_workspace_rls('opportunity.dead_letter');

CREATE INDEX dead_letter_job_ix ON opportunity.dead_letter (workspace_id, job_id, recorded_at) WHERE job_id IS NOT NULL;
CREATE INDEX dead_letter_recorded_ix ON opportunity.dead_letter (workspace_id, recorded_at);

CREATE TABLE opportunity.dead_letter_installation (
    message_id              text        NOT NULL,
    claimed_workspace_id    uuid        NULL,
    queue                   text        NOT NULL,
    exchange                text        NOT NULL,
    routing_key             text        NOT NULL,
    job_id                  uuid        NULL,
    subject_id              uuid        NULL,
    message_type            text        NULL,
    correlation_id          text        NULL,
    death_reason            text        NOT NULL,
    death_count             integer     NOT NULL,
    error_type              text        NULL,
    error                   text        NULL,
    first_death_at          timestamptz NULL,
    headers                 jsonb       NOT NULL,
    body                    bytea       NOT NULL,
    body_size               integer     NOT NULL,
    recorded_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT dead_letter_installation_pk PRIMARY KEY (message_id),
    CONSTRAINT dead_letter_installation_message_id_ck CHECK (length(message_id) BETWEEN 1 AND 64),
    CONSTRAINT dead_letter_installation_names_ck CHECK (length(queue) <= 255 AND length(exchange) <= 255
        AND length(routing_key) <= 255 AND length(death_reason) BETWEEN 1 AND 100
        AND coalesce(length(message_type), 0) <= 255 AND coalesce(length(correlation_id), 0) <= 255
        AND coalesce(length(error_type), 0) <= 500),
    CONSTRAINT dead_letter_installation_error_ck CHECK (coalesce(length(error), 0) <= 4000),
    CONSTRAINT dead_letter_installation_count_ck CHECK (death_count >= 1),
    CONSTRAINT dead_letter_installation_body_ck CHECK (length(body) <= 65536 AND body_size >= length(body)),
    CONSTRAINT dead_letter_installation_headers_ck CHECK (jsonb_typeof(headers) = 'object' AND pg_column_size(headers) <= 65536)
);

COMMENT ON TABLE opportunity.dead_letter_installation IS
    '@global dead-lettered messages that name no existing workspace (ADR-010 §7.3); diagnostics only';

CREATE INDEX dead_letter_installation_recorded_ix ON opportunity.dead_letter_installation (recorded_at);

REVOKE ALL ON opportunity.dead_letter FROM opportunity_readonly;
REVOKE ALL ON opportunity.dead_letter_installation FROM opportunity_readonly;
