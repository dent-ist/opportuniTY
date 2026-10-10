using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

using Opportunity.Api.Content;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>How the coverage suite proves a route's audit event (E14-T02).</summary>
internal enum AuditCoverage
{
    /// <summary>The route's own-identifier probe of <see cref="RouteAttackCatalog"/> is sent and its event is read back.</summary>
    Probe,

    /// <summary>A request built here (with any setup it needs) is sent and its event is read back.</summary>
    Custom,

    /// <summary>A step of the workspace lifecycle scenario of <see cref="AuditCoverageTests"/> (create, hold, deletion).</summary>
    Scenario,

    /// <summary>An existing test asserts the event (named, checked to exist); the route cannot be probed in the attack world.</summary>
    CoveredBy,

    /// <summary>The route writes no event, for the stated reason. Never allowed for a route the metadata says must be audited.</summary>
    NotAudited,
}

/// <summary>One request of a custom probe: sent once by a non-member (denied, audited) and once by <see cref="Actor"/>.</summary>
internal sealed record AuditRequest(HttpMethod Method, string Url, Guid Actor, Func<HttpContent?>? Body = null, string? IfMatch = null, string? Amr = null);

/// <summary>A route of the endpoint data source and how its audit event is proved (or why it writes none).</summary>
internal sealed record AuditCase(
    string Method,
    string Pattern,
    AuditCoverage Coverage,
    string? Event = null,
    string? ProbeName = null,
    Func<AttackWorld, Task<AuditRequest>>? Custom = null,
    string? Note = null)
{
    public string Key => $"{Method} {Pattern}";
}

/// <summary>
/// E14-T02: every route the API host maps, with the audit event it writes (as <c>Category.Action</c>) and how the suite
/// proves it, or the reason it writes none. A route missing here fails the build, like <see cref="RouteAttackCatalog"/>;
/// a route that <see cref="MustAudit"/> derives from its endpoint metadata (protected content, search execution,
/// administration and protected state changes) can never be <see cref="AuditCoverage.NotAudited"/>.
/// </summary>
internal static class AuditCoverageCatalog
{
    private const string V1 = "/api/v1";
    private const string Ws = V1 + "/workspaces/{workspaceId}";

    private const string ConfigurationRead = "reads workspace configuration or status, never document content";
    private const string DocumentDataRead =
        "reads coding, relationships or redaction geometry through the PDP's document check; document content is served only by the gateway (Document.*)";
    private const string Personal = "the caller's own setting or history; no shared state changes";
    private const string Preview = "computes a preview or validation and changes nothing; the action it previews is audited";
    private const string ListRead = "lists or reads records whose creation and downloads are audited";

    /// <summary>
    /// Workspace permissions whose state-changing routes must write an audit event: administration (roles, security,
    /// fields, holds, deletion, job replay) and the protected operations (export, production, redaction, bulk and
    /// privilege coding, reports, sharing).
    /// </summary>
    private static readonly HashSet<Permission> AuditedWrites =
    [
        Permission.WorkspaceManageUsers, Permission.WorkspaceManageSecurity, Permission.WorkspaceManageFields, Permission.WorkspaceManageHolds,
        Permission.WorkspaceRequestDeletion, Permission.JobReplay, Permission.JobManage, Permission.ExportCreate, Permission.ExportDownload,
        Permission.ProductionCreate, Permission.ProductionFinalize, Permission.RedactionApply, Permission.RedactionRemove, Permission.CodingBulk,
        Permission.CodingWritePrivilege, Permission.PrivilegeLogGenerate, Permission.SearchTermReportRun, Permission.SavedSearchShare,
        Permission.ViewManageShared, Permission.HighlightSetManage, Permission.ReviewBatchManage, Permission.ImportOverlay,
    ];

    /// <summary>Why the endpoint's metadata requires an audit event, or null.</summary>
    public static string? MustAudit(RouteEndpoint endpoint, string method)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var pattern = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
        if (endpoint.Metadata.GetMetadata<ProtectedContentEndpointMetadata>() is not null)
        {
            return "protected-content gateway endpoint";
        }

        if (endpoint.Metadata.GetMetadata<DocumentPermissionMetadata>() is not null)
        {
            return "document permission endpoint";
        }

