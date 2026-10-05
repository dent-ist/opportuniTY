using System.Diagnostics.CodeAnalysis;

namespace Opportunity.Core.Security;

/// <summary>
/// The closed workspace permission set (ADR-015 D5.6). Adding a member is a reviewed change (CODEOWNERS Security):
/// give it a <see cref="PermissionCatalog"/> entry, grant it in <see cref="RoleCatalog"/> and regenerate
/// <c>docs/security/permission-matrix.md</c>. Never renumber: values may be persisted.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "ADR-015 D5.6 names the type Permission.")]
public enum Permission
{
    DocumentView = 1,
    DocumentDownloadNative = 2,
    DocumentPrint = 3,
    DocumentViewQuarantined = 4,
    SearchExecute = 5,
    SavedSearchShare = 6,
    CodingWrite = 7,
    CodingWritePrivilege = 8,
    CodingBulk = 9,
    RedactionApply = 10,
    RedactionRemove = 11,
    ImportRun = 12,
    ImportOverlay = 13,
    ExportCreate = 14,
    ExportDownload = 15,
    ProductionCreate = 16,
    ProductionFinalize = 17,
    PrivilegeLogGenerate = 18,
    JobViewAll = 19,
    JobManage = 20,
    AuditRead = 21,
    AuditReadSearchText = 22,
    WorkspaceManageUsers = 23,
    WorkspaceManageSecurity = 24,
    WorkspaceManageFields = 25,
    WorkspaceRequestDeletion = 26,
    JobReplay = 27,
    SearchTermReportRun = 28,
    ViewManageShared = 29,
}

/// <summary>Catalogue entry: the dotted wire name used in docs, audit details and the API, and what it allows.</summary>
public sealed record PermissionInfo(Permission Permission, string Name, string Description)
{
    /// <summary>
    /// Read permissions that an active break-glass activation may exercise past restriction classes and walls
    /// (ADR-015 D6.4, Q-45). Everything else (download, print, coding, export, production) stays blocked.
    /// </summary>
    public bool BreakGlassEligible => Permission is Permission.DocumentView or Permission.SearchExecute or Permission.AuditRead;
}

public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        new(Permission.DocumentView, "Document.View", "Open a document: metadata, extracted text and renditions."),
        new(Permission.DocumentDownloadNative, "Document.DownloadNative", "Download the native file."),
        new(Permission.DocumentPrint, "Document.Print", "Print or save rendered pages."),
        new(Permission.DocumentViewQuarantined, "Document.ViewQuarantined", "See that a native is quarantined and its scan result; never renders it."),
        new(Permission.SearchExecute, "Search.Execute", "Run searches and see result pages (post-filtered, Q-12)."),
        new(Permission.SearchTermReportRun, "SearchTermReport.Run", "Run, rerun and export search term reports (Q-30); with Search.Execute."),
        new(Permission.SavedSearchShare, "SavedSearch.Share", "Share saved searches with other workspace users."),
        new(Permission.CodingWrite, "Coding.Write", "Change coding fields that are not security-affecting."),
        new(Permission.CodingWritePrivilege, "Coding.WritePrivilege", "Change security-affecting fields: privilege, confidentiality, wall membership."),
        new(Permission.CodingBulk, "Coding.Bulk", "Submit bulk coding jobs."),
        new(Permission.RedactionApply, "Redaction.Apply", "Add or modify redactions."),
        new(Permission.RedactionRemove, "Redaction.Remove", "Remove redactions."),
        new(Permission.ImportRun, "Import.Run", "Run imports of new documents."),
        new(Permission.ImportOverlay, "Import.Overlay", "Run overlay imports, including coding and privilege overlays (Q-31)."),
        new(Permission.ExportCreate, "Export.Create", "Create exports."),
        new(Permission.ExportDownload, "Export.Download", "Download export packages."),
        new(Permission.ProductionCreate, "Production.Create", "Create and run productions."),
        new(Permission.ProductionFinalize, "Production.Finalize", "Finalize productions."),
        new(Permission.PrivilegeLogGenerate, "PrivilegeLog.Generate", "Generate privilege logs."),
        new(Permission.JobViewAll, "Job.ViewAll", "See every user's jobs (users always see their own)."),
        new(Permission.JobManage, "Job.Manage", "Cancel, pause and resume other users' jobs (users may cancel their own)."),
        new(Permission.JobReplay, "Job.Replay", "Replay failed job chunks, index tasks and search outbox rows from PostgreSQL (ADR-010 §7.4)."),
        new(Permission.AuditRead, "Audit.Read", "Read the workspace audit trail."),
        new(Permission.AuditReadSearchText, "Audit.ReadSearchText", "Read search query text in the audit trail (Q-16)."),
        new(Permission.WorkspaceManageUsers, "Workspace.ManageUsers", "Assign and revoke workspace roles."),
        new(Permission.WorkspaceManageSecurity, "Workspace.ManageSecurity", "Manage restriction classes, class grants and ethical walls."),
        new(Permission.WorkspaceManageFields, "Workspace.ManageFields", "Manage fields, choices and coding layouts."),
        new(Permission.WorkspaceRequestDeletion, "Workspace.RequestDeletion", "Request workspace deletion (Q-23)."),
        new(Permission.ViewManageShared, "View.ManageShared", "Create, change and delete the document-list views shared with the whole workspace."),
    ];

    private static readonly Dictionary<Permission, PermissionInfo> ByPermission = All.ToDictionary(p => p.Permission);

    private static readonly Dictionary<string, PermissionInfo> ByName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static PermissionInfo Get(Permission permission) =>
        ByPermission.TryGetValue(permission, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(permission), permission, "Not a catalogued permission.");

    /// <summary>The dotted wire name, e.g. <c>Document.View</c>.</summary>
    public static string Name(this Permission permission) => Get(permission).Name;

    public static bool TryParse(string name, out Permission permission)
    {
        if (name is not null && ByName.TryGetValue(name, out var info))
        {
            permission = info.Permission;
            return true;
        }

        permission = default;
        return false;
    }
}
