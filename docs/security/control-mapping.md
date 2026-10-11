# Control mapping: SOC 2 and ISO/IEC 27001:2022

For organizations that run opportuniTY themselves and are audited against **SOC 2** (Trust Services Criteria 2017,
revised points of focus 2022) or certified to **ISO/IEC 27001:2022** (E14-T06, SEC-13). It maps the controls in scope —
SOC 2 **CC6** (logical and physical access) and **CC7** (system operations), ISO 27001 Annex A **5.15–5.18** (access
control, identity, authentication information, access rights) and **8.15–8.16** (logging, monitoring) — to what the
platform does and what the operator must do.

opportuniTY is self-hosted software, not a service: **the operator is the service organization**. The platform supplies
mechanisms and evidence; the operator owns the policy, the infrastructure, the identity provider, the people and the
reviews. Nothing here is a certification or an auditor's opinion.

**Responsibility** in the tables:

- **Platform** — built into opportuniTY and enforced in code or the database; evidence comes from the product.
- **Operator** — the operator's process or infrastructure; opportuniTY cannot do it.
- **Shared** — the platform provides the mechanism or evidence, the operator must configure, run or review it.

## Evidence the platform produces

| Evidence | Where | Supports |
|---|---|---|
| Permission matrix (roles × permissions, generated from code) | [permission-matrix.md](permission-matrix.md) | CC6.1, CC6.3, A.5.15, A.5.18 |
| Access review: users, groups, roles (direct / via group), walls, last access, last sign-in | `GET …/compliance/access-review[/export]` ([compliance-reports.md](compliance-reports.md)) | CC6.2, CC6.3, A.5.16, A.5.18 |
| Ethical-wall proof: every attempt by a walled user on walled content, with its outcome | `GET …/compliance/wall-proof[/export]` | CC6.1, CC6.3, A.5.15 |
| Audit export with hash chain, signed checkpoints and verification result; offline `audit verify-export` | `GET …/compliance/audit-export`, [audit-chain.md](../operations/audit-chain.md) | CC7.2, CC7.3, A.8.15 |
| `audit verify` over every chain (`Audit.Verified`, `Integrity.ChainBroken`) | worker image CLI | CC7.2, A.8.15 |
| Acknowledgment roster (protective order / reviewer attestation per version) | `GET …/acknowledgment-roster/export` | CC1.4, CC2.2 (context), A.5.18 |
| Destruction certificate of a deleted workspace | [workspace-deletion.md](../operations/workspace-deletion.md) | CC6.5 |
| Alert rules with runbooks, metrics and dashboard | [alerts.md](../operations/alerts.md), [metrics.md](../operations/metrics.md) | CC7.2, CC7.3, A.8.16 |
| Threat model (STRIDE per trust boundary) | [threat-model.md](threat-model.md) | CC3.2 (context), CC7.1 |

Every report generation, every audit query and export, every role, wall and security change and every denied attempt is
itself an audit event, so the evidence trail covers its own production.

## SOC 2 — CC6 Logical and physical access controls

