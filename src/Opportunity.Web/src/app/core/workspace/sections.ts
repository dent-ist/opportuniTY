/**
 * Workspace navigation (familiarity guide §2.1–§2.3). Order is fixed; a section shows only when the API
 * grants its permission in the workspace (RBAC-driven navigation, baseline §15). Search is part of
 * Documents, so there is no separate Search section. Review Batches stay hidden until M5.
 *
 * Names are the closed E05-T02 catalogue (docs/security/permission-matrix.md); `GET /api/v1/workspaces/{id}`
 * returns the caller's effective subset. Navigation is an affordance only: the API authorizes every call.
 */
export const PERMISSIONS = {
  documentView: 'Document.View',
  searchExecute: 'Search.Execute',
  searchTermReportRun: 'SearchTermReport.Run',
  codingWrite: 'Coding.Write',
  /** Security-affecting fields: privilege, confidentiality, wall membership (Q-11). */
  codingWritePrivilege: 'Coding.WritePrivilege',
  codingBulk: 'Coding.Bulk',
  /** Privilege logs and the privilege conflicts report (E13-T02). */
  privilegeLogGenerate: 'PrivilegeLog.Generate',
  downloadNative: 'Document.DownloadNative',
  productionCreate: 'Production.Create',
  importRun: 'Import.Run',
  importOverlay: 'Import.Overlay',
  exportCreate: 'Export.Create',
  manageFields: 'Workspace.ManageFields',
  manageUsers: 'Workspace.ManageUsers',
  manageSecurity: 'Workspace.ManageSecurity',
  /** Place and release legal holds (preservation locks, E20-T01). */
  manageHolds: 'Workspace.ManageHolds',
  /** Request the deletion of the workspace (E20-T02, Q-23); a Retention Approver approves it. */
  requestDeletion: 'Workspace.RequestDeletion',
  auditRead: 'Audit.Read',
  /** Create, change and delete shared document-list views (E16-T09, "manage views"). */
  manageSharedViews: 'View.ManageShared',
  manageHighlightSets: 'HighlightSet.Manage',
  /** Add redactions and change one's own (E11-T04, ADR-012 §3.8). */
  redactionApply: 'Redaction.Apply',
  /** Remove redactions and change other users' redactions. */
  redactionRemove: 'Redaction.Remove',
} as const;

/** Installation-level permissions (`GET /api/v1/me` → `installationPermissions`), not tied to a workspace. */
export const INSTALLATION_PERMISSIONS = {
  manageWorkspaces: 'Installation.ManageWorkspaces',
  /** The Retention Approver role: approve workspace deletions (E20-T02, ADR-014 §3.2). */
  approveDeletion: 'Installation.ApproveDeletion',
} as const;

export interface WorkspaceSection {
  /** Route path under `/w/:workspaceId`. */
  readonly path: string;
  readonly label: string;
  readonly permission: string;
}

export const WORKSPACE_SECTIONS: readonly WorkspaceSection[] = [
  { path: 'documents', label: 'Documents', permission: PERMISSIONS.documentView },
  // Saved searches: create, organise, share and report on them (Search Terms Reports is one of its tabs).
  { path: 'searches', label: 'Searches', permission: PERMISSIONS.searchExecute },
  { path: 'productions', label: 'Productions', permission: PERMISSIONS.productionCreate },
  { path: 'imports', label: 'Imports', permission: PERMISSIONS.importRun },
  { path: 'exports', label: 'Exports', permission: PERMISSIONS.exportCreate },
  // Every member sees their own jobs (Job.ViewAll widens the list), so any role with Document.View qualifies.
  { path: 'jobs', label: 'Jobs', permission: PERMISSIONS.documentView },
];

/** Tabs of the Searches section, in order; paths are under `/w/:workspaceId/searches/`. */
export const SEARCH_AREAS: readonly WorkspaceSection[] = [
  { path: 'saved', label: 'Saved Searches', permission: PERMISSIONS.searchExecute },
  {
    path: 'terms-reports',
    label: 'Search Terms Reports',
    permission: PERMISSIONS.searchTermReportRun,
  },
  // Families and duplicates with inconsistent privilege calls (E13-T02): a QC report like Search Terms Reports.
  {
    path: 'privilege-conflicts',
    label: 'Privilege Conflicts',
    permission: PERMISSIONS.privilegeLogGenerate,
  },
];

/** Entries of the Admin ▾ menu, in guide order; paths are under `/w/:workspaceId/admin/`. */
export const ADMIN_AREAS: readonly WorkspaceSection[] = [
  { path: 'fields', label: 'Fields', permission: PERMISSIONS.manageFields },
  { path: 'choices', label: 'Choices', permission: PERMISSIONS.manageFields },
  { path: 'coding-layouts', label: 'Coding Layouts', permission: PERMISSIONS.manageFields },
  { path: 'views', label: 'Views', permission: PERMISSIONS.manageFields },
  { path: 'highlight-sets', label: 'Highlight Sets', permission: PERMISSIONS.manageHighlightSets },
  { path: 'redaction-sets', label: 'Redaction Sets', permission: PERMISSIONS.manageFields },
  { path: 'users-groups', label: 'Users & Groups', permission: PERMISSIONS.manageUsers },
  { path: 'roles-security', label: 'Roles & Security', permission: PERMISSIONS.manageSecurity },
  { path: 'ethical-walls', label: 'Ethical Walls', permission: PERMISSIONS.manageSecurity },
  { path: 'settings', label: 'Workspace Settings', permission: PERMISSIONS.manageSecurity },
  // The empty-state checklist a new workspace lands on (E04-T07); stays available to finish setting up.
  { path: 'setup', label: 'Setup Checklist', permission: PERMISSIONS.manageSecurity },
  { path: 'audit', label: 'Audit', permission: PERMISSIONS.auditRead },
];

export function allowedSections(
  sections: readonly WorkspaceSection[],
  permissions: readonly string[],
): WorkspaceSection[] {
  const granted = new Set(permissions);
  return sections.filter((s) => granted.has(s.permission));
}