        if (endpoint.Metadata.GetMetadata<BreakGlassHolderMetadata>() is not null)
        {
            return "break-glass access";
        }

        if (pattern.EndsWith("/searches", StringComparison.Ordinal) || pattern.Contains("/searches/{searchId}", StringComparison.Ordinal))
        {
            return "search execution";
        }

        if (method is "GET" or "HEAD" or "*")
        {
            return null;
        }

        if (endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>() is { } required && AuditedWrites.Contains(required.Permission))
        {
            return $"state change under {required.Permission.Name()}";
        }

        if (endpoint.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy?.StartsWith("installation:", StringComparison.Ordinal) == true))
        {
            return "installation administration";
        }

        return pattern.Contains("/security/", StringComparison.Ordinal) ? "security administration" : null;
    }

    private static AuditCase Audited(string method, string suffix, string auditEvent, string? probe = null) =>
        new(method, Ws + suffix, AuditCoverage.Probe, auditEvent, probe);

    private static AuditCase Custom(string method, string suffix, string auditEvent, Func<AttackWorld, Task<AuditRequest>> request) =>
        new(method, Ws + suffix, AuditCoverage.Custom, auditEvent, Custom: request);

    private static AuditCase Scenario(string method, string pattern, string auditEvent) => new(method, pattern, AuditCoverage.Scenario, auditEvent);

    private static AuditCase CoveredBy(string method, string pattern, string auditEvent, string test) =>
        new(method, pattern, AuditCoverage.CoveredBy, auditEvent, Note: test);

    private static AuditCase None(string method, string pattern, string reason) => new(method, pattern, AuditCoverage.NotAudited, Note: reason);

    private static AuditCase NoneWs(string method, string suffix, string reason) => None(method, Ws + suffix, reason);

    private static string W(Guid workspaceId) => $"{V1}/workspaces/{workspaceId}";

    private static StringContent J(JsonNode node) => AttackWorld.Json(node);

    private static Func<HttpContent?> Body(JsonNode node) => () => J(node.DeepClone());

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    public static IReadOnlyList<AuditCase> Cases { get; } =
    [
        // Search execution (Q-16: query text in restricted details, snapshot/generation and hit count).
        NoneWs("POST", "/query-validations", Preview),
        Audited("POST", "/searches", "Search.Executed"),
        Audited("GET", "/searches/{searchId}/pages", "Search.ResultsPageServed"),
        NoneWs("GET", "/query-history", Personal),
        NoneWs("POST", "/query-history", Personal),
        NoneWs("GET", "/search-freshness", "reads the index watermark; no documents"),

        // Document security administration (E05-T06).
        NoneWs("GET", "/security/restriction-classes", ConfigurationRead),
        Audited("PUT", "/security/restriction-classes/{classKey}", "Security.RestrictionChanged"),
        Custom("DELETE", "/security/restriction-classes/{classKey}", "Security.RestrictionChanged", async w =>
        {
            var key = Unique("AuditProbe");
            await w.JsonAsync(HttpMethod.Put, $"{W(w.A.WorkspaceId)}/security/restriction-classes/{key}", w.Attacker, HttpStatusCode.Created,
                new JsonObject { ["displayName"] = "Audit probe class", ["roles"] = new JsonArray(), ["rules"] = new JsonArray() });
            return new AuditRequest(HttpMethod.Delete, $"{W(w.A.WorkspaceId)}/security/restriction-classes/{key}", w.Attacker, IfMatch: "*");
        }),
        NoneWs("GET", "/security/walls", ConfigurationRead),
        Audited("POST", "/security/walls", "Security.WallCreated"),
        NoneWs("GET", "/security/walls/{wallId}", ConfigurationRead),
        Audited("PUT", "/security/walls/{wallId}", "Security.WallChanged"),
        Audited("DELETE", "/security/walls/{wallId}", "Security.WallDeleted"),
        NoneWs("GET", "/security/field-restrictions", ConfigurationRead),
        Audited("PUT", "/security/field-restrictions/{fieldId}", "Security.PermissionChanged"),
        Custom("DELETE", "/security/field-restrictions/{fieldId}", "Security.PermissionChanged", async w =>
        {
            var url = $"{W(w.A.WorkspaceId)}/security/field-restrictions/{w.A.Fields.Notes}";
            using (var put = await w.SendAsync(HttpMethod.Put, url, w.Attacker,
                J(new JsonObject { ["visibleTo"] = new JsonArray("WorkspaceAdmin"), ["editableBy"] = new JsonArray("WorkspaceAdmin") }), ifMatch: "*"))
            {
                ((int)put.StatusCode).Should().BeInRange(200, 299, await put.Content.ReadAsStringAsync());
            }

            return new AuditRequest(HttpMethod.Delete, url, w.Attacker, IfMatch: "*");
        }),
        NoneWs("GET", "/security/break-glass/activations", ConfigurationRead),
        Custom("POST", "/security/break-glass/activations", "Security.BreakGlassActivated", async w =>
        {
            var glass = await w.Db.CreateUserAsync();
            await w.Db.AssignAsync(w.A.WorkspaceId, WorkspaceRole.BreakGlass, glass);
            return new AuditRequest(HttpMethod.Post, $"{W(w.A.WorkspaceId)}/security/break-glass/activations", glass,
                Body(new JsonObject { ["reason"] = "Audit coverage probe" }), Amr: "mfa");
        }),
        Audited("POST", "/security/break-glass/activations/{activationId}/end", "Security.BreakGlassEnded"),

        // Highlight Sets (E16-T12); toggling a set for oneself is not audited.
        NoneWs("GET", "/highlight-sets", ConfigurationRead),
        Audited("POST", "/highlight-sets", "Search.HighlightSet.Created"),
        NoneWs("GET", "/highlight-sets/{highlightSetId}", ConfigurationRead),
        Audited("PUT", "/highlight-sets/{highlightSetId}", "Search.HighlightSet.Modified"),
        Audited("DELETE", "/highlight-sets/{highlightSetId}", "Search.HighlightSet.Deleted"),
        NoneWs("GET", "/highlight-set-selection", Personal),
        NoneWs("PUT", "/highlight-set-selection", Personal),

        // Saved searches (E07-T09): criteria, place and sharing; folders only organize them.
        NoneWs("GET", "/saved-search-folders", ConfigurationRead),
        NoneWs("POST", "/saved-search-folders", "a folder only organizes saved searches; their criteria, place and sharing are SavedSearch.* events"),
        NoneWs("PUT", "/saved-search-folders/{folderId}", "a folder only organizes saved searches; their criteria, place and sharing are SavedSearch.* events"),
        NoneWs("DELETE", "/saved-search-folders/{folderId}", "deletes an empty folder only; a saved search's place is a SavedSearch.* event"),
        NoneWs("GET", "/saved-searches", ConfigurationRead),
        NoneWs("GET", "/saved-searches/share-candidates", ConfigurationRead),
        Audited("POST", "/saved-searches", "Search.SavedSearch.Created"),
        NoneWs("GET", "/saved-searches/{savedSearchId}", "reads the criteria; running them is Search.Executed"),
        Audited("PUT", "/saved-searches/{savedSearchId}", "Search.SavedSearch.Modified"),
        Audited("DELETE", "/saved-searches/{savedSearchId}", "Search.SavedSearch.Deleted"),
        Audited("POST", "/saved-searches/{savedSearchId}/clone", "Search.SavedSearch.Created"),
        Audited("PUT", "/saved-searches/{savedSearchId}/sharing", "Search.SavedSearch.Shared"),

        // Document-list views (E16-T09): shared views are audited, personal views and the caller's layout are not.
        NoneWs("GET", "/grid-views", ConfigurationRead),
        Custom("POST", "/grid-views", "Search.GridView.Created", w => Task.FromResult(new AuditRequest(HttpMethod.Post, $"{W(w.A.WorkspaceId)}/grid-views",
            w.Attacker, Body(new JsonObject { ["name"] = Unique("Shared audit probe "), ["visibility"] = "shared" })))),
        NoneWs("GET", "/grid-views/layout", Personal),
        NoneWs("PUT", "/grid-views/layout", Personal),
        NoneWs("GET", "/grid-views/{viewId}", ConfigurationRead),
        Audited("PUT", "/grid-views/{viewId}", "Search.GridView.Modified"),
        Custom("DELETE", "/grid-views/{viewId}", "Search.GridView.Deleted", async w =>
        {
            var view = await w.JsonAsync(HttpMethod.Post, $"{W(w.A.WorkspaceId)}/grid-views", w.Attacker, HttpStatusCode.Created,
                new JsonObject { ["name"] = Unique("Shared audit probe "), ["visibility"] = "shared" });
            return new AuditRequest(HttpMethod.Delete, $"{W(w.A.WorkspaceId)}/grid-views/{view.GetProperty("viewId").GetGuid()}", w.Attacker, IfMatch: "*");
        }),

        // Search term reports (E07-T10): the run is a job (its counts are TermReportGenerated when it completes).
        Audited("POST", "/search-term-reports", "Job.Created"),
        NoneWs("GET", "/search-term-reports", ListRead),
        NoneWs("GET", "/search-term-reports/{reportId}", "reads a report's counts; its runs and exports are audited"),
        Audited("GET", "/search-term-reports/{reportId}/export", "Search.TermReportExported"),
        Audited("POST", "/search-term-reports/{reportId}/rerun", "Job.Created"),
        Audited("DELETE", "/search-term-reports/{reportId}", "Search.TermReportDeleted"),

        // Jobs and search index operations.
        NoneWs("GET", "/jobs/{jobId}", ConfigurationRead),
        NoneWs("GET", "/jobs", ConfigurationRead),
        NoneWs("GET", "/jobs/{jobId}/failures", ConfigurationRead),
        CoveredBy("POST", Ws + "/jobs/{jobId}/cancel", "Job.Cancelled", nameof(AuditAtomicityTests.Job_lifecycle_events_commit_with_the_job_change)),
        // A replay that finds nothing failed changes nothing and is not audited; the attack world has no failures.
        CoveredBy("POST", Ws + "/jobs/{jobId}/retry-failed", "Job.Replayed",
            nameof(Jobs.JobOperationsApiTests.Retry_failed_replays_chunks_and_index_tasks_from_postgres_once_audited_and_only_with_job_replay)),
        NoneWs("GET", "/search-outbox/failures", ConfigurationRead),
        // A replay that finds nothing failed changes nothing and is not audited; the attack world has no failures.
        CoveredBy("POST", Ws + "/search-outbox/retry-failed", "Job.Replayed",
            nameof(Jobs.JobOperationsApiTests.Retry_failed_replays_chunks_and_index_tasks_from_postgres_once_audited_and_only_with_job_replay)),
        NoneWs("GET", "/job-events", "streams job status events; no documents"),
        NoneWs("GET", "/search-index", ConfigurationRead),
        Audited("POST", "/search-index/reindexes", "Job.Created"),

        // Workspace administration.
        NoneWs("GET", string.Empty, ConfigurationRead),
        Custom("PUT", string.Empty, "Workspace.SettingsChanged", w => Task.FromResult(new AuditRequest(HttpMethod.Put, W(w.A.WorkspaceId), w.Attacker,
            Body(new JsonObject { ["name"] = "Workspace A " + Guid.NewGuid().ToString("N")[..6], ["displayTimeZone"] = "UTC" }), IfMatch: "*"))),
        NoneWs("GET", "/members", ConfigurationRead),
        NoneWs("GET", "/roles", ConfigurationRead),
        NoneWs("GET", "/role-assignments", ConfigurationRead),
        NoneWs("GET", "/role-assignments/candidates", ConfigurationRead),
        Custom("PUT", "/role-assignments/users/{userId}", "Security.RoleAssigned", async w =>
        {
            var user = await w.Db.CreateUserAsync();
            return new AuditRequest(HttpMethod.Put, $"{W(w.A.WorkspaceId)}/role-assignments/users/{user}", w.Attacker,
                Body(new JsonObject { ["roles"] = new JsonArray("Reviewer") }), IfMatch: "*");
        }),
        Custom("PUT", "/role-assignments/groups", "Security.RoleAssigned", w => Task.FromResult(new AuditRequest(HttpMethod.Put,
            $"{W(w.A.WorkspaceId)}/role-assignments/groups?name=cn%3Daudit-probe-{Guid.NewGuid():N}", w.Attacker,
            Body(new JsonObject { ["roles"] = new JsonArray("Reviewer") }), IfMatch: "*"))),
        NoneWs("GET", "/deletions", ConfigurationRead),
        Scenario("POST", Ws + "/deletions", "Workspace.DeletionRequested"),
        NoneWs("GET", "/preservation-locks", ConfigurationRead),
        Scenario("POST", Ws + "/preservation-locks", "Workspace.HoldPlaced"),
        NoneWs("GET", "/preservation-locks/{lockId}", ConfigurationRead),
        Scenario("POST", Ws + "/preservation-locks/{lockId}/release", "Workspace.HoldReleaseRequested"),
        Scenario("POST", Ws + "/preservation-locks/{lockId}/release/approve", "Workspace.HoldReleased"),
        Scenario("POST", Ws + "/preservation-locks/{lockId}/release/cancel", "Workspace.HoldReleaseCancelled"),

        // Field and coding layout administration (E04-T06).
        NoneWs("GET", "/fields", ConfigurationRead),
        NoneWs("GET", "/coding-layouts", ConfigurationRead),
        NoneWs("GET", "/field-capacity", ConfigurationRead),
        Audited("POST", "/fields", "Workspace.Field.Created"),
        NoneWs("GET", "/fields/{fieldId}", ConfigurationRead),
        Audited("PUT", "/fields/{fieldId}", "Workspace.Field.Modified"),
        Custom("DELETE", "/fields/{fieldId}", "Workspace.Field.Retired", async w =>
        {
            var field = await w.JsonAsync(HttpMethod.Post, $"{W(w.A.WorkspaceId)}/fields", w.Attacker, HttpStatusCode.Created,
                new JsonObject { ["displayName"] = Unique("Retire probe "), ["type"] = "keyword", ["storage"] = "coding" });
            return new AuditRequest(HttpMethod.Delete, $"{W(w.A.WorkspaceId)}/fields/{field.GetProperty("fieldId").GetInt32()}", w.Attacker, IfMatch: "*");
        }),
        Audited("POST", "/fields/{fieldId}/choices", "Workspace.Field.Modified"),
        Audited("PUT", "/fields/{fieldId}/choices/{choiceId}", "Workspace.Field.Modified"),
        Custom("DELETE", "/fields/{fieldId}/choices/{choiceId}", "Workspace.Field.Modified", async w =>
        {
            var name = Unique("Delete probe ");
            var field = await SetupAsync(w, HttpMethod.Post, $"{W(w.A.WorkspaceId)}/fields/{w.A.Fields.Issues}/choices", new JsonObject { ["name"] = name }, "*");
            var choice = field.GetProperty("choices").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("choiceId").GetInt32();
            return new AuditRequest(HttpMethod.Delete, $"{W(w.A.WorkspaceId)}/fields/{w.A.Fields.Issues}/choices/{choice}", w.Attacker, IfMatch: "*");
        }),
        Audited("PUT", "/fields/{fieldId}/choice-order", "Workspace.Field.Modified"),
        Audited("POST", "/coding-layouts", "Workspace.CodingLayout.Created"),
        NoneWs("GET", "/coding-layouts/{layoutId}", ConfigurationRead),
        Audited("PUT", "/coding-layouts/{layoutId}", "Workspace.CodingLayout.Modified"),
        Custom("DELETE", "/coding-layouts/{layoutId}", "Workspace.CodingLayout.Deleted", async w =>
        {
            var layout = await w.JsonAsync(HttpMethod.Post, $"{W(w.A.WorkspaceId)}/coding-layouts", w.Attacker, HttpStatusCode.Created,
                new JsonObject { ["name"] = Unique("Delete probe "), ["isDefault"] = false, ["sections"] = new JsonArray() });
            return new AuditRequest(HttpMethod.Delete, $"{W(w.A.WorkspaceId)}/coding-layouts/{layout.GetProperty("layoutId").GetGuid()}", w.Attacker, IfMatch: "*");
        }),
        NoneWs("GET", "/dedupe-policy", ConfigurationRead),
        Audited("PUT", "/dedupe-policy", "Workspace.SettingsChanged"),
        Audited("POST", "/dedupe-runs", "Job.Created"),

        // Protected content through the gateway: every retrieval (display or prefetch), native download, print and view.
        Audited("GET", "/documents/{documentId}", "Document.Retrieved"),
        Audited("GET", "/documents/{documentId}/pages", "Document.Retrieved"),
        Audited("GET", "/documents/{documentId}/text/hits", "Document.Retrieved"),
        Audited("GET", "/documents/{documentId}/text/chunks/{chunkIndex}", "Document.Retrieved"),
        Audited("GET", "/documents/{documentId}/native", "Document.NativeDownloaded"),
        Audited("GET", "/documents/{documentId}/text", "Document.Retrieved"),
        Audited("GET", "/documents/{documentId}/pages/{pageNumber}/image", "Document.Printed", "document, print"),
        Audited("GET", "/documents/{documentId}/pages/{pageNumber}/thumbnail", "Document.Retrieved"),
        Audited("POST", "/documents/{documentId}/views", "Document.Viewed"),

        // Coding.
        NoneWs("GET", "/documents/{documentId}/coding", DocumentDataRead),
        Audited("PUT", "/documents/{documentId}/coding", "Coding.Changed"),
        Audited("POST", "/bulk-coding", "Coding.BulkSubmitted"),
        NoneWs("GET", "/documents/{documentId}/relationships", DocumentDataRead),
        NoneWs("POST", "/coding-propagations/preview", Preview),
        Audited("POST", "/coding-propagations", "Coding.FamilyApplied"),
        NoneWs("GET", "/privilege-conflicts", "the on-screen conflict list; its CSV export is audited"),
        Audited("GET", "/privilege-conflicts/export", "Privilege.ConflictReportExported"),
        CoveredBy("POST", Ws + "/privilege-conflicts/propagations", "Coding.FamilyApplied",
            nameof(Coding.PrivilegeConflictApiTests.Propagating_privilege_calls_to_duplicates_is_one_bulk_job_with_provenance_that_leaves_hidden_members_alone)),
        NoneWs("GET", "/bulk-coding/{jobId}/report", ListRead),
        NoneWs("GET", "/documents/{documentId}/coding-history", DocumentDataRead),

        // Review batches (E10-T05).
        Audited("POST", "/review-batch-sets", "Coding.ReviewBatchSet.Created"),
        NoneWs("GET", "/review-batch-sets", ListRead),
        NoneWs("GET", "/review-batch-sets/{batchSetId}", ListRead),
        NoneWs("GET", "/review-batch-sets/{batchSetId}/conflicts", DocumentDataRead),
        NoneWs("GET", "/review-batches", ListRead),
        NoneWs("GET", "/review-batches/{batchId}", ListRead),
        NoneWs("GET", "/review-batches/{batchId}/documents", ListRead),
        Audited("POST", "/review-batches/{batchId}/check-out", "Coding.ReviewBatch.CheckedOut"),
        Audited("POST", "/review-batches/{batchId}/check-in", "Coding.ReviewBatch.CheckedIn"),
        Audited("PUT", "/review-batches/{batchId}/assignment", "Coding.ReviewBatch.Assigned"),

        // Redactions (E11-T04).
        NoneWs("GET", "/redaction-sets", ConfigurationRead),
        Audited("POST", "/redaction-sets", "Redaction.RedactionSet.Created"),
        Audited("PUT", "/redaction-sets/{redactionSetId}", "Redaction.RedactionSet.Modified"),
        NoneWs("GET", "/redaction-reasons", ConfigurationRead),
        Audited("POST", "/redaction-reasons", "Redaction.Reason.Created"),
        Audited("PUT", "/redaction-reasons/{reasonCode}", "Redaction.Reason.Modified"),
        NoneWs("GET", "/documents/{documentId}/redaction-sets/{redactionSetId}", DocumentDataRead),
        NoneWs("GET", "/documents/{documentId}/redaction-sets/{redactionSetId}/history", DocumentDataRead),
        Audited("POST", "/documents/{documentId}/redaction-sets/{redactionSetId}/revisions", "Redaction.Added"),

        // Frozen sets: freezing runs the search.
        Audited("POST", "/snapshots", "Search.Executed"),
        NoneWs("GET", "/snapshots", ListRead),
        NoneWs("GET", "/snapshots/{snapshotId}", ListRead),

        // Imports.
        NoneWs("GET", "/import-targets", ConfigurationRead),
        NoneWs("GET", "/import-profiles", ConfigurationRead),
        NoneWs("POST", "/import-profiles", "a reusable mapping template; each import records the profile it ran with in Import.Started"),
        NoneWs("GET", "/import-profiles/{profileId}", ConfigurationRead),
        NoneWs("PUT", "/import-profiles/{profileId}", "a reusable mapping template; each import records the profile it ran with in Import.Started"),
        NoneWs("DELETE", "/import-profiles/{profileId}", "a reusable mapping template; each import records the profile it ran with in Import.Started"),
        NoneWs("POST", "/import-mapping-previews", Preview),
        Audited("POST", "/imports", "Import.Started"),
        Audited("POST", "/imports/preflight", "Import.PreflightRun"),
        NoneWs("GET", "/imports", ListRead),
        NoneWs("GET", "/imports/{importId}", ListRead),
        NoneWs("GET", "/imports/{importId}/errors", "row-level error summaries; the downloadable error file is audited"),
        Audited("GET", "/imports/{importId}/report", "Import.ReportDownloaded"),
        Audited("GET", "/imports/{importId}/report.csv", "Import.ReportDownloaded"),
        Audited("GET", "/imports/{importId}/error-file", "Import.ReportDownloaded"),
        Audited("GET", "/imports/preflight/{preflightId}/issues", "Import.ReportDownloaded"),
        NoneWs("GET", "/imports/{importId}/family-issues", ListRead),

        // Exports.
        Audited("POST", "/exports", "Export.Created"),
        NoneWs("GET", "/exports", ListRead),
        NoneWs("GET", "/exports/{exportId}", ListRead),
        NoneWs("GET", "/exports/{exportId}/exclusions", ListRead),
        NoneWs("GET", "/exports/{exportId}/files", "lists file names and sizes; downloading a file or the package is audited"),
        Audited("GET", "/exports/{exportId}/files/{fileId}/content", "Export.Downloaded"),
        Audited("GET", "/exports/{exportId}/package", "Export.Downloaded"),

        // Productions (production inclusion: the frozen set, designations and volumes).
        Audited("POST", "/productions", "Production.Created"),
        NoneWs("GET", "/productions", ListRead),
        NoneWs("GET", "/productions/bates-lookup", ListRead),
        NoneWs("GET", "/productions/{productionId}", ListRead),
        Audited("PUT", "/productions/{productionId}", "Production.Modified"),
        Audited("POST", "/productions/{productionId}/bates-allocation", "Job.Created"),
        CoveredBy("POST", Ws + "/productions/{productionId}/finalize", "Production.Finalized",
            nameof(Productions.ProductionLifecycleTests.A_production_is_allocated_family_adjacent_finalized_frozen_verified_and_superseded_by_a_new_version)),
        Audited("POST", "/productions/{productionId}/void", "Production.Voided"),
        Audited("POST", "/productions/{productionId}/discard", "Production.Discarded"),
        NoneWs("GET", "/productions/{productionId}/documents", ListRead),
        Audited("POST", "/productions/{productionId}/verification", "Production.Verified"),
        CoveredBy("POST", Ws + "/productions/{productionId}/qc", "Production.QcRun",
            nameof(Productions.ProductionQcGateTests.Each_check_reports_its_exceptions_failures_block_and_authorized_overrides_are_audited_printed_and_retained)),
        NoneWs("GET", "/productions/{productionId}/qc", ListRead),
        NoneWs("GET", "/productions/{productionId}/qc/exceptions", ListRead),
        Audited("GET", "/productions/{productionId}/qc/report", "Production.Downloaded", probe: "finalized production, CSV"),
        NoneWs("GET", "/productions/{productionId}/designations", ListRead),
        Audited("PUT", "/productions/{productionId}/designation-overrides/{documentId}", "Production.DesignationOverridden"),
        Audited("DELETE", "/productions/{productionId}/designation-overrides/{documentId}", "Production.DesignationOverrideRemoved"),
        NoneWs("GET", "/productions/{productionId}/redesignation-report", "the on-screen report; its overlay download is audited"),
        Audited("GET", "/productions/{productionId}/redesignation-overlay", "Production.RedesignationExported"),
        CoveredBy("POST", Ws + "/productions/{productionId}/volumes", "Production.Run",
            nameof(Productions.ProductionVolumeTests.A_finalized_production_is_written_with_burned_redactions_endorsements_placeholders_and_load_files_reproducibly)),
        NoneWs("GET", "/productions/{productionId}/volumes", ListRead),
        NoneWs("GET", "/productions/{productionId}/volumes/{volumeId}", ListRead),
        NoneWs("GET", "/productions/{productionId}/volumes/{volumeId}/files", "lists file names and sizes; downloading a file or the package is audited"),
        Audited("GET", "/productions/{productionId}/volumes/{volumeId}/files/{fileId}/content", "Production.Downloaded"),
        Audited("GET", "/productions/{productionId}/volumes/{volumeId}/package", "Production.Downloaded"),
        Audited("GET", "/productions/{productionId}/volumes/{volumeId}/verification-report", "Production.Downloaded"),

        // Outside a workspace.
        Scenario("POST", V1 + "/workspaces", "Workspace.Created"),
        None("GET", V1 + "/workspaces", "lists the caller's own memberships"),
        None("GET", V1 + "/me", "the caller's own profile"),
        None("GET", V1 + "/me/preferences", Personal),
        None("PUT", V1 + "/me/preferences/{key}", Personal),
        None("DELETE", V1 + "/me/preferences/{key}", Personal),
        None("GET", V1 + "/workspace-deletions", ListRead),
        None("GET", V1 + "/workspace-deletions/{deletionId}", ListRead),
        Scenario("POST", V1 + "/workspace-deletions/{deletionId}/approve", "Workspace.DeletionApproved"),
        Scenario("POST", V1 + "/workspace-deletions/{deletionId}/cancel", "Workspace.DeletionCancelled"),
        None("GET", V1 + "/workspace-deletions/{deletionId}/certificate", "the certificate of a completed deletion; its creation is the audited Deleted event"),
        CoveredBy("GET", "/bff/login", "Auth.SignIn",
            nameof(Auth.SessionAuthenticationTests.Claims_map_to_the_principal_and_a_user_keyed_by_issuer_and_subject)),
        CoveredBy("POST", "/bff/logout", "Auth.SignOut", nameof(Auth.SessionAuthenticationTests.Logout_revokes_the_session_and_returns_the_idp_end_session_url)),
        CoveredBy("POST", "/bff/backchannel-logout", "Auth.SessionRevoked",
            nameof(Auth.SessionAuthenticationTests.Back_channel_logout_invalidates_the_server_session_immediately)),
        None("*", "/health/live", "anonymous liveness probe"),
        None("*", "/health/ready", "anonymous readiness probe"),
        None("GET", "/openapi/{documentName}.json", "the public API description"),
    ];

    /// <summary>A setup request of a custom probe as the attacker; it must succeed.</summary>
    private static async Task<JsonElement> SetupAsync(AttackWorld w, HttpMethod method, string url, JsonNode body, string? ifMatch)
    {
        using var response = await w.SendAsync(method, url, w.Attacker, J(body), ifMatch);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeInRange(200, 299, "{0} {1}: {2}", method, url, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>The probe of <see cref="RouteAttackCatalog"/> a <see cref="AuditCoverage.Probe"/> case sends.</summary>
    public static RouteProbe AttackProbe(AuditCase auditCase)
    {
        ArgumentNullException.ThrowIfNull(auditCase);
        var route = RouteAttackCatalog.Cases.Single(c => c.Key == auditCase.Key);
        return auditCase.ProbeName is null ? route.Probes[0] : route.Probes.Single(p => p.Name == auditCase.ProbeName);
    }
}
