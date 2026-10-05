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

    /// <summary>Saved searches (E07-T09): changes to a search's criteria, place and sharing. Runs are <c>Search.Executed</c>.</summary>
    public static class SavedSearch
    {
        public const string Category = "Search";
        public const string Created = "SavedSearch.Created";
        public const string Modified = "SavedSearch.Modified";
        public const string Deleted = "SavedSearch.Deleted";
        public const string Shared = "SavedSearch.Shared";
    }

    /// <summary>Highlight Sets (E16-T12): a set's terms, colours and name changed. Toggling a set is not audited.</summary>
    public static class HighlightSet
    {
        public const string Category = "Search";
        public const string Created = "HighlightSet.Created";
        public const string Modified = "HighlightSet.Modified";
        public const string Deleted = "HighlightSet.Deleted";

        /// <summary>The audit resource type of highlight-set events.</summary>
        public const string ResourceType = "HighlightSet";
    }

    public static class Coding
    {
        public const string Category = "Coding";
        public const string Changed = "Changed";

        /// <summary>A bulk coding job was submitted over a snapshot (E10-T04): one event per job, never per document.</summary>
        public const string BulkSubmitted = "BulkSubmitted";

        public const string BulkChunkApplied = "BulkChunkApplied";

        /// <summary>A bulk coding job finished its chunks (Completed or CompletedWithErrors) with its final counts.</summary>
        public const string BulkCompleted = "BulkCompleted";

        /// <summary>An administrator enabled coding/privilege fields for overlay by one import (Q-31).</summary>
        public const string OverlayEnabled = "OverlayEnabled";
    }

    public static class Import
    {
        public const string Category = "Import";
        public const string Started = "Started";
        public const string Completed = "Completed";

        /// <summary>A pre-flight validation ran (E08-T06); it wrote nothing but its own result.</summary>
        public const string PreflightRun = "PreflightRun";

        /// <summary>An import report, error file or pre-flight issue list was downloaded through the gateway (E08-T06).</summary>
        public const string ReportDownloaded = "ReportDownloaded";

        /// <summary>The audit resource type of import events.</summary>
        public const string ResourceType = "ImportBatch";

        /// <summary>An import chunk overlaid documents (E08-T07); old and new values are in <c>document_overlay_event</c>.</summary>
        public const string Overlaid = "Overlaid";
    }

    /// <summary>Load-file exports of a frozen set (E12-T01, ADR-013 §5).</summary>
    public static class Export
    {
        public const string Category = "Export";
        public const string Created = "Created";
        public const string Completed = "Completed";

        /// <summary>Members a chunk left out after the Q-15 access re-check, with the precise reason per document.</summary>
        public const string DocumentsExcluded = "DocumentsExcluded";

        public const string Downloaded = "Downloaded";

        /// <summary>The audit resource type of export events.</summary>
        public const string ResourceType = "Export";
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
            "SavedSearch.Created", "SavedSearch.Modified", "SavedSearch.Deleted", "SavedSearch.Shared",
            "HighlightSet.Created", "HighlightSet.Modified", "HighlightSet.Deleted"),
        .. Expand("Coding", "Changed", "FamilyApplied", "BulkSubmitted", "BulkChunkApplied", "BulkCompleted", "OverlayEnabled"),
        .. Expand("Privilege", "LogGenerated", "ConflictOverride", "ClawbackRecorded"),
        .. Expand("Redaction", "Added", "Modified", "Removed"),
        .. Expand("Export", "Created", "Completed", "DocumentsExcluded", "Downloaded"),
        .. Expand("Production", "Created", "SpecFrozen", "Run", "VerificationFailed", "QcOverride", "Finalized", "Voided",
            "Downloaded", "Rerun"),
        .. Expand("Import", "Started", "Completed", "MalwareDetected", "HashMismatch", "PreflightRun", "ReportDownloaded", "Overlaid"),
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
