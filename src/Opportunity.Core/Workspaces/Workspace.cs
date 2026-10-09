namespace Opportunity.Core.Workspaces;

/// <summary>Workspace lifecycle (ADR-014 §1). Legal holds are separate preservation-lock records, not a status.</summary>
public enum WorkspaceStatus
{
    Active,
    Closed,
    Deleting,
    Purged,
}

/// <summary>A matter workspace: the tenant and isolation boundary for every document, page and object.</summary>
public sealed class Workspace
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? MatterNumber { get; set; }

    /// <summary>IANA time zone id used for display (ADR-009 R26); users may override it.</summary>
    public string DisplayTimeZone { get; set; } = string.Empty;

    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;

    /// <summary>Starts the audit retention clock; null exactly when the workspace is Active.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Fencing epoch, incremented when deletion starts (ADR-014 §4).</summary>
    public long Epoch { get; set; } = 1;

    /// <summary>ADR-009 R2; fixed once the workspace holds a document.</summary>
    public bool ControlNumberCaseSensitive { get; set; }

    /// <summary>Named object-storage profile of the installation (E04-T05); fixed once the workspace stores an object.</summary>
    public string StorageProfile { get; set; } = WorkspaceRules.DefaultStorageProfile;

    /// <summary>Optimistic concurrency version of the settings, the API's ETag (ADR-019 §2.7).</summary>
    public long RowVersion { get; set; } = 1;

    /// <summary>Active preservation locks (legal holds, ADR-014 §2); maintained by the database, never written by the application.</summary>
    public int ActivePreservationLocks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