| Criterion | Platform | Operator | Responsibility |
|---|---|---|---|
| **CC6.1** Logical access security software, infrastructure and architectures over protected information assets | Single policy decision point; every API route declares its permission and an architecture test fails the build otherwise; default deny, deny overrides allow; non-members get an identical 404 ([how-permissions-work.md](how-permissions-work.md)). Tenant data only through PostgreSQL row-level security (`WorkspaceTransaction`); search only through the search module with a mandatory workspace filter; document content only through the protected-content gateway. Restriction classes and ethical walls hide documents everywhere, Workspace Admins included. Per-workspace data keys under an installation KEK, crypto-shredding ([keys-and-secrets.md](../operations/keys-and-secrets.md)). Separate database logins per component; per-area RabbitMQ users and optional HMAC envelope signing ([message-trust.md](../operations/message-trust.md)) | Network segmentation, TLS termination, host and container hardening, database and object-store administration accounts, KMS/HSM for the KEK in the Full profile, keeping the deployment on supported versions | Shared |
| **CC6.2** Registration and authorization of new users before credentials are issued; removal when no longer authorized | Identities come only from the operator's OIDC provider; first sign-in grants **no** workspace access; roles are assigned per workspace by a `Workspace.ManageUsers` holder and audited (`Security.RoleAssigned/RoleRevoked`); the last Workspace Admin cannot be removed. Access review lists everyone with access and their last use | Joiner/mover/leaver process in the IdP; approving role requests; disabling leavers in the IdP (a disabled user loses the session within `PrincipalRefreshInterval`, 15 min by default, or immediately with back-channel logout) | Shared |
| **CC6.3** Authorizes, modifies or removes access based on roles, least privilege and segregation of duties | Built-in roles with fixed grants ([permission-matrix.md](permission-matrix.md)); nobody can grant themselves a role or change a wall or class that applies to themselves; break-glass needs an installation admin to assign, MFA and a reason to activate, is read-only and time-boxed (Q-45). Periodic evidence: access review and wall proof | Defining who gets which role; the periodic access review (recommended at least quarterly per matter) and acting on it; segregation between installation admins and workspace admins | Shared |
| **CC6.4** Restricts physical access to facilities and protected information assets | — | Data centre, office and device security for the hosts, backups and key material | Operator |
| **CC6.5** Discontinues logical and physical protections over assets only after the ability to read or recover data has been diminished | Defensible workspace deletion: two-person approval, legal-hold check, resumable purge of PostgreSQL, OpenSearch and object storage, crypto-shredding of the workspace keys, destruction certificate, audit checkpoint before deletion ([workspace-deletion.md](../operations/workspace-deletion.md), [deletion-verification.md](../operations/deletion-verification.md)) | Backup retention and destruction, decommissioning disks and hosts, retention schedules | Shared |
| **CC6.6** Logical access security measures against threats from sources outside system boundaries | OIDC with a BFF session (no tokens in the browser), `__Host-` cookies with anti-forgery token, strict security headers and CSP, MFA step-up per workspace and for administration ([authentication.md](authentication.md)); untrusted documents rendered in a sandbox (Landlock, seccomp, rlimits); natives served only as attachments | Exposing only the web entry point; WAF / rate limiting at the edge if required; IdP MFA policy | Shared |
| **CC6.7** Restricts the transmission, movement and removal of information to authorized users and processes | Exports, productions, native downloads and prints need their own permissions and are audited; exports re-check access per document (Q-15); productions refuse withheld privileged documents; every gateway download is audited before the first byte | TLS on every connection, secure transfer of productions to recipients, endpoint data-loss controls | Shared |
| **CC6.8** Prevents or detects the introduction of unauthorized or malicious software | Rendering sandbox, import hash checks and quarantine of flagged natives, message integrity checks (`Integrity.MessageRejected`); dependency licence and secret scanning in CI | Malware scanning engine and signatures, host anti-malware, verifying release images before deployment | Shared |

## SOC 2 — CC7 System operations

