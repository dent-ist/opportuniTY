-- V0005: users, server-side browser sessions and the Data Protection key ring (E05-T01, ADR-015 D3, D4.1, D10.4).
-- All three are installation-level: a user and their session span workspaces, so there is no workspace_id and no RLS.
-- Workspace access is granted separately (E05-T02); a provisioned user has none.

-- ---------------------------------------------------------------------------------------------------------------
-- Users, keyed by (issuer, subject) (D3.3). Email and display name are display attributes only and never matched.
-- groups is the IdP group snapshot (D3.4) used for role assignment and ethical walls (Q-13).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.app_user (
    user_id                 uuid        NOT NULL,
    issuer                  text        NOT NULL,
    subject                 text        NOT NULL,
    display_name            text        NULL,
    email                   text        NULL,
    groups                  text[]      NOT NULL DEFAULT '{}',
    groups_refreshed_at     timestamptz NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    last_sign_in_at         timestamptz NOT NULL,
    CONSTRAINT app_user_pk PRIMARY KEY (user_id),
    CONSTRAINT app_user_identity_uq UNIQUE (issuer, subject),
    CONSTRAINT app_user_issuer_ck CHECK (length(issuer) BETWEEN 1 AND 2048),
    CONSTRAINT app_user_subject_ck CHECK (length(subject) BETWEEN 1 AND 255)
);

COMMENT ON TABLE opportunity.app_user IS '@global installation-level identity, keyed by OIDC (iss, sub) (ADR-015 D3.3)';

-- ---------------------------------------------------------------------------------------------------------------
-- Browser sessions (D4.1). key_hash is SHA-256 of the 256-bit cookie value; the raw value is never stored.
-- tokens holds the IdP token set encrypted with ASP.NET Core Data Protection. groups/acr/amr are the principal
-- snapshot the API builds the request principal from; groups are refreshed every PrincipalRefreshInterval (D3.5).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.user_session (
    session_id              uuid        NOT NULL,
    key_hash                bytea       NOT NULL,
    user_id                 uuid        NOT NULL,
    issuer                  text        NOT NULL,
    subject                 text        NOT NULL,
    idp_session_id          text        NULL,
    display_name            text        NULL,
    email                   text        NULL,
    groups                  text[]      NOT NULL DEFAULT '{}',
    acr                     text        NULL,
    amr                     text[]      NOT NULL DEFAULT '{}',
    created_at              timestamptz NOT NULL,
    last_seen_at            timestamptz NOT NULL,
    absolute_expires_at     timestamptz NOT NULL,
    principal_refreshed_at  timestamptz NOT NULL,
    tokens                  bytea       NOT NULL,
    revoked_at              timestamptz NULL,
    revoked_reason          text        NULL,
    CONSTRAINT user_session_pk PRIMARY KEY (session_id),
    CONSTRAINT user_session_key_hash_uq UNIQUE (key_hash),
    CONSTRAINT user_session_user_fk FOREIGN KEY (user_id) REFERENCES opportunity.app_user (user_id) ON DELETE CASCADE,
    CONSTRAINT user_session_key_hash_ck CHECK (length(key_hash) = 32),
    CONSTRAINT user_session_expiry_ck CHECK (absolute_expires_at > created_at),
    CONSTRAINT user_session_revoked_ck CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
    CONSTRAINT user_session_revoked_reason_ck CHECK (revoked_reason IS NULL OR revoked_reason IN
        ('SignOut', 'IdleTimeout', 'AbsoluteTimeout', 'BackChannelLogout', 'PrincipalRefreshFailed', 'Replaced'))
);

-- Back-channel logout looks sessions up by IdP session (sid) or by user (sub).
CREATE INDEX user_session_idp_session_ix ON opportunity.user_session (issuer, idp_session_id)
    WHERE revoked_at IS NULL AND idp_session_id IS NOT NULL;
CREATE INDEX user_session_subject_ix ON opportunity.user_session (issuer, subject) WHERE revoked_at IS NULL;
CREATE INDEX user_session_user_ix ON opportunity.user_session (user_id);
CREATE INDEX user_session_cleanup_ix ON opportunity.user_session (absolute_expires_at);

COMMENT ON TABLE opportunity.user_session IS '@global server-side browser sessions (ADR-015 D4.1); not workspace-scoped';

-- ---------------------------------------------------------------------------------------------------------------
-- ASP.NET Core Data Protection key ring shared by all API replicas (D10.4). Encrypting the key XML with a KEK from
-- IKeyProvider is E05-T09 (#54); until then the ring is protected by database access control only.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.data_protection_key (
    friendly_name           text        NOT NULL,
    xml                     text        NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT data_protection_key_pk PRIMARY KEY (friendly_name)
);

COMMENT ON TABLE opportunity.data_protection_key IS '@global ASP.NET Core Data Protection key ring (ADR-015 D10.4)';

-- Keys are never edited or deleted by the application; expired keys stay to read old payloads.
REVOKE UPDATE, DELETE ON opportunity.data_protection_key FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Per-workspace MFA requirement (D3.6). What counts as MFA (acr/amr values) is installation configuration.
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE opportunity.workspace ADD COLUMN require_mfa boolean NOT NULL DEFAULT false;
