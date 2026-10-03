/**
 * Workspace navigation (familiarity guide §2.1–§2.3). Order is fixed; a section shows only when the API
 * grants its permission in the workspace (RBAC-driven navigation, baseline §15). Search is part of
 * Documents, so there is no separate Search section. Review Batches stay hidden until M5.
 *
 * The permission names follow the E05-T02 catalogue style (`Area.Action`). Until that catalogue is
 * published, this table is the single place to align them.
 */
export const PERMISSIONS = {
  documentView: 'Document.View',
  searchTermsReportView: 'SearchTermsReport.View',
  productionView: 'Production.View',
  importView: 'Import.View',
  exportView: 'Export.View',
  jobView: 'Job.View',
  workspaceAdmin: 'Workspace.Admin',
  manageUsers: 'Workspace.ManageUsers',
  auditRead: 'Audit.Read',
} as const;

export interface WorkspaceSection {
  /** Route path under `/w/:workspaceId`. */
  readonly path: string;
  readonly label: string;
  readonly permission: string;
}

export const WORKSPACE_SECTIONS: readonly WorkspaceSection[] = [
  { path: 'documents', label: 'Documents', permission: PERMISSIONS.documentView },
  {
    path: 'search-terms-reports',
    label: 'Search Terms Reports',
    permission: PERMISSIONS.searchTermsReportView,
  },
  { path: 'productions', label: 'Productions', permission: PERMISSIONS.productionView },
  { path: 'imports', label: 'Imports', permission: PERMISSIONS.importView },
  { path: 'exports', label: 'Exports', permission: PERMISSIONS.exportView },
  { path: 'jobs', label: 'Jobs', permission: PERMISSIONS.jobView },
];

/** Entries of the Admin ▾ menu, in guide order; paths are under `/w/:workspaceId/admin/`. */
export const ADMIN_AREAS: readonly WorkspaceSection[] = [
  { path: 'fields', label: 'Fields', permission: PERMISSIONS.workspaceAdmin },
  { path: 'choices', label: 'Choices', permission: PERMISSIONS.workspaceAdmin },
  { path: 'coding-layouts', label: 'Coding Layouts', permission: PERMISSIONS.workspaceAdmin },
  { path: 'views', label: 'Views', permission: PERMISSIONS.workspaceAdmin },
  { path: 'highlight-sets', label: 'Highlight Sets', permission: PERMISSIONS.workspaceAdmin },
  { path: 'redaction-sets', label: 'Redaction Sets', permission: PERMISSIONS.workspaceAdmin },
  { path: 'users-groups', label: 'Users & Groups', permission: PERMISSIONS.manageUsers },
  { path: 'roles-security', label: 'Roles & Security', permission: PERMISSIONS.workspaceAdmin },
  { path: 'ethical-walls', label: 'Ethical Walls', permission: PERMISSIONS.workspaceAdmin },
  { path: 'settings', label: 'Workspace Settings', permission: PERMISSIONS.workspaceAdmin },
  { path: 'audit', label: 'Audit', permission: PERMISSIONS.auditRead },
];

export function allowedSections(
  sections: readonly WorkspaceSection[],
  permissions: readonly string[],
): WorkspaceSection[] {
  const granted = new Set(permissions);
  return sections.filter((s) => granted.has(s.permission));
}