| Criterion | Platform | Operator | Responsibility |
|---|---|---|---|
| **CC7.1** Detection and monitoring procedures to identify configuration changes and new vulnerabilities | Threat model; migrations are versioned SQL; workspace settings changes and key operations are audited (`Workspace.SettingsChanged`, `Admin.Key*`) | Vulnerability scanning of hosts and images, configuration management, patching | Shared |
| **CC7.2** Monitors system components for anomalies indicative of malicious acts, natural disasters and errors | Every denied attempt is audited (`AuthZ.Denied`, `Document.*` with outcome `Denied`); hash-chained audit with signed checkpoints and `audit verify`; Prometheus alert rules with runbooks (pipeline, security projection lag, dead letters, worker heartbeats, API error budget, WAL archiving) | Running the monitoring stack, routing alerts to people, reviewing denied-access patterns and wall proofs, scheduling `audit verify` | Shared |
| **CC7.3** Evaluates security events to determine whether they could or have resulted in a failure to meet objectives | Wall proof states whether walls held; `audit verify` / `verify-export` state whether the trail is intact and name the positions; correlation IDs link a support ticket to its events | Triage and classification of events as incidents | Shared |
| **CC7.4** Responds to identified security incidents | Break-glass is read-only, time-boxed and reported; roles can be revoked immediately (active break-glass activations end with the role); legal holds stop deletion; the audit-chain runbook says how to preserve evidence when verification fails ([audit-chain.md](../operations/audit-chain.md#when-verification-fails)) | Incident response plan, communication, forensic preservation of hosts and backups | Shared |
| **CC7.5** Identifies, develops and implements activities to recover from identified security incidents | Durable work state in PostgreSQL with replay and re-dispatch runbooks; alias reindex rebuilds the search projection from PostgreSQL ([operations README](../operations/README.md)) | Backups and restore tests of PostgreSQL, object storage and key material; disaster recovery | Shared |

## ISO/IEC 27001:2022 Annex A

| Control | Platform | Operator | Responsibility |
|---|---|---|---|
| **5.15 Access control** | Rules enforced by one PDP: workspace roles, document restriction classes, ethical walls, field-level restrictions, break-glass; default deny; identical 404 for non-members; RLS as a safety net; permission matrix generated from code; wall proof shows the walls held | Access control policy: who may be given which role per matter; approving walls and break-glass assignments | Shared |
| **5.16 Identity management** | Identities keyed by IdP issuer and subject (email and name display-only); groups from the IdP drive roles and walls; first sign-in grants nothing; access review lists every identity with access and its last sign-in and last use | Identity lifecycle in the IdP (unique identities, no shared accounts, timely deactivation); reconciling the access review with HR records | Shared |
| **5.17 Authentication information** | No passwords in the platform (OIDC only); server-side sessions with idle (30 min) and absolute (12 h) timeouts; raw session IDs are never stored or logged (audit keeps an HMAC); MFA required per workspace or for administration | Password and MFA policy in the IdP; protecting IdP client secrets and the secret files of the installation ([keys-and-secrets.md](../operations/keys-and-secrets.md)) | Shared |
| **5.18 Access rights** | Role assignments with assigner and time, audited; self-assignment refused; last Workspace Admin protected; access review export for periodic review; acknowledgment roster shows who accepted the protective order; break-glass time-boxed | Provisioning approvals, periodic review and removal of access rights, review after role changes and departures | Shared |
| **8.15 Logging** | Append-only, partitioned audit store (no application role can change or delete events); every event records actor, workspace, object, action, outcome, UTC time, client IP, session hash and correlation ID; per-workspace SHA-256 hash chain with ES256-signed checkpoints; retention purge only under a separate login, refused under legal hold or before sealing; audit export verifiable offline; audit reads and exports are themselves audited; search text readable only with `Audit.ReadSearchText` | Retention period configuration, protecting the database superuser and backups, keeping the checkpoint signing key outside the database host, exporting public keys and checkpoints for the case file, time synchronization (NTP) of all hosts | Shared |
| **8.16 Monitoring activities** | Metrics, alert rules and runbooks; audit of every denied attempt; wall proof and `audit verify` as periodic checks | Operating Prometheus/Grafana (or equivalent), on-call, reviewing alerts, denied attempts and wall proofs on a schedule, feeding logs to a SIEM if required | Shared |

## Suggested operator routine

| When | Do | Evidence kept |
|---|---|---|
| Per matter, at least quarterly | Generate the access review; remove access no longer needed; check walls name the right people | Access review CSV, `Security.RoleRevoked` events |
| Per matter with ethical walls, at least quarterly and at matter close | Generate the wall proof for the period; investigate any `Allowed` row | Wall proof CSV, `Security.WallProofGenerated` events |
| Monthly and before any exhibit | `audit seal --checkpoint`, then `audit verify` with exported public keys | `audit verify` output, `Audit.Verified` events |
| For an exhibit or an external audit | Download the audit export for the period; verify it with `audit verify-export` and the independently exported public key | Export file, public key PEM, verification output |
| Continuously | Route alerts; review `AuthZ.Denied` spikes and `Integrity.*` events | Alert history, incident tickets |
