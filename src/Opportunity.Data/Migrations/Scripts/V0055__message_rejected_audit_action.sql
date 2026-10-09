-- V0055: message and worker trust (E05-T07, #52; ADR-015 D9.3/D9.5). A message a worker refuses — its envelope
-- disagrees with PostgreSQL (a forged or stale WorkspaceId, job or idempotency key) or, with envelope signing on, its
-- HMAC signature is missing, invalid or under an unknown key — is dead-lettered and audited as
-- Integrity.MessageRejected with the reason code. Integrity.EnvelopeMismatch stays in the closed list for events written
-- before this migration.

INSERT INTO audit.audit_action (category, action) VALUES
    ('Integrity', 'MessageRejected');
