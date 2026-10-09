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

    /// <summary>Search term reports (E07-T10, Q-30): runs, exports and deletion, with the report ID and snapshot.</summary>
    public static class SearchTermReport
    {
        public const string Category = "Search";
        public const string Generated = "TermReportGenerated";
        public const string Exported = "TermReportExported";
        public const string Deleted = "TermReportDeleted";

        /// <summary>The audit resource type of report events.</summary>
        public const string ResourceType = "SearchTermReport";
    }

    /// <summary>Changes to shared document-list views (E16-T09); personal views are not audited.</summary>
    public static class GridView
    {
        public const string Category = "Search";
        public const string Created = "GridView.Created";
        public const string Modified = "GridView.Modified";
        public const string Deleted = "GridView.Deleted";
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

    /// <summary>
    /// Redactions (E11-T04, ADR-012 §3.4): one event per added, modified or removed rectangle, in the transaction of the
    /// save (IDs, enums and geometry; never the note), plus changes to Redaction Sets and the reason picklist.
    /// </summary>
    public static class Redaction
    {
        public const string Category = "Redaction";
        public const string Added = "Added";
        public const string Modified = "Modified";
        public const string Removed = "Removed";
        public const string SetCreated = "RedactionSet.Created";
        public const string SetModified = "RedactionSet.Modified";
        public const string ReasonCreated = "Reason.Created";
        public const string ReasonModified = "Reason.Modified";

        /// <summary>The audit resource type of redaction events (resource ID: the redaction ID).</summary>
        public const string ResourceType = "Redaction";

        public const string SetResourceType = "RedactionSet";
        public const string ReasonResourceType = "RedactionReason";
    }

    public static class Coding
    {
        public const string Category = "Coding";
        public const string Changed = "Changed";

        /// <summary>
        /// A document's coding was propagated to its family and/or duplicates (E09-T05, Q-14): one event per apply, on the
        /// source document, carrying the preview, scope, mode and the originating CodingEvents.
        /// </summary>
        public const string FamilyApplied = "FamilyApplied";

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

    /// <summary>Productions (E12-T02/T03, ADR-013 §5, Q-54).</summary>
    public static class Production
    {
        public const string Category = "Production";
        public const string Created = "Created";

        /// <summary>A draft's name, frozen set or specification changed (its Bates allocation, if any, was released).</summary>
        public const string Modified = "Modified";

        /// <summary>A draft's Bates numbers were allocated and passed the integrity check.</summary>
        public const string BatesAllocated = "BatesAllocated";

        /// <summary>The specification was frozen into the manifest at finalization.</summary>
        public const string SpecFrozen = "SpecFrozen";

        public const string Finalized = "Finalized";
        public const string Voided = "Voided";

        /// <summary>A draft was discarded; its never-produced Bates numbers were released for reuse (Q-54).</summary>
        public const string Discarded = "Discarded";

        /// <summary>A re-verification matched the manifest.</summary>
        public const string Verified = "Verified";

        /// <summary>A re-verification found differences from the manifest.</summary>
        public const string VerificationFailed = "VerificationFailed";

        /// <summary>A draft's designation of one member was set by a person, with a reason (E12-T04).</summary>
        public const string DesignationOverridden = "DesignationOverridden";

        /// <summary>A draft's designation override was removed; the family rule applies again.</summary>
        public const string DesignationOverrideRemoved = "DesignationOverrideRemoved";

        /// <summary>Every member's designation was frozen at finalization (rule and counts by source).</summary>
        public const string DesignationsFrozen = "DesignationsFrozen";

        /// <summary>A re-designation overlay load file was downloaded.</summary>
        public const string RedesignationExported = "RedesignationExported";

        /// <summary>The audit resource type of production events.</summary>
        public const string ResourceType = "Production";
    }

    public static class Security
    {
        public const string Category = "Security";
        public const string RoleAssigned = "RoleAssigned";

        /// <summary>A role assignment was removed (E05-T08).</summary>
        public const string RoleRevoked = "RoleRevoked";

        /// <summary>The audit resource type of role assignment events; the resource id is the assignment id.</summary>
        public const string RoleAssignmentResourceType = "RoleAssignment";

        /// <summary>A restriction class, its grants or its coding rules changed (E05-T06).</summary>
        public const string RestrictionChanged = "RestrictionChanged";

        /// <summary>A field-level restriction changed (E05-T06).</summary>
        public const string PermissionChanged = "PermissionChanged";

        public const string WallCreated = "WallCreated";
        public const string WallChanged = "WallChanged";
        public const string WallDeleted = "WallDeleted";
        public const string BreakGlassActivated = "BreakGlassActivated";
        public const string BreakGlassEnded = "BreakGlassEnded";
    }

    public static class Workspace
    {
        public const string Category = "Workspace";
        public const string Created = "Created";
        public const string SettingsChanged = "SettingsChanged";
    }

    /// <summary>
    /// Field and coding layout administration (E04-T06): a field or its choices, or a layout, changed. IDs and enums
    /// only (never names); a field is retired, never purged here (ADR-003 R6).
    /// </summary>
    public static class FieldCatalog
    {
        public const string Category = "Workspace";
        public const string FieldCreated = "Field.Created";
        public const string FieldModified = "Field.Modified";
        public const string FieldRetired = "Field.Retired";
        public const string LayoutCreated = "CodingLayout.Created";
        public const string LayoutModified = "CodingLayout.Modified";
        public const string LayoutDeleted = "CodingLayout.Deleted";

        public const string FieldResourceType = "Field";
        public const string LayoutResourceType = "CodingLayout";
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

        /// <summary>A Bates allocation overlapped another production's range or failed its integrity check (E12-T03).</summary>
        public const string BatesConflict = "BatesConflict";
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
        .. Expand("Search", "Executed", "ResultsPageServed", "CountExact", "TermReportGenerated", "TermReportExported", "TermReportDeleted",
            "SavedSearch.Created", "SavedSearch.Modified", "SavedSearch.Deleted", "SavedSearch.Shared",
            "GridView.Created", "GridView.Modified", "GridView.Deleted",
            "HighlightSet.Created", "HighlightSet.Modified", "HighlightSet.Deleted"),
        .. Expand("Coding", "Changed", "FamilyApplied", "BulkSubmitted", "BulkChunkApplied", "BulkCompleted", "OverlayEnabled"),
        .. Expand("Privilege", "LogGenerated", "ConflictOverride", "ClawbackRecorded"),
        .. Expand("Redaction", "Added", "Modified", "Removed", "RedactionSet.Created", "RedactionSet.Modified", "Reason.Created", "Reason.Modified"),
        .. Expand("Export", "Created", "Completed", "DocumentsExcluded", "Downloaded"),
        .. Expand("Production", "Created", "SpecFrozen", "Run", "VerificationFailed", "QcOverride", "Finalized", "Voided",
            "Downloaded", "Rerun", "Modified", "Discarded", "BatesAllocated", "Verified",
            "DesignationOverridden", "DesignationOverrideRemoved", "DesignationsFrozen", "RedesignationExported"),
        .. Expand("Import", "Started", "Completed", "MalwareDetected", "HashMismatch", "PreflightRun", "ReportDownloaded", "Overlaid"),
        .. Expand("Security", "RoleAssigned", "RoleRevoked", "PermissionChanged", "RestrictionChanged", "WallCreated",
            "WallChanged", "WallDeleted", "WallMemberAdded", "WallMemberRemoved", "BreakGlassActivated", "BreakGlassEnded",
            "AcknowledgmentAccepted"),
        .. Expand("Workspace", "Created", "SettingsChanged", "Closed", "Reopened", "HoldPlaced", "HoldReleaseRequested",
            "HoldReleased", "DeletionRequested", "DeletionApproved", "DeletionCancelled", "DeletionStarted",
            "DeletionStepCompleted", "DeletionHalted", "Deleted",
            "Field.Created", "Field.Modified", "Field.Retired", "CodingLayout.Created", "CodingLayout.Modified", "CodingLayout.Deleted"),
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
