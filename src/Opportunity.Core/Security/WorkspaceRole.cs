using System.Collections.Frozen;

namespace Opportunity.Core.Security;

/// <summary>
/// Built-in workspace roles (ADR-015 D5.6/D5.7). Grants are fixed in code; custom roles are post-MVP. The wire name
/// (<see cref="RoleCatalog.Key"/>) is what <c>workspace_role_assignment.role</c> stores.
/// </summary>
public enum WorkspaceRole
{
    WorkspaceAdmin = 1,
    Reviewer = 2,
    QcReviewer = 3,
    PrivilegeReviewer = 4,
    ProductionManager = 5,
    Auditor = 6,

    /// <summary>
    /// Emergency access (Q-13, Q-45, ADR-015 D6.4). Grants nothing and is not membership on its own: it counts only while
    /// the holder has an active, unexpired break-glass activation in the workspace, and then only for read permissions.
    /// </summary>
    BreakGlass = 7,
}

public sealed record RoleInfo(WorkspaceRole Role, string Key, string DisplayName, FrozenSet<Permission> Grants);

public static class RoleCatalog
{
    private static readonly Permission[] Everything = [.. PermissionCatalog.All.Select(p => p.Permission)];

    public static IReadOnlyList<RoleInfo> All { get; } =
    [
        Role(WorkspaceRole.WorkspaceAdmin, "WorkspaceAdmin", "Workspace Admin", Everything),
        Role(WorkspaceRole.Reviewer, "Reviewer", "Reviewer",
        [
            Permission.DocumentView, Permission.SearchExecute, Permission.CodingWrite, Permission.RedactionApply,
        ]),
        Role(WorkspaceRole.QcReviewer, "QcReviewer", "QC Reviewer",
        [
            Permission.DocumentView, Permission.SearchExecute, Permission.SavedSearchShare, Permission.SearchTermReportRun, Permission.CodingWrite,
            Permission.CodingBulk, Permission.RedactionApply, Permission.RedactionRemove,
        ]),
        Role(WorkspaceRole.PrivilegeReviewer, "PrivilegeReviewer", "Privilege Reviewer",
        [
            Permission.DocumentView, Permission.SearchExecute, Permission.SavedSearchShare, Permission.SearchTermReportRun, Permission.CodingWrite,
            Permission.CodingWritePrivilege, Permission.CodingBulk, Permission.RedactionApply, Permission.RedactionRemove,
            Permission.PrivilegeLogGenerate,
        ]),
        Role(WorkspaceRole.ProductionManager, "ProductionManager", "Production Manager",
        [
            Permission.DocumentView, Permission.DocumentDownloadNative, Permission.DocumentPrint, Permission.SearchExecute,
            Permission.SavedSearchShare, Permission.SearchTermReportRun, Permission.CodingBulk, Permission.ExportCreate, Permission.ExportDownload,
            Permission.ProductionCreate, Permission.ProductionFinalize, Permission.PrivilegeLogGenerate, Permission.JobViewAll,
        ]),
        Role(WorkspaceRole.Auditor, "Auditor", "Auditor (read-only)",
        [
            Permission.DocumentView, Permission.SearchExecute, Permission.JobViewAll, Permission.AuditRead,
            Permission.AuditReadSearchText,
        ]),
        Role(WorkspaceRole.BreakGlass, "BreakGlass", "Break-glass",
        [
            .. PermissionCatalog.All.Where(p => p.BreakGlassEligible).Select(p => p.Permission),
        ]),
    ];

    private static readonly FrozenDictionary<WorkspaceRole, RoleInfo> ByRole = All.ToFrozenDictionary(r => r.Role);

    private static readonly FrozenDictionary<string, RoleInfo> ByKey = All.ToFrozenDictionary(r => r.Key, StringComparer.Ordinal);

    public static RoleInfo Get(WorkspaceRole role) =>
        ByRole.TryGetValue(role, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Not a built-in role.");

    public static string Key(this WorkspaceRole role) => Get(role).Key;

    public static bool Grants(this WorkspaceRole role, Permission permission) => Get(role).Grants.Contains(permission);

    public static bool TryParse(string? key, out WorkspaceRole role)
    {
        if (key is not null && ByKey.TryGetValue(key, out var info))
        {
            role = info.Role;
            return true;
        }

        role = default;
        return false;
    }

    private static RoleInfo Role(WorkspaceRole role, string key, string displayName, Permission[] grants) =>
        new(role, key, displayName, grants.ToFrozenSet());
}

/// <summary>
/// Built-in document restriction classes and their default grants (Q-11, ADR-015 D6.1). Workspaces may add classes
/// and tighten grants; a class grant names the roles that may see documents carrying the class.
/// </summary>
public static class RestrictionClasses
{
    public const string Privileged = "Privileged";
    public const string Confidential = "Confidential";
    public const string AttorneysEyesOnly = "AttorneysEyesOnly";

    /// <summary>Seeded for every workspace by V0012 (the SQL seed is checked against this list by a test).</summary>
    public static IReadOnlyList<(string ClassKey, string DisplayName, IReadOnlyList<WorkspaceRole> Roles)> BuiltIn { get; } =
    [
        (Privileged, "Privileged", ReviewRoles),
        (Confidential, "Confidential", ReviewRoles),
        (AttorneysEyesOnly, "Attorneys' Eyes Only",
            [WorkspaceRole.WorkspaceAdmin, WorkspaceRole.PrivilegeReviewer, WorkspaceRole.ProductionManager]),
    ];

    private static IReadOnlyList<WorkspaceRole> ReviewRoles =>
    [
        WorkspaceRole.WorkspaceAdmin, WorkspaceRole.Reviewer, WorkspaceRole.QcReviewer, WorkspaceRole.PrivilegeReviewer,
        WorkspaceRole.ProductionManager, WorkspaceRole.Auditor,
    ];
}
