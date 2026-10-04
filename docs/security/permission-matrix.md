# Permission matrix

<!-- Generated from Opportunity.Core.Security (PermissionCatalog, RoleCatalog, RestrictionClasses). Do not edit by hand:
     run the unit tests with OPPORTUNITY_UPDATE_GOLDEN=1 to regenerate. -->

The closed permission set and built-in workspace roles of the policy decision point (ADR-015 D5). Roles are
assigned per workspace to users or IdP groups; a principal holds the union of its roles' permissions. Default
deny; deny overrides allow. Restriction classes and ethical walls then hide documents (404) regardless of
role, Workspace Admin included; only an active break-glass activation lifts them, and only for the
permissions in the Break-glass column (Q-45).

## Roles × permissions

| Permission | Workspace Admin | Reviewer | QC Reviewer | Privilege Reviewer | Production Manager | Auditor (read-only) | Break-glass |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| `Document.View` | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `Document.DownloadNative` | ✔ | | | | ✔ | | |
| `Document.Print` | ✔ | | | | ✔ | | |
| `Document.ViewQuarantined` | ✔ | | | | | | |
| `Search.Execute` | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `SavedSearch.Share` | ✔ | | ✔ | ✔ | ✔ | | |
| `Coding.Write` | ✔ | ✔ | ✔ | ✔ | | | |
| `Coding.WritePrivilege` | ✔ | | | ✔ | | | |
| `Coding.Bulk` | ✔ | | ✔ | ✔ | ✔ | | |
| `Redaction.Apply` | ✔ | ✔ | ✔ | ✔ | | | |
| `Redaction.Remove` | ✔ | | ✔ | ✔ | | | |
| `Import.Run` | ✔ | | | | | | |
| `Import.Overlay` | ✔ | | | | | | |
| `Export.Create` | ✔ | | | | ✔ | | |
| `Export.Download` | ✔ | | | | ✔ | | |
| `Production.Create` | ✔ | | | | ✔ | | |
| `Production.Finalize` | ✔ | | | | ✔ | | |
| `PrivilegeLog.Generate` | ✔ | | | ✔ | ✔ | | |
| `Job.ViewAll` | ✔ | | | | ✔ | ✔ | |
| `Job.Manage` | ✔ | | | | | | |
| `Job.Replay` | ✔ | | | | | | |
| `Audit.Read` | ✔ | | | | | ✔ | ✔ |
| `Audit.ReadSearchText` | ✔ | | | | | ✔ | |
| `Workspace.ManageUsers` | ✔ | | | | | | |
| `Workspace.ManageSecurity` | ✔ | | | | | | |
| `Workspace.ManageFields` | ✔ | | | | | | |
| `Workspace.RequestDeletion` | ✔ | | | | | | |

Break-glass is not membership on its own: it counts only while the holder has an active activation in the
workspace (reason and MFA required, default 60 minutes, at most 4 hours). Decisions that rely on it are
flagged, so the audit event of the action carries access path `BreakGlass`.

## Permissions

| Permission | Allows |
|---|---|
| `Document.View` | Open a document: metadata, extracted text and renditions. |
| `Document.DownloadNative` | Download the native file. |
| `Document.Print` | Print or save rendered pages. |
| `Document.ViewQuarantined` | See that a native is quarantined and its scan result; never renders it. |
| `Search.Execute` | Run searches and see result pages (post-filtered, Q-12). |
| `SavedSearch.Share` | Share saved searches with other workspace users. |
| `Coding.Write` | Change coding fields that are not security-affecting. |
| `Coding.WritePrivilege` | Change security-affecting fields: privilege, confidentiality, wall membership. |
| `Coding.Bulk` | Submit bulk coding jobs. |
| `Redaction.Apply` | Add or modify redactions. |
| `Redaction.Remove` | Remove redactions. |
| `Import.Run` | Run imports of new documents. |
| `Import.Overlay` | Run overlay imports, including coding and privilege overlays (Q-31). |
| `Export.Create` | Create exports. |
| `Export.Download` | Download export packages. |
| `Production.Create` | Create and run productions. |
| `Production.Finalize` | Finalize productions. |
| `PrivilegeLog.Generate` | Generate privilege logs. |
| `Job.ViewAll` | See every user's jobs (users always see their own). |
| `Job.Manage` | Cancel, pause and resume other users' jobs (users may cancel their own). |
| `Job.Replay` | Replay failed job chunks, index tasks and search outbox rows from PostgreSQL (ADR-010 §7.4). |
| `Audit.Read` | Read the workspace audit trail. |
| `Audit.ReadSearchText` | Read search query text in the audit trail (Q-16). |
| `Workspace.ManageUsers` | Assign and revoke workspace roles. |
| `Workspace.ManageSecurity` | Manage restriction classes, class grants and ethical walls. |
| `Workspace.ManageFields` | Manage fields, choices and coding layouts. |
| `Workspace.RequestDeletion` | Request workspace deletion (Q-23). |

## Role keys

| Role | Key (`workspace_role_assignment.role`) |
|---|---|
| Workspace Admin | `WorkspaceAdmin` |
| Reviewer | `Reviewer` |
| QC Reviewer | `QcReviewer` |
| Privilege Reviewer | `PrivilegeReviewer` |
| Production Manager | `ProductionManager` |
| Auditor (read-only) | `Auditor` |
| Break-glass | `BreakGlass` |

## Default restriction-class grants (Q-11)

Seeded for every workspace; workspace admins may tighten them. A document carrying a class is visible only
to principals holding a role granted that class.

| Class | Workspace Admin | Reviewer | QC Reviewer | Privilege Reviewer | Production Manager | Auditor (read-only) |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| `Privileged` | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `Confidential` | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| `AttorneysEyesOnly` | ✔ | | | ✔ | ✔ | |
