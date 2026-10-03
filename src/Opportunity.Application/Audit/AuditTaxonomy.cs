namespace Opportunity.Application.Audit;

/// <summary>
/// The closed ADR-013 §5 taxonomy. The audit store rejects any other Category/Action pair (<c>audit.audit_action</c>,
/// V0010); a contract test keeps <see cref="All"/> and that table identical. Constants exist for the actions already
/// emitted; the rest arrive with their features.
/// </summary>
public static class AuditTaxonomy
{
    public static class Auth
    {
        public const string Category = "Auth";
        public const string SignIn = "SignIn";
        public const string SignInFailed = "SignInFailed";
        public const string SignOut = "SignOut";
        public const string SessionExpired = "SessionExpired";
        public const string SessionRevoked = "SessionRevoked";
        public const string StepUp = "StepUp";
    }

    public static class AuthZ
    {
        public const string Category = "AuthZ";
        public const string Denied = "Denied";
    }

    /// <summary>Protected-content access through the gateway (E05-T04, ADR-013 §5).</summary>
    public static class Document
    {
        public const string Category = "Document";

        /// <summary>One gateway grant (or denied attempt): rendition, purpose (Display, Prefetch), object ID.</summary>
        public const string Retrieved = "Retrieved";

        /// <summary>The viewer displayed the document; references the <see cref="Retrieved"/> event.</summary>
        public const string Viewed = "Viewed";

        public const string NativeDownloaded = "NativeDownloaded";
        public const string Printed = "Printed";
        public const string TextDownloaded = "TextDownloaded";
    }

    public static class Coding
    {
        public const string Category = "Coding";
        public const string Changed = "Changed";
        public const string BulkChunkApplied = "BulkChunkApplied";
    }

    public static class Security
    {
        public const string Category = "Security";
        public const string RoleAssigned = "RoleAssigned";
    }

    public static class Workspace
    {
        public const string Category = "Workspace";
        public const string Created = "Created";
        public const string SettingsChanged = "SettingsChanged";
    }

    public static class Job
    {
        public const string Category = "Job";
        public const string Created = "Created";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";
        public const string CompletedWithErrors = "CompletedWithErrors";
        public const string Replayed = "Replayed";
    }

    public static class Integrity
    {
        public const string Category = "Integrity";

        /// <summary>A worker rejected a message whose envelope disagrees with PostgreSQL (ADR-013 §5, ADR-015 D9.3).</summary>
        public const string EnvelopeMismatch = "EnvelopeMismatch";
    }

    public static class Audit
    {
        public const string Category = "Audit";
        public const string Queried = "Queried";
        public const string Purged = "Purged";
    }

    /// <summary>Every allowed (Category, Action) pair.</summary>
    public static IReadOnlyList<(string Category, string Action)> All { get; } =
    [
        .. Expand("Auth", "SignIn", "SignInFailed", "SignOut", "SessionExpired", "SessionRevoked", "StepUp"),
        .. Expand("AuthZ", "Denied"),
        .. Expand("Document", "Retrieved", "Viewed", "NativeDownloaded", "Printed", "TextDownloaded"),
        .. Expand("Search", "Executed", "ResultsPageServed", "CountExact", "TermReportGenerated",
            "SavedSearch.Created", "SavedSearch.Modified", "SavedSearch.Deleted"),
        .. Expand("Coding", "Changed", "FamilyApplied", "BulkSubmitted", "BulkChunkApplied", "BulkCompleted", "OverlayEnabled"),
        .. Expand("Privilege", "LogGenerated", "ConflictOverride", "ClawbackRecorded"),
        .. Expand("Redaction", "Added", "Modified", "Removed"),
        .. Expand("Export", "Created", "Completed", "DocumentsExcluded", "Downloaded"),
        .. Expand("Production", "Created", "SpecFrozen", "Run", "VerificationFailed", "QcOverride", "Finalized", "Voided",
            "Downloaded", "Rerun"),
        .. Expand("Import", "Started", "Completed", "MalwareDetected", "HashMismatch"),
        .. Expand("Security", "RoleAssigned", "RoleRevoked", "PermissionChanged", "RestrictionChanged", "WallCreated",
            "WallChanged", "WallDeleted", "WallMemberAdded", "WallMemberRemoved", "BreakGlassActivated", "BreakGlassEnded",
            "AcknowledgmentAccepted"),
        .. Expand("Workspace", "Created", "SettingsChanged", "Closed", "Reopened", "HoldPlaced", "HoldReleaseRequested",
            "HoldReleased", "DeletionRequested", "DeletionApproved", "DeletionCancelled", "DeletionStarted",
            "DeletionStepCompleted", "DeletionHalted", "Deleted"),
        .. Expand("Admin", "ConfigChanged", "UserProvisioned", "UserDeactivated", "KeyCreated", "KeyRotated", "KeyDestroyed",
            "SecretRotated"),
        .. Expand("Audit", "Queried", "Exported", "CheckpointCreated", "Verified", "Purged"),
        .. Expand("Job", "Created", "Cancelled", "Failed", "CompletedWithErrors", "Replayed"),
        .. Expand("Integrity", "HashMismatch", "ChainBroken", "FenceViolation", "EnvelopeMismatch", "BatesConflict"),
    ];

    /// <summary>The category that may carry <see cref="AuditEvent.RestrictedDetails"/> (Q-16 search text).</summary>
    public const string SearchCategory = "Search";

    private static readonly HashSet<(string, string)> Allowed = [.. All];

    public static bool IsDefined(string category, string action) => Allowed.Contains((category, action));

    private static IEnumerable<(string, string)> Expand(string category, params string[] actions) =>
        actions.Select(action => (category, action));
}
