using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Opportunity.IntegrationTests.Security;

/// <summary>What a foreign identifier must produce, compared with the same request naming an identifier that exists nowhere.</summary>
internal enum ForeignExpectation
{
    /// <summary>The same 404 problem as for the unknown identifier (no existence oracle).</summary>
    NotFound,

    /// <summary>The same non-2xx answer as for the unknown identifier (e.g. a validation problem naming the parameter).</summary>
    SameAsUnknown,

    /// <summary>A set-valued request (frozen set, search): the same 2xx as for the unknown identifier, holding nothing.</summary>
    EmptySet,
}

/// <summary>A §24 protected operation (or another resource class) a route case attacks; the coverage report groups by it.</summary>
internal static class ProtectedOperation
{
    public const string Open = "Open document (metadata, page list)";
    public const string View = "View (text, text chunks, page images, thumbnails, review-mode prefetch)";
    public const string NativeDownload = "Native download";
    public const string ImageRetrieval = "Image retrieval (page image, print)";
    public const string Export = "Export (create, read, files, package download)";
    public const string ProductionInclusion = "Production inclusion";
    public const string Coding = "Coding (interactive read/write, bulk coding)";
    public const string Search = "Search hits, counts, facets, handles and cursors";
    public const string SavedSearch = "Saved searches and their folders";
    public const string TermReport = "Search term reports (counts, export, term hit sets)";
    public const string GridView = "Document-list views and layouts";
    public const string HighlightSet = "Highlight Sets and highlighting toggles";
    public const string Redaction = "Redactions, Redaction Sets and reasons";
    public const string ReviewBatch = "Review Batch Sets, batches, check-out/in, assignment and QC conflicts";
    public const string Snapshot = "Frozen sets (snapshots)";
    public const string Import = "Import jobs, reports and profiles";
    public const string Job = "Job status";
    public const string Workspace = "Workspace administration and catalogues";
    public const string PrivilegeLog = "Privilege logs (templates, versions, rows, files)";

    public static IReadOnlyList<string> Section24 { get; } = [Open, View, NativeDownload, ImageRetrieval, Export, ProductionInclusion];
}

/// <summary>
/// One request shape of a route: built from the caller's own workspace (always the URL's workspace) and a target
/// identifier set (own, foreign or unknown) whose identifiers are substituted into path, query or body.
/// <see cref="OwnStatus"/> is the answer for the caller's own identifiers (null: anything PEP-1 admitted).
/// </summary>
internal sealed record RouteProbe(
    string Name,
    HttpMethod Method,
    Func<WorkspaceResources, WorkspaceResources, string> Url,
    HttpStatusCode? OwnStatus,
    Func<WorkspaceResources, WorkspaceResources, HttpContent?>? Body = null,
    string? IfMatch = null,
    bool HasForeignIdentifier = true,
    ForeignExpectation Expectation = ForeignExpectation.NotFound);

/// <summary>A route of the endpoint data source with its probes, or the reason it carries no workspace-scoped identifier.</summary>
internal sealed record RouteCase(string Method, string Pattern, string Operation, IReadOnlyList<RouteProbe> Probes, string? NoWorkspaceIdentifier = null)
{
    public string Key => $"{Method} {Pattern}";
}

/// <summary>
/// Every route the API host maps (the suite fails when the endpoint data source has a route that is not listed here):
/// each <c>{workspaceId}</c> route with probes that substitute another workspace's identifiers into every identifier
/// position (path, query, body, cursor, search handle), and the remaining routes with the reason they take none.
/// </summary>
internal static class RouteAttackCatalog
{
    private const string V1 = "/api/v1";
    private const string Ws = V1 + "/workspaces/{workspaceId}";

    private static string W(WorkspaceResources own) => $"{V1}/workspaces/{own.WorkspaceId}";

    private static string Doc(WorkspaceResources own, WorkspaceResources t) => $"{W(own)}/documents/{t.DocumentId}";

    private static StringContent J(JsonNode node) => AttackWorld.Json(node);

    private static RouteProbe WorkspaceOnly(HttpMethod method, string suffix, HttpStatusCode own, Func<WorkspaceResources, HttpContent?>? body = null) =>
        new RouteProbe("workspace", method, (o, _) => W(o) + suffix, own, body is null ? null : (o, _) => body(o), HasForeignIdentifier: false);

    private static RouteCase Case(string method, string pattern, string operation, params RouteProbe[] probes) =>
        new(method, pattern, operation, probes);

    /// <summary>A draft production request with a source and a Bates prefix unique to the request.</summary>
    private static JsonObject ProductionBody(JsonObject source)
    {
        source["specification"] = new JsonObject { ["bates"] = new JsonObject { ["prefix"] = "P" + Guid.NewGuid().ToString("N")[..8] } };
        return source;
    }

    /// <summary>One small redaction added on page 1.</summary>
    private static JsonObject RedactionBody() => new()
    {
        ["changes"] = new JsonArray(new JsonObject
        {
            ["operation"] = "add",
            ["pageNumber"] = 1,
            ["type"] = "black",
            ["reasonCode"] = "Other",
            ["rect"] = new JsonObject { ["x"] = 100_000, ["y"] = 100_000, ["w"] = 100_000, ["h"] = 100_000 },
        }),
    };

    public static IReadOnlyList<RouteCase> Cases { get; } =
    [
        // Search: query text, handles and cursors.
        Case("POST", Ws + "/query-validations", ProtectedOperation.Search,
            WorkspaceOnly(HttpMethod.Post, "/query-validations", HttpStatusCode.OK, _ => J(new JsonObject { ["query"] = "memo" }))),
        Case("POST", Ws + "/searches", ProtectedOperation.Search,
            new RouteProbe("query names another workspace's control number", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["query"] = $"controlNumber:\"{t.ControlNumber}\"", ["facets"] = new JsonArray("fileType") }),
                Expectation: ForeignExpectation.EmptySet),
            new RouteProbe("saved search in the body", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["savedSearchId"] = t.SavedSearchId.ToString() })),
            new RouteProbe("query references another workspace's saved search", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["query"] = $"savedsearch:{t.SavedSearchId}" }), Expectation: ForeignExpectation.SameAsUnknown),
            new RouteProbe("search term report term in the body", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["searchTermReportId"] = t.TermReportId.ToString(), ["termId"] = t.TermId.ToString() })),
            new RouteProbe("own report, another workspace's term", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (o, t) => J(new JsonObject { ["searchTermReportId"] = o.TermReportId.ToString(), ["termId"] = t.TermId.ToString() }))),
        Case("GET", Ws + "/searches/{searchId}/pages", ProtectedOperation.Search,
            new RouteProbe("search handle and its cursor", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{t.SearchId:N}/pages?cursor={t.SearchCursor}", HttpStatusCode.OK),
            new RouteProbe("own handle, another workspace's cursor", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{o.SearchId:N}/pages?cursor={t.SearchCursor}", HttpStatusCode.OK),
            new RouteProbe("search handle, page jump", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{t.SearchId:N}/pages?page=1", HttpStatusCode.OK)),
        Case("GET", Ws + "/query-history", ProtectedOperation.Search, WorkspaceOnly(HttpMethod.Get, "/query-history", HttpStatusCode.OK)),
        Case("GET", Ws + "/search-freshness", ProtectedOperation.Search, WorkspaceOnly(HttpMethod.Get, "/search-freshness", HttpStatusCode.OK)),

        // Saved searches and their folders (E07-T09).
        // Document security administration (E05-T06): classes and field restrictions are addressed by workspace-local keys.
        Case("GET", Ws + "/security/restriction-classes", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/security/restriction-classes", HttpStatusCode.OK)),
        Case("PUT", Ws + "/security/restriction-classes/{classKey}", ProtectedOperation.Workspace,
            new RouteProbe("workspace-local class key", HttpMethod.Put,
                (o, _) => W(o) + "/security/restriction-classes/Probe" + Guid.NewGuid().ToString("N")[..8], HttpStatusCode.Created,
                (_, _) => J(new JsonObject { ["displayName"] = "Probe class", ["roles"] = new JsonArray(), ["rules"] = new JsonArray() }),
                HasForeignIdentifier: false)),
        Case("DELETE", Ws + "/security/restriction-classes/{classKey}", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Delete, "/security/restriction-classes/Privileged", HttpStatusCode.PreconditionRequired)),
        Case("GET", Ws + "/security/walls", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/security/walls", HttpStatusCode.OK)),
        Case("POST", Ws + "/security/walls", ProtectedOperation.Workspace,
            new RouteProbe("document ids in the scope", HttpMethod.Post, (o, _) => W(o) + "/security/walls", HttpStatusCode.Created,
                (o, t) => J(new JsonObject
                {
                    ["name"] = "Probe wall " + Guid.NewGuid().ToString("N"),
                    ["members"] = new JsonObject { ["userIds"] = new JsonArray(Guid.CreateVersion7().ToString()), ["groups"] = new JsonArray() },
                    ["scope"] = new JsonObject
                    {
                        ["documentIds"] = new JsonArray(t.DocumentId.ToString()), ["custodians"] = new JsonArray(), ["choices"] = new JsonArray(),
                    },
                }), Expectation: ForeignExpectation.SameAsUnknown)),
        Case("GET", Ws + "/security/walls/{wallId}", ProtectedOperation.Workspace,
            new RouteProbe("wall", HttpMethod.Get, (o, t) => $"{W(o)}/security/walls/{t.WallId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/security/walls/{wallId}", ProtectedOperation.Workspace,
            new RouteProbe("wall", HttpMethod.Put, (o, t) => $"{W(o)}/security/walls/{t.WallId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject
                {
                    ["name"] = "Wall " + o.Name,
                    ["members"] = new JsonObject { ["userIds"] = new JsonArray(Guid.CreateVersion7().ToString()), ["groups"] = new JsonArray() },
                    ["scope"] = new JsonObject { ["documentIds"] = new JsonArray(), ["custodians"] = new JsonArray("nobody"), ["choices"] = new JsonArray() },
                }), IfMatch: "*")),
        Case("DELETE", Ws + "/security/walls/{wallId}", ProtectedOperation.Workspace,
            new RouteProbe("wall", HttpMethod.Delete, (o, t) => $"{W(o)}/security/walls/{t.SpareWallId}", HttpStatusCode.NoContent, IfMatch: "*")),
        Case("GET", Ws + "/security/field-restrictions", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/security/field-restrictions", HttpStatusCode.OK)),
        Case("PUT", Ws + "/security/field-restrictions/{fieldId}", ProtectedOperation.Workspace,
            new RouteProbe("workspace-local field id", HttpMethod.Put, (o, _) => $"{W(o)}/security/field-restrictions/{o.Fields.ReviewDate}", null,
                (_, _) => J(new JsonObject { ["visibleTo"] = new JsonArray("WorkspaceAdmin"), ["editableBy"] = new JsonArray("WorkspaceAdmin") }),
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("DELETE", Ws + "/security/field-restrictions/{fieldId}", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Delete, "/security/field-restrictions/999999", HttpStatusCode.NotFound)),
        Case("GET", Ws + "/security/break-glass/activations", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/security/break-glass/activations", HttpStatusCode.OK)),
        Case("POST", Ws + "/security/break-glass/activations", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/security/break-glass/activations", HttpStatusCode.Forbidden,
                _ => J(new JsonObject { ["reason"] = "Attack suite probe (no MFA step-up)" }))),
        Case("POST", Ws + "/security/break-glass/activations/{activationId}/end", ProtectedOperation.Workspace,
            new RouteProbe("activation", HttpMethod.Post, (o, t) => $"{W(o)}/security/break-glass/activations/{t.BreakGlassActivationId}/end", HttpStatusCode.OK)),

        // Highlight Sets (E16-T12).
        Case("GET", Ws + "/highlight-sets", ProtectedOperation.HighlightSet, WorkspaceOnly(HttpMethod.Get, "/highlight-sets", HttpStatusCode.OK)),
        Case("POST", Ws + "/highlight-sets", ProtectedOperation.HighlightSet,
            WorkspaceOnly(HttpMethod.Post, "/highlight-sets", HttpStatusCode.Created, _ => J(new JsonObject
            {
                ["name"] = "Probe " + Guid.NewGuid().ToString("N"), ["color"] = "blue", ["terms"] = new JsonArray(new JsonObject { ["expression"] = "memo" }),
            }))),
        Case("GET", Ws + "/highlight-sets/{highlightSetId}", ProtectedOperation.HighlightSet,
            new RouteProbe("highlight set", HttpMethod.Get, (o, t) => $"{W(o)}/highlight-sets/{t.HighlightSetId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/highlight-sets/{highlightSetId}", ProtectedOperation.HighlightSet,
            new RouteProbe("highlight set", HttpMethod.Put, (o, t) => $"{W(o)}/highlight-sets/{t.HighlightSetId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject
                {
                    ["name"] = "Key terms " + o.Name, ["color"] = "violet", ["terms"] = new JsonArray(new JsonObject { ["expression"] = "memo OR terminat*" }),
                }), IfMatch: "*")),
        Case("DELETE", Ws + "/highlight-sets/{highlightSetId}", ProtectedOperation.HighlightSet,
            new RouteProbe("highlight set", HttpMethod.Delete, (o, t) => $"{W(o)}/highlight-sets/{t.SpareHighlightSetId}", HttpStatusCode.NoContent, IfMatch: "*")),
        Case("GET", Ws + "/highlight-set-selection", ProtectedOperation.HighlightSet,
            WorkspaceOnly(HttpMethod.Get, "/highlight-set-selection", HttpStatusCode.OK)),
        Case("PUT", Ws + "/highlight-set-selection", ProtectedOperation.HighlightSet,
            new RouteProbe("highlight set ids in the body", HttpMethod.Put, (o, _) => W(o) + "/highlight-set-selection", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["disabledSetIds"] = new JsonArray(t.HighlightSetId.ToString()), ["searchHits"] = true }),
                HasForeignIdentifier: false)),

        Case("GET", Ws + "/saved-search-folders", ProtectedOperation.SavedSearch, WorkspaceOnly(HttpMethod.Get, "/saved-search-folders", HttpStatusCode.OK)),
        Case("POST", Ws + "/saved-search-folders", ProtectedOperation.SavedSearch,
            WorkspaceOnly(HttpMethod.Post, "/saved-search-folders", HttpStatusCode.Created, _ => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N") })),
            new RouteProbe("parent folder in the body", HttpMethod.Post, (o, _) => W(o) + "/saved-search-folders", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N"), ["parentFolderId"] = t.SavedSearchFolderId.ToString() }),
                Expectation: ForeignExpectation.SameAsUnknown)),
        Case("PUT", Ws + "/saved-search-folders/{folderId}", ProtectedOperation.SavedSearch,
            new RouteProbe("folder", HttpMethod.Put, (o, t) => $"{W(o)}/saved-search-folders/{t.SavedSearchFolderId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject { ["name"] = "Renamed folder " + o.Name }), IfMatch: "*")),
        Case("DELETE", Ws + "/saved-search-folders/{folderId}", ProtectedOperation.SavedSearch,
            new RouteProbe("folder", HttpMethod.Delete, (o, t) => $"{W(o)}/saved-search-folders/{t.SpareFolderId}", HttpStatusCode.NoContent)),
        Case("GET", Ws + "/saved-searches", ProtectedOperation.SavedSearch,
            WorkspaceOnly(HttpMethod.Get, "/saved-searches", HttpStatusCode.OK),
            new RouteProbe("folder filter", HttpMethod.Get, (o, t) => $"{W(o)}/saved-searches?folderId={t.SavedSearchFolderId}", HttpStatusCode.OK,
                Expectation: ForeignExpectation.EmptySet)),
        Case("GET", Ws + "/saved-searches/share-candidates", ProtectedOperation.SavedSearch,
            WorkspaceOnly(HttpMethod.Get, "/saved-searches/share-candidates", HttpStatusCode.OK)),
        Case("POST", Ws + "/saved-searches", ProtectedOperation.SavedSearch,
            new RouteProbe("folder in the body", HttpMethod.Post, (o, _) => W(o) + "/saved-searches", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["name"] = "Probe", ["query"] = "memo", ["folderId"] = t.SavedSearchFolderId.ToString() }),
                Expectation: ForeignExpectation.SameAsUnknown),
            new RouteProbe("nested saved search in the query", HttpMethod.Post, (o, _) => W(o) + "/saved-searches", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["name"] = "Probe", ["query"] = $"savedsearch:{t.SavedSearchId}" }),
                Expectation: ForeignExpectation.SameAsUnknown)),
        Case("GET", Ws + "/saved-searches/{savedSearchId}", ProtectedOperation.SavedSearch,
            new RouteProbe("saved search", HttpMethod.Get, (o, t) => $"{W(o)}/saved-searches/{t.SavedSearchId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/saved-searches/{savedSearchId}", ProtectedOperation.SavedSearch,
            new RouteProbe("saved search", HttpMethod.Put, (o, t) => $"{W(o)}/saved-searches/{t.SavedSearchId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject { ["name"] = "Renamed search " + o.Name, ["query"] = "", ["folderId"] = o.SavedSearchFolderId.ToString() }),
                IfMatch: "*")),
        Case("DELETE", Ws + "/saved-searches/{savedSearchId}", ProtectedOperation.SavedSearch,
            new RouteProbe("saved search", HttpMethod.Delete, (o, t) => $"{W(o)}/saved-searches/{t.SpareSavedSearchId}", HttpStatusCode.NoContent, IfMatch: "*")),
        Case("POST", Ws + "/saved-searches/{savedSearchId}/clone", ProtectedOperation.SavedSearch,
            new RouteProbe("saved search", HttpMethod.Post, (o, t) => $"{W(o)}/saved-searches/{t.SavedSearchId}/clone", HttpStatusCode.Created,
                (_, _) => J(new JsonObject()))),
        Case("PUT", Ws + "/saved-searches/{savedSearchId}/sharing", ProtectedOperation.SavedSearch,
            new RouteProbe("saved search", HttpMethod.Put, (o, t) => $"{W(o)}/saved-searches/{t.SavedSearchId}/sharing", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["sharedWith"] = new JsonArray() }))),

        // Document-list views and the caller's layout (E16-T09).
        Case("GET", Ws + "/grid-views", ProtectedOperation.GridView, WorkspaceOnly(HttpMethod.Get, "/grid-views", HttpStatusCode.OK)),
        Case("POST", Ws + "/grid-views", ProtectedOperation.GridView,
            WorkspaceOnly(HttpMethod.Post, "/grid-views", HttpStatusCode.Created, _ => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N") }))),
        Case("GET", Ws + "/grid-views/layout", ProtectedOperation.GridView, WorkspaceOnly(HttpMethod.Get, "/grid-views/layout", HttpStatusCode.OK)),
        Case("PUT", Ws + "/grid-views/layout", ProtectedOperation.GridView,
            new RouteProbe("view in the body", HttpMethod.Put, (o, _) => W(o) + "/grid-views/layout", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["viewId"] = t.GridViewId.ToString() }), Expectation: ForeignExpectation.SameAsUnknown)),
        Case("GET", Ws + "/grid-views/{viewId}", ProtectedOperation.GridView,
            new RouteProbe("view", HttpMethod.Get, (o, t) => $"{W(o)}/grid-views/{t.GridViewId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/grid-views/{viewId}", ProtectedOperation.GridView,
            new RouteProbe("view", HttpMethod.Put, (o, t) => $"{W(o)}/grid-views/{t.GridViewId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject { ["name"] = "Renamed view " + o.Name, ["visibility"] = "shared" }), IfMatch: "*")),
        Case("DELETE", Ws + "/grid-views/{viewId}", ProtectedOperation.GridView,
            new RouteProbe("view", HttpMethod.Delete, (o, t) => $"{W(o)}/grid-views/{t.SpareGridViewId}", HttpStatusCode.NoContent, IfMatch: "*")),
        Case("POST", Ws + "/query-history", ProtectedOperation.Search,
            WorkspaceOnly(HttpMethod.Post, "/query-history", HttpStatusCode.NoContent, _ => J(new JsonObject { ["query"] = "memo" }))),

        // Search term reports (E07-T10). The rerun probe comes after every probe that needs the report completed.
        Case("POST", Ws + "/search-term-reports", ProtectedOperation.TermReport,
            new RouteProbe("snapshot scope in the body", HttpMethod.Post, (o, _) => W(o) + "/search-term-reports", HttpStatusCode.Accepted,
                (_, t) => J(new JsonObject
                {
                    ["name"] = "Probe",
                    ["terms"] = new JsonArray(new JsonObject { ["name"] = "Memo", ["expression"] = "memo" }),
                    ["scope"] = new JsonObject { ["kind"] = "snapshot", ["id"] = t.ExportSnapshotId.ToString() },
                })),
            new RouteProbe("saved search scope in the body", HttpMethod.Post, (o, _) => W(o) + "/search-term-reports", HttpStatusCode.Accepted,
                (_, t) => J(new JsonObject
                {
                    ["name"] = "Probe",
                    ["termsCsv"] = "Name,Expression\r\nMemo,memo",
                    ["scope"] = new JsonObject { ["kind"] = "savedSearch", ["id"] = t.SavedSearchId.ToString() },
                }))),
        Case("GET", Ws + "/search-term-reports", ProtectedOperation.TermReport, WorkspaceOnly(HttpMethod.Get, "/search-term-reports", HttpStatusCode.OK)),
        Case("GET", Ws + "/search-term-reports/{reportId}", ProtectedOperation.TermReport,
            new RouteProbe("report", HttpMethod.Get, (o, t) => $"{W(o)}/search-term-reports/{t.TermReportId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/search-term-reports/{reportId}/export", ProtectedOperation.TermReport,
            new RouteProbe("report as CSV", HttpMethod.Get, (o, t) => $"{W(o)}/search-term-reports/{t.TermReportId}/export?format=csv", HttpStatusCode.OK),
            new RouteProbe("report as XLSX", HttpMethod.Get, (o, t) => $"{W(o)}/search-term-reports/{t.TermReportId}/export?format=xlsx", HttpStatusCode.OK)),
        Case("POST", Ws + "/search-term-reports/{reportId}/rerun", ProtectedOperation.TermReport,
            new RouteProbe("report", HttpMethod.Post, (o, t) => $"{W(o)}/search-term-reports/{t.TermReportId}/rerun", HttpStatusCode.Accepted)),
        Case("DELETE", Ws + "/search-term-reports/{reportId}", ProtectedOperation.TermReport,
            new RouteProbe("report", HttpMethod.Delete, (o, t) => $"{W(o)}/search-term-reports/{t.SpareTermReportId}", HttpStatusCode.NoContent)),

        // Jobs.
        Case("GET", Ws + "/jobs/{jobId}", ProtectedOperation.Job,
            new RouteProbe("import job", HttpMethod.Get, (o, t) => $"{W(o)}/jobs/{t.ImportJobId}", HttpStatusCode.OK),
            new RouteProbe("bulk coding job", HttpMethod.Get, (o, t) => $"{W(o)}/jobs/{t.BulkCodingJobId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/jobs", ProtectedOperation.Job, WorkspaceOnly(HttpMethod.Get, "/jobs?createdBy=all&limit=500", HttpStatusCode.OK)),
        Case("GET", Ws + "/jobs/{jobId}/failures", ProtectedOperation.Job,
            new RouteProbe("import job", HttpMethod.Get, (o, t) => $"{W(o)}/jobs/{t.ImportJobId}/failures", HttpStatusCode.OK)),
        Case("POST", Ws + "/jobs/{jobId}/cancel", ProtectedOperation.Job,
            new RouteProbe("finished import job", HttpMethod.Post, (o, t) => $"{W(o)}/jobs/{t.ImportJobId}/cancel", HttpStatusCode.Conflict)),
        Case("POST", Ws + "/jobs/{jobId}/retry-failed", ProtectedOperation.Job,
            new RouteProbe("import job (nothing failed)", HttpMethod.Post, (o, t) => $"{W(o)}/jobs/{t.ImportJobId}/retry-failed", HttpStatusCode.Accepted)),
        Case("GET", Ws + "/search-outbox/failures", ProtectedOperation.Job, WorkspaceOnly(HttpMethod.Get, "/search-outbox/failures", HttpStatusCode.OK)),
        Case("POST", Ws + "/search-outbox/retry-failed", ProtectedOperation.Job,
            WorkspaceOnly(HttpMethod.Post, "/search-outbox/retry-failed", HttpStatusCode.Accepted)),
        // The suite runs the stream with a 1 s maximum duration (AttackWorld), so the probe reads a complete response.
        Case("GET", Ws + "/job-events", ProtectedOperation.Job, WorkspaceOnly(HttpMethod.Get, "/job-events", HttpStatusCode.OK)),

        // Search index operations (E07-T11): the workspace is the only identifier; a start answers 202, or 409 while an
        // earlier probe's reindex is still in flight (no coordinator runs in the attack world).
        Case("GET", Ws + "/search-index", ProtectedOperation.Job, WorkspaceOnly(HttpMethod.Get, "/search-index", HttpStatusCode.OK)),
        Case("POST", Ws + "/search-index/reindexes", ProtectedOperation.Job,
            new RouteProbe("workspace", HttpMethod.Post, (o, _) => W(o) + "/search-index/reindexes", null, (_, _) => J(new JsonObject()),
                HasForeignIdentifier: false)),

        // Workspace administration and catalogues (the workspace is the only identifier).
        Case("GET", Ws, ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, string.Empty, HttpStatusCode.OK)),
        Case("PUT", Ws, ProtectedOperation.Workspace,
            new RouteProbe("workspace", HttpMethod.Put, (o, _) => W(o), null, (o, _) => J(new JsonObject { ["name"] = "Workspace " + o.Name }),
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("GET", Ws + "/members", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/members", HttpStatusCode.OK)),

        // Role administration (E05-T08). A user id is installation-level (one user may hold roles in many workspaces) and a
        // group is an IdP name, so neither is an identifier another workspace could leak; an unknown user is a validation error.
        Case("GET", Ws + "/roles", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/roles", HttpStatusCode.OK)),
        Case("GET", Ws + "/role-assignments", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/role-assignments", HttpStatusCode.OK)),
        Case("GET", Ws + "/role-assignments/candidates", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/role-assignments/candidates?q=zz-attack-probe", HttpStatusCode.OK)),
        Case("PUT", Ws + "/role-assignments/users/{userId}", ProtectedOperation.Workspace,
            new RouteProbe("installation-level user id", HttpMethod.Put, (o, _) => $"{W(o)}/role-assignments/users/{Guid.CreateVersion7()}",
                HttpStatusCode.BadRequest, (_, _) => J(new JsonObject { ["roles"] = new JsonArray("Reviewer") }), IfMatch: "*",
                HasForeignIdentifier: false)),
        Case("PUT", Ws + "/role-assignments/groups", ProtectedOperation.Workspace,
            new RouteProbe("IdP group name", HttpMethod.Put, (o, _) => W(o) + "/role-assignments/groups?name=cn%3Dattack-probe", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["roles"] = new JsonArray() }), IfMatch: "*", HasForeignIdentifier: false)),
        // Legal holds (E20-T01). The world's lock is released, so release steps answer 409 for its own workspace.
        // Workspace deletion (E20-T02): the workspace-scoped request side; the installation-level status routes are exempt below.
        Case("GET", Ws + "/deletions", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/deletions", HttpStatusCode.OK)),
        Case("POST", Ws + "/deletions", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/deletions", HttpStatusCode.BadRequest, _ => J(new JsonObject { ["reason"] = " " }))),
        Case("GET", Ws + "/preservation-locks", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/preservation-locks", HttpStatusCode.OK)),
        Case("POST", Ws + "/preservation-locks", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/preservation-locks", HttpStatusCode.BadRequest, _ => J(new JsonObject { ["reason"] = " " }))),
        Case("GET", Ws + "/preservation-locks/{lockId}", ProtectedOperation.Workspace,
            new RouteProbe("preservation lock", HttpMethod.Get, (o, t) => $"{W(o)}/preservation-locks/{t.PreservationLockId}", HttpStatusCode.OK)),
        Case("POST", Ws + "/preservation-locks/{lockId}/release", ProtectedOperation.Workspace,
            new RouteProbe("preservation lock", HttpMethod.Post, (o, t) => $"{W(o)}/preservation-locks/{t.PreservationLockId}/release",
                HttpStatusCode.Conflict, (_, _) => J(new JsonObject { ["reason"] = "probe" }), IfMatch: "*")),
        Case("POST", Ws + "/preservation-locks/{lockId}/release/approve", ProtectedOperation.Workspace,
            new RouteProbe("preservation lock", HttpMethod.Post, (o, t) => $"{W(o)}/preservation-locks/{t.PreservationLockId}/release/approve",
                HttpStatusCode.Conflict, IfMatch: "*")),
        Case("POST", Ws + "/preservation-locks/{lockId}/release/cancel", ProtectedOperation.Workspace,
            new RouteProbe("preservation lock", HttpMethod.Post, (o, t) => $"{W(o)}/preservation-locks/{t.PreservationLockId}/release/cancel",
                HttpStatusCode.Conflict, IfMatch: "*")),
        // Acknowledgments (E20-T03). Versions are workspace-local numbers (every workspace has a version 1 once it publishes),
        // not identifiers of another workspace. The world publishes no text, so nothing gates the other probes: publishing
        // is probed with an invalid body and version 1 does not exist.
        Case("GET", Ws + "/acknowledgment", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/acknowledgment", HttpStatusCode.OK)),
        Case("POST", Ws + "/acknowledgment/acceptances", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/acknowledgment/acceptances", HttpStatusCode.Conflict,
                _ => J(new JsonObject { ["version"] = 1, ["textSha256"] = new string('a', 64) }))),
        Case("GET", Ws + "/acknowledgment-versions", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/acknowledgment-versions", HttpStatusCode.OK)),
        Case("POST", Ws + "/acknowledgment-versions", ProtectedOperation.Workspace,
            new RouteProbe("workspace", HttpMethod.Post, (o, _) => W(o) + "/acknowledgment-versions", HttpStatusCode.BadRequest,
                (_, _) => J(new JsonObject { ["title"] = " ", ["text"] = "probe" }), IfMatch: "*", HasForeignIdentifier: false)),
        Case("GET", Ws + "/acknowledgment-versions/{version}", ProtectedOperation.Workspace,
            new RouteProbe("version", HttpMethod.Get, (o, _) => W(o) + "/acknowledgment-versions/1", HttpStatusCode.NotFound, HasForeignIdentifier: false)),
        Case("GET", Ws + "/acknowledgment-roster", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/acknowledgment-roster", HttpStatusCode.OK)),
        Case("GET", Ws + "/acknowledgment-roster/export", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Get, "/acknowledgment-roster/export", HttpStatusCode.OK)),
        Case("GET", Ws + "/fields", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/fields", HttpStatusCode.OK)),
        Case("GET", Ws + "/coding-layouts", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/coding-layouts", HttpStatusCode.OK)),

        // Field and coding layout administration (E04-T06). Field and choice ids are workspace-local numbers (every
        // workspace has field 1000), not identifiers of another workspace; layouts are addressed by a global id.
        Case("GET", Ws + "/field-capacity", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/field-capacity", HttpStatusCode.OK)),
        Case("POST", Ws + "/fields", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/fields", HttpStatusCode.Created, _ => J(new JsonObject
            {
                ["displayName"] = "Probe " + Guid.NewGuid().ToString("N")[..8], ["type"] = "keyword", ["storage"] = "coding",
            }))),
        Case("GET", Ws + "/fields/{fieldId}", ProtectedOperation.Workspace,
            new RouteProbe("field", HttpMethod.Get, (o, _) => $"{W(o)}/fields/{o.Fields.Issues}", HttpStatusCode.OK, HasForeignIdentifier: false)),
        Case("PUT", Ws + "/fields/{fieldId}", ProtectedOperation.Workspace,
            new RouteProbe("field", HttpMethod.Put, (o, _) => $"{W(o)}/fields/{o.Fields.Notes}", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["displayName"] = "Reviewer Notes", ["description"] = "Probe", ["isHidden"] = false, ["type"] = "text" }),
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("DELETE", Ws + "/fields/{fieldId}", ProtectedOperation.Workspace,
            new RouteProbe("system field", HttpMethod.Delete, (o, _) => $"{W(o)}/fields/1", HttpStatusCode.Conflict, IfMatch: "*", HasForeignIdentifier: false)),
        Case("POST", Ws + "/fields/{fieldId}/choices", ProtectedOperation.Workspace,
            new RouteProbe("field", HttpMethod.Post, (o, _) => $"{W(o)}/fields/{o.Fields.Issues}/choices", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N")[..8] }), IfMatch: "*", HasForeignIdentifier: false)),
        Case("PUT", Ws + "/fields/{fieldId}/choices/{choiceId}", ProtectedOperation.Workspace,
            new RouteProbe("choice", HttpMethod.Put, (o, _) => $"{W(o)}/fields/{o.Fields.Issues}/choices/{o.Fields.IssueA}", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["isActive"] = true }), IfMatch: "*", HasForeignIdentifier: false)),
        Case("DELETE", Ws + "/fields/{fieldId}/choices/{choiceId}", ProtectedOperation.Workspace,
            new RouteProbe("choice", HttpMethod.Delete, (o, _) => $"{W(o)}/fields/{o.Fields.Issues}/choices/{int.MaxValue}", HttpStatusCode.NotFound,
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("PUT", Ws + "/fields/{fieldId}/choice-order", ProtectedOperation.Workspace,
            new RouteProbe("field", HttpMethod.Put, (o, _) => $"{W(o)}/fields/{o.Fields.Confidentiality}/choice-order", HttpStatusCode.OK,
                (o, _) => J(new JsonObject { ["choiceIds"] = new JsonArray(o.Fields.Confidential, o.Fields.AttorneysEyesOnly) }),
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("POST", Ws + "/coding-layouts", ProtectedOperation.Workspace,
            WorkspaceOnly(HttpMethod.Post, "/coding-layouts", HttpStatusCode.Created, _ => J(new JsonObject
            {
                ["name"] = "Probe " + Guid.NewGuid().ToString("N")[..8], ["isDefault"] = false, ["sections"] = new JsonArray(),
            }))),
        Case("GET", Ws + "/coding-layouts/{layoutId}", ProtectedOperation.Workspace,
            new RouteProbe("layout", HttpMethod.Get, (o, t) => $"{W(o)}/coding-layouts/{t.LayoutId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/coding-layouts/{layoutId}", ProtectedOperation.Workspace,
            new RouteProbe("layout", HttpMethod.Put, (o, t) => $"{W(o)}/coding-layouts/{t.LayoutId}", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["name"] = "Default", ["isDefault"] = true, ["sections"] = new JsonArray() }), IfMatch: "*")),
        Case("DELETE", Ws + "/coding-layouts/{layoutId}", ProtectedOperation.Workspace,
            new RouteProbe("default layout", HttpMethod.Delete, (o, t) => $"{W(o)}/coding-layouts/{t.LayoutId}", HttpStatusCode.Conflict, IfMatch: "*")),

        // Computed duplicate grouping (E09-T04): field ids in the policy are workspace-local numbers, not identifiers.
        Case("GET", Ws + "/dedupe-policy", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/dedupe-policy", HttpStatusCode.OK)),
        Case("PUT", Ws + "/dedupe-policy", ProtectedOperation.Workspace,
            new RouteProbe("workspace", HttpMethod.Put, (o, _) => W(o) + "/dedupe-policy", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["enabled"] = true, ["hashSource"] = "sha256", ["scope"] = "global" }), IfMatch: "*", HasForeignIdentifier: false)),
        Case("POST", Ws + "/dedupe-runs", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Post, "/dedupe-runs", HttpStatusCode.Accepted)),

        // Documents through the protected-content gateway.
        Case("GET", Ws + "/documents/{documentId}", ProtectedOperation.Open,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t), HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "?purpose=prefetch", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/pages", ProtectedOperation.Open,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages?purpose=prefetch", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/text/hits", ProtectedOperation.View,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/text/hits", HttpStatusCode.OK),
            new RouteProbe("search handle", HttpMethod.Get, (o, t) => $"{Doc(o, o)}/text/hits?searchId={t.SearchId:N}", HttpStatusCode.OK),
            new RouteProbe("highlight set", HttpMethod.Get, (o, t) => $"{Doc(o, o)}/text/hits?highlightSetId={t.HighlightSetId}", HttpStatusCode.OK),
            new RouteProbe("document, search handle and highlight set", HttpMethod.Get,
                (o, t) => $"{Doc(o, t)}/text/hits?searchId={t.SearchId:N}&highlightSetId={t.HighlightSetId}&fromChunk=0", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/text/chunks/{chunkIndex}", ProtectedOperation.View,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/text/chunks/0", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/text/chunks/0?purpose=prefetch", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/native", ProtectedOperation.NativeDownload,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/native", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/text", ProtectedOperation.View,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/text", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/text?purpose=prefetch", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/pages/{pageNumber}/image", ProtectedOperation.ImageRetrieval,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages/1/image", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages/1/image?purpose=prefetch", HttpStatusCode.OK),
            new RouteProbe("document, print", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages/1/image?purpose=print", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/pages/{pageNumber}/thumbnail", ProtectedOperation.View,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages/1/thumbnail", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages/1/thumbnail?purpose=prefetch", HttpStatusCode.OK)),
        Case("POST", Ws + "/documents/{documentId}/views", ProtectedOperation.View,
            new RouteProbe("document", HttpMethod.Post, (o, t) => Doc(o, t) + "/views", HttpStatusCode.NoContent, (_, _) => J(new JsonObject()))),

        // Coding.
        Case("GET", Ws + "/documents/{documentId}/coding", ProtectedOperation.Coding,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/coding", HttpStatusCode.OK),
            new RouteProbe("own document, another workspace's layout", HttpMethod.Get, (o, t) => $"{Doc(o, o)}/coding?layoutId={t.LayoutId}", HttpStatusCode.OK,
                Expectation: ForeignExpectation.SameAsUnknown)),
        Case("PUT", Ws + "/documents/{documentId}/coding", ProtectedOperation.Coding,
            new RouteProbe("document", HttpMethod.Put, (o, t) => Doc(o, t) + "/coding", HttpStatusCode.OK,
                (o, _) => J(new JsonObject
                {
                    ["changes"] = new JsonArray(new JsonObject { ["fieldId"] = o.Fields.Responsive, ["operation"] = "set", ["value"] = true }),
                }),
                IfMatch: "*")),
        Case("POST", Ws + "/bulk-coding", ProtectedOperation.Coding,
            new RouteProbe("frozen set in the body", HttpMethod.Post, (o, _) => W(o) + "/bulk-coding", HttpStatusCode.Accepted,
                (o, t) => J(new JsonObject
                {
                    ["snapshotId"] = t.BulkSnapshotId.ToString(),
                    ["changes"] = new JsonArray(new JsonObject { ["fieldId"] = o.Fields.Responsive, ["operation"] = "set", ["value"] = false }),
                }))),
        Case("GET", Ws + "/documents/{documentId}/relationships", ProtectedOperation.Coding,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/relationships", HttpStatusCode.OK),
            new RouteProbe("document, with coding values", HttpMethod.Get, (o, t) => Doc(o, t) + "/relationships?fields=responsive", HttpStatusCode.OK)),
        Case("POST", Ws + "/coding-propagations/preview", ProtectedOperation.Coding,
            new RouteProbe("source document in the body", HttpMethod.Post, (o, _) => W(o) + "/coding-propagations/preview", HttpStatusCode.OK,
                (o, t) => J(new JsonObject
                {
                    ["sourceDocumentId"] = t.DocumentId.ToString(), ["scope"] = "familyAndDuplicates", ["fields"] = new JsonArray(o.Fields.Responsive),
                }))),
        Case("POST", Ws + "/coding-propagations", ProtectedOperation.Coding,
            new RouteProbe("preview in the body", HttpMethod.Post, (o, _) => W(o) + "/coding-propagations", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["previewId"] = t.PropagationPreviewId.ToString() }))),
        // Privilege conflicts (E13-T02): the report (JSON and CSV through the gateway) may be scoped to a production; the
        // propagation names source documents and duplicate groups in its body.
        Case("GET", Ws + "/privilege-conflicts", ProtectedOperation.Coding,
            WorkspaceOnly(HttpMethod.Get, "/privilege-conflicts", HttpStatusCode.OK),
            new RouteProbe("production in the query", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-conflicts?productionId={t.FinalizedProductionId}",
                HttpStatusCode.OK)),
        Case("GET", Ws + "/privilege-conflicts/export", ProtectedOperation.Coding,
            WorkspaceOnly(HttpMethod.Get, "/privilege-conflicts/export", HttpStatusCode.OK),
            new RouteProbe("production in the query", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-conflicts/export?productionId={t.FinalizedProductionId}",
                HttpStatusCode.OK)),
        // Privilege logs (E13-T03): templates, generation from a production (with a review set) or a frozen set, versions,
        // their rows and files (gateway). A version listing a document the caller may not see answers like a missing one.
        Case("GET", Ws + "/privilege-log-templates", ProtectedOperation.PrivilegeLog,
            WorkspaceOnly(HttpMethod.Get, "/privilege-log-templates", HttpStatusCode.OK)),
        Case("POST", Ws + "/privilege-log-templates", ProtectedOperation.PrivilegeLog,
            WorkspaceOnly(HttpMethod.Post, "/privilege-log-templates", HttpStatusCode.Created,
                _ => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N") }))),
        Case("GET", Ws + "/privilege-log-templates/{templateId}", ProtectedOperation.PrivilegeLog,
            new RouteProbe("template", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-log-templates/{t.PrivilegeLogTemplateId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/privilege-log-templates/{templateId}", ProtectedOperation.PrivilegeLog,
            new RouteProbe("template", HttpMethod.Put, (o, t) => $"{W(o)}/privilege-log-templates/{t.PrivilegeLogTemplateId}", HttpStatusCode.OK,
                (o, _) => J(new JsonObject { ["name"] = "Log template " + o.Name, ["definition"] = new JsonObject { ["privIdPrefix"] = "PRIV" } }),
                IfMatch: "*")),
        Case("POST", Ws + "/privilege-logs", ProtectedOperation.PrivilegeLog,
            new RouteProbe("production and template in the body", HttpMethod.Post, (o, _) => W(o) + "/privilege-logs", null,
                (o, t) => J(new JsonObject { ["productionId"] = t.FinalizedProductionId.ToString(), ["templateId"] = o.PrivilegeLogTemplateId.ToString() })),
            new RouteProbe("template in the body", HttpMethod.Post, (o, _) => W(o) + "/privilege-logs", null,
                (o, t) => J(new JsonObject { ["productionId"] = o.FinalizedProductionId.ToString(), ["templateId"] = t.PrivilegeLogTemplateId.ToString() })),
            new RouteProbe("review set in the body", HttpMethod.Post, (o, _) => W(o) + "/privilege-logs", null,
                (o, t) => J(new JsonObject { ["productionId"] = o.FinalizedProductionId.ToString(), ["snapshotId"] = t.ProductionSnapshotId.ToString() })),
            new RouteProbe("frozen set in the body", HttpMethod.Post, (o, _) => W(o) + "/privilege-logs", null,
                (_, t) => J(new JsonObject { ["snapshotId"] = t.ProductionSnapshotId.ToString(), ["preset"] = "metadataOnly" }))),
        Case("GET", Ws + "/privilege-logs", ProtectedOperation.PrivilegeLog,
            new RouteProbe("production in the query", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-logs?productionId={t.FinalizedProductionId}",
                HttpStatusCode.OK, Expectation: ForeignExpectation.EmptySet),
            new RouteProbe("frozen set in the query", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-logs?snapshotId={t.ProductionSnapshotId}",
                HttpStatusCode.OK, Expectation: ForeignExpectation.EmptySet)),
        Case("GET", Ws + "/privilege-logs/{logId}", ProtectedOperation.PrivilegeLog,
            new RouteProbe("privilege log", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-logs/{t.PrivilegeLogId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/privilege-logs/{logId}/entries", ProtectedOperation.PrivilegeLog,
            new RouteProbe("privilege log", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-logs/{t.PrivilegeLogId}/entries", HttpStatusCode.OK)),
        Case("GET", Ws + "/privilege-logs/{logId}/content", ProtectedOperation.PrivilegeLog,
            new RouteProbe("privilege log", HttpMethod.Get, (o, t) => $"{W(o)}/privilege-logs/{t.PrivilegeLogId}/content?format=xlsx", HttpStatusCode.OK)),
        Case("POST", Ws + "/privilege-conflicts/propagations", ProtectedOperation.Coding,
            // The probe document is in no duplicate group: the own request is a validation problem, a foreign source a 404.
            new RouteProbe("source document and duplicate group in the body", HttpMethod.Post, (o, _) => W(o) + "/privilege-conflicts/propagations",
                HttpStatusCode.BadRequest,
                (_, t) => J(new JsonObject
                {
                    ["groups"] = new JsonArray(new JsonObject
                    {
                        ["duplicateGroupId"] = t.DocumentId.ToString(), ["sourceDocumentId"] = t.DocumentId.ToString(),
                    }),
                }))),
        Case("GET", Ws + "/bulk-coding/{jobId}/report", ProtectedOperation.Coding,
            new RouteProbe("bulk coding job", HttpMethod.Get, (o, t) => $"{W(o)}/bulk-coding/{t.BulkCodingJobId}/report", HttpStatusCode.OK),
            new RouteProbe("bulk coding job, skippedHidden", HttpMethod.Get, (o, t) => $"{W(o)}/bulk-coding/{t.BulkCodingJobId}/report?outcome=skippedHidden", HttpStatusCode.OK)),

        // Coding history and review batches (E10-T05). Status changes accept any answer PEP-1 admitted for the caller's own
        // batch (they change it once, then may answer 409); foreign sets and batches must look exactly like unknown ones.
        Case("GET", Ws + "/documents/{documentId}/coding-history", ProtectedOperation.Coding,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/coding-history", HttpStatusCode.OK),
            new RouteProbe("document, one field", HttpMethod.Get, (o, t) => Doc(o, t) + $"/coding-history?fieldId={o.Fields.Responsive}", HttpStatusCode.OK)),
        Case("POST", Ws + "/review-batch-sets", ProtectedOperation.ReviewBatch,
            new RouteProbe("frozen set in the body", HttpMethod.Post, (o, _) => W(o) + "/review-batch-sets", HttpStatusCode.Created,
                (_, t) => J(BatchSetBody(t.ReviewBatchSnapshotId))),
            new RouteProbe("first-pass set in the body", HttpMethod.Post, (o, _) => W(o) + "/review-batch-sets", HttpStatusCode.Created,
                (o, t) =>
                {
                    var body = BatchSetBody(o.ReviewBatchSnapshotId);
                    body["reviewPass"] = "qc";
                    body["qcOfBatchSetId"] = t.FirstPassBatchSetId.ToString();
                    return J(body);
                })),
        Case("GET", Ws + "/review-batch-sets", ProtectedOperation.ReviewBatch, WorkspaceOnly(HttpMethod.Get, "/review-batch-sets", HttpStatusCode.OK)),
        Case("GET", Ws + "/review-batch-sets/{batchSetId}", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch set", HttpMethod.Get, (o, t) => $"{W(o)}/review-batch-sets/{t.FirstPassBatchSetId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/review-batch-sets/{batchSetId}/conflicts", ProtectedOperation.ReviewBatch,
            new RouteProbe("QC batch set", HttpMethod.Get, (o, t) => $"{W(o)}/review-batch-sets/{t.QcBatchSetId}/conflicts", HttpStatusCode.OK)),
        Case("GET", Ws + "/review-batches", ProtectedOperation.ReviewBatch,
            WorkspaceOnly(HttpMethod.Get, "/review-batches", HttpStatusCode.OK),
            new RouteProbe("batch set in the query", HttpMethod.Get, (o, t) => $"{W(o)}/review-batches?batchSetId={t.FirstPassBatchSetId}", HttpStatusCode.OK,
                Expectation: ForeignExpectation.EmptySet)),
        Case("GET", Ws + "/review-batches/{batchId}", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch", HttpMethod.Get, (o, t) => $"{W(o)}/review-batches/{t.ReviewBatchId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/review-batches/{batchId}/documents", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch", HttpMethod.Get, (o, t) => $"{W(o)}/review-batches/{t.ReviewBatchId}/documents", HttpStatusCode.OK)),
        Case("POST", Ws + "/review-batches/{batchId}/check-out", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch", HttpMethod.Post, (o, t) => $"{W(o)}/review-batches/{t.ReviewBatchId}/check-out", null, IfMatch: "*")),
        Case("POST", Ws + "/review-batches/{batchId}/check-in", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch", HttpMethod.Post, (o, t) => $"{W(o)}/review-batches/{t.ReviewBatchId}/check-in", null,
                (_, _) => J(new JsonObject { ["completed"] = false }), IfMatch: "*")),
        Case("PUT", Ws + "/review-batches/{batchId}/assignment", ProtectedOperation.ReviewBatch,
            new RouteProbe("batch", HttpMethod.Put, (o, t) => $"{W(o)}/review-batches/{t.ReviewBatchId}/assignment", null,
                (o, _) => J(new JsonObject { ["assigneeId"] = o.Owner.ToString() }), IfMatch: "*")),

        // Redactions (E11-T04): Redaction Sets by id, reasons by workspace-local code, document redactions by document and set.
        Case("GET", Ws + "/redaction-sets", ProtectedOperation.Redaction, WorkspaceOnly(HttpMethod.Get, "/redaction-sets", HttpStatusCode.OK)),
        Case("POST", Ws + "/redaction-sets", ProtectedOperation.Redaction,
            WorkspaceOnly(HttpMethod.Post, "/redaction-sets", HttpStatusCode.Created, _ => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N") }))),
        Case("PUT", Ws + "/redaction-sets/{redactionSetId}", ProtectedOperation.Redaction,
            new RouteProbe("redaction set", HttpMethod.Put, (o, t) => $"{W(o)}/redaction-sets/{t.RedactionSetId}", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["name"] = "Default" }), IfMatch: "*")),
        Case("GET", Ws + "/redaction-reasons", ProtectedOperation.Redaction, WorkspaceOnly(HttpMethod.Get, "/redaction-reasons", HttpStatusCode.OK)),
        Case("POST", Ws + "/redaction-reasons", ProtectedOperation.Redaction,
            WorkspaceOnly(HttpMethod.Post, "/redaction-reasons", HttpStatusCode.Created, _ => J(new JsonObject
            {
                ["code"] = "P" + Guid.NewGuid().ToString("N"), ["name"] = "Probe " + Guid.NewGuid().ToString("N"), ["category"] = "other", ["boxLabel"] = "Redacted",
            }))),
        Case("PUT", Ws + "/redaction-reasons/{reasonCode}", ProtectedOperation.Redaction,
            new RouteProbe("workspace-local reason code", HttpMethod.Put, (o, _) => W(o) + "/redaction-reasons/Other", HttpStatusCode.OK,
                (_, _) => J(new JsonObject { ["name"] = "Other", ["category"] = "other", ["boxLabel"] = "Redacted" }), IfMatch: "*",
                HasForeignIdentifier: false)),
        Case("GET", Ws + "/documents/{documentId}/redaction-sets/{redactionSetId}", ProtectedOperation.Redaction,
            new RouteProbe("document and redaction set", HttpMethod.Get, (o, t) => $"{Doc(o, t)}/redaction-sets/{t.RedactionSetId}", HttpStatusCode.OK),
            new RouteProbe("own document, another workspace's redaction set", HttpMethod.Get, (o, t) => $"{Doc(o, o)}/redaction-sets/{t.RedactionSetId}",
                HttpStatusCode.OK),
            new RouteProbe("document, as of version 1", HttpMethod.Get, (o, t) => $"{Doc(o, t)}/redaction-sets/{t.RedactionSetId}?version=1", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/redaction-sets/{redactionSetId}/history", ProtectedOperation.Redaction,
            new RouteProbe("document and redaction set", HttpMethod.Get, (o, t) => $"{Doc(o, t)}/redaction-sets/{t.RedactionSetId}/history", HttpStatusCode.OK),
            new RouteProbe("own document, another workspace's redaction set", HttpMethod.Get,
                (o, t) => $"{Doc(o, o)}/redaction-sets/{t.RedactionSetId}/history", HttpStatusCode.OK)),
        Case("POST", Ws + "/documents/{documentId}/redaction-sets/{redactionSetId}/revisions", ProtectedOperation.Redaction,
            new RouteProbe("document and redaction set", HttpMethod.Post, (o, t) => $"{Doc(o, t)}/redaction-sets/{t.RedactionSetId}/revisions",
                HttpStatusCode.OK, (_, _) => J(RedactionBody()), IfMatch: "*"),
            new RouteProbe("own document, another workspace's redaction set", HttpMethod.Post,
                (o, t) => $"{Doc(o, o)}/redaction-sets/{t.RedactionSetId}/revisions", HttpStatusCode.OK, (_, _) => J(RedactionBody()), IfMatch: "*")),

        // Frozen sets.
        Case("POST", Ws + "/snapshots", ProtectedOperation.Snapshot,
            new RouteProbe("source snapshot in the body", HttpMethod.Post, (o, _) => W(o) + "/snapshots", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["purpose"] = "Export", ["snapshotId"] = t.ExportSnapshotId.ToString() })),
            new RouteProbe("document IDs in the body", HttpMethod.Post, (o, _) => W(o) + "/snapshots", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["purpose"] = "BulkCoding", ["documentIds"] = new JsonArray(t.DocumentId.ToString()) }),
                Expectation: ForeignExpectation.EmptySet),
            new RouteProbe("saved search in the body", HttpMethod.Post, (o, _) => W(o) + "/snapshots", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["purpose"] = "Report", ["savedSearchId"] = t.SavedSearchId.ToString() }))),
        Case("GET", Ws + "/snapshots", ProtectedOperation.Snapshot, WorkspaceOnly(HttpMethod.Get, "/snapshots", HttpStatusCode.OK)),
        Case("GET", Ws + "/snapshots/{snapshotId}", ProtectedOperation.Snapshot,
            new RouteProbe("bulk coding snapshot", HttpMethod.Get, (o, t) => $"{W(o)}/snapshots/{t.BulkSnapshotId}", HttpStatusCode.OK),
            new RouteProbe("export snapshot", HttpMethod.Get, (o, t) => $"{W(o)}/snapshots/{t.ExportSnapshotId}", HttpStatusCode.OK)),

        // Imports.
        Case("GET", Ws + "/import-targets", ProtectedOperation.Import, WorkspaceOnly(HttpMethod.Get, "/import-targets", HttpStatusCode.OK)),
        Case("GET", Ws + "/import-profiles", ProtectedOperation.Import, WorkspaceOnly(HttpMethod.Get, "/import-profiles", HttpStatusCode.OK)),
        Case("POST", Ws + "/import-profiles", ProtectedOperation.Import,
            WorkspaceOnly(HttpMethod.Post, "/import-profiles", HttpStatusCode.Created,
                _ => J(new JsonObject { ["name"] = "Probe " + Guid.NewGuid().ToString("N"), ["definition"] = new JsonObject() }))),
        Case("GET", Ws + "/import-profiles/{profileId}", ProtectedOperation.Import,
            new RouteProbe("import profile", HttpMethod.Get, (o, t) => $"{W(o)}/import-profiles/{t.ImportProfileId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/import-profiles/{profileId}", ProtectedOperation.Import,
            new RouteProbe("import profile", HttpMethod.Put, (o, t) => $"{W(o)}/import-profiles/{t.ImportProfileId}", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["name"] = "Renamed " + t.ImportProfileId.ToString("N"), ["definition"] = new JsonObject() }),
                IfMatch: "*")),
        Case("DELETE", Ws + "/import-profiles/{profileId}", ProtectedOperation.Import,
            new RouteProbe("import profile", HttpMethod.Delete, (o, t) => $"{W(o)}/import-profiles/{t.SpareProfileId}", HttpStatusCode.NoContent, IfMatch: "*")),
        Case("POST", Ws + "/import-mapping-previews", ProtectedOperation.Import,
            new RouteProbe("import profile in the request part", HttpMethod.Post, (o, _) => W(o) + "/import-mapping-previews", HttpStatusCode.OK,
                (o, t) => AttackWorld.ImportForm(new JsonObject { ["profileId"] = t.ImportProfileId.ToString() }, o.Name + "-PREVIEW"))),
        Case("POST", Ws + "/imports", ProtectedOperation.Import,
            new RouteProbe("import profile in the request part", HttpMethod.Post, (o, _) => W(o) + "/imports", HttpStatusCode.Accepted,
                (o, t) => AttackWorld.ImportForm(new JsonObject { ["profileId"] = t.ImportProfileId.ToString() }, o.Name + "-" + Guid.NewGuid().ToString("N")[..8]))),
        Case("POST", Ws + "/imports/preflight", ProtectedOperation.Import,
            new RouteProbe("import profile in the request part", HttpMethod.Post, (o, _) => W(o) + "/imports/preflight", HttpStatusCode.OK,
                (o, t) => AttackWorld.ImportForm(new JsonObject { ["profileId"] = t.ImportProfileId.ToString() }, o.Name + "-PREFLIGHT"))),
        Case("GET", Ws + "/imports", ProtectedOperation.Import, WorkspaceOnly(HttpMethod.Get, "/imports", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}/errors", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}/errors", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}/report", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}/report", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}/report.csv", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}/report.csv", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}/error-file", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}/error-file", null)),
        Case("GET", Ws + "/imports/preflight/{preflightId}/issues", ProtectedOperation.Import,
            new RouteProbe("pre-flight", HttpMethod.Get, (o, t) => $"{W(o)}/imports/preflight/{t.PreflightId}/issues", HttpStatusCode.OK)),
        Case("GET", Ws + "/imports/{importId}/family-issues", ProtectedOperation.Import,
            new RouteProbe("import", HttpMethod.Get, (o, t) => $"{W(o)}/imports/{t.ImportId}/family-issues", HttpStatusCode.OK)),

        // Exports.
        Case("POST", Ws + "/exports", ProtectedOperation.Export,
            new RouteProbe("frozen set in the body", HttpMethod.Post, (o, _) => W(o) + "/exports", HttpStatusCode.Accepted,
                (_, t) => J(new JsonObject
                {
                    ["snapshotId"] = t.ExportSnapshotId.ToString(),
                    ["fields"] = new JsonArray(new JsonObject { ["fieldId"] = Core.Fields.SystemFields.ControlNumber }),
                }))),
        Case("GET", Ws + "/exports", ProtectedOperation.Export, WorkspaceOnly(HttpMethod.Get, "/exports", HttpStatusCode.OK)),
        Case("GET", Ws + "/exports/{exportId}", ProtectedOperation.Export,
            new RouteProbe("export", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{t.ExportId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/exports/{exportId}/exclusions", ProtectedOperation.Export,
            new RouteProbe("export", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{t.ExportId}/exclusions", HttpStatusCode.OK)),
        Case("GET", Ws + "/exports/{exportId}/files", ProtectedOperation.Export,
            new RouteProbe("export", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{t.ExportId}/files", HttpStatusCode.OK)),
        Case("GET", Ws + "/exports/{exportId}/files/{fileId}/content", ProtectedOperation.Export,
            new RouteProbe("export and file", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{t.ExportId}/files/{t.ExportFileId}/content", HttpStatusCode.OK),
            new RouteProbe("own export, another workspace's file", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{o.ExportId}/files/{t.ExportFileId}/content", HttpStatusCode.OK)),
        Case("GET", Ws + "/exports/{exportId}/package", ProtectedOperation.Export,
            new RouteProbe("export", HttpMethod.Get, (o, t) => $"{W(o)}/exports/{t.ExportId}/package", HttpStatusCode.OK)),

        // Productions (E12-T02/T03). State-changing probes accept any answer PEP-1 admitted for the caller's own production
        // (they change it once, then answer 409); foreign productions must look exactly like unknown ones.
        Case("POST", Ws + "/productions", ProtectedOperation.ProductionInclusion,
            new RouteProbe("frozen set in the body", HttpMethod.Post, (o, _) => W(o) + "/productions", HttpStatusCode.Created,
                (_, t) => J(ProductionBody(new JsonObject { ["snapshotId"] = t.ProductionSnapshotId.ToString() }))),
            new RouteProbe("saved search in the body", HttpMethod.Post, (o, _) => W(o) + "/productions", HttpStatusCode.Created,
                (_, t) => J(ProductionBody(new JsonObject { ["savedSearchId"] = t.SavedSearchId.ToString() }))),
            new RouteProbe("version to supersede in the body", HttpMethod.Post, (o, _) => W(o) + "/productions", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["previousVersionId"] = t.FinalizedProductionId.ToString() }))),
        Case("GET", Ws + "/productions", ProtectedOperation.ProductionInclusion, WorkspaceOnly(HttpMethod.Get, "/productions", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/bates-lookup", ProtectedOperation.ProductionInclusion,
            new RouteProbe("Bates number in the query", HttpMethod.Get, (o, t) => $"{W(o)}/productions/bates-lookup?number={t.BatesLabel}", HttpStatusCode.OK,
                Expectation: ForeignExpectation.EmptySet),
            new RouteProbe("document in the query", HttpMethod.Get, (o, t) => $"{W(o)}/productions/bates-lookup?documentId={t.DocumentId}", HttpStatusCode.OK,
                Expectation: ForeignExpectation.EmptySet)),
        Case("GET", Ws + "/productions/{productionId}", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}", HttpStatusCode.OK)),
        Case("PUT", Ws + "/productions/{productionId}", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production", HttpMethod.Put, (o, t) => $"{W(o)}/productions/{t.DraftProductionId}", null,
                (_, _) => J(new JsonObject { ["specification"] = new JsonObject { ["bates"] = new JsonObject { ["prefix"] = "PROBE", ["startNumber"] = 500 } } }),
                IfMatch: "*")),
        Case("POST", Ws + "/productions/{productionId}/bates-allocation", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.DraftProductionId}/bates-allocation", null)),
        Case("POST", Ws + "/productions/{productionId}/finalize", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.DraftProductionId}/finalize", null, IfMatch: "*")),
        Case("POST", Ws + "/productions/{productionId}/void", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/void", null,
                (_, _) => J(new JsonObject { ["reason"] = "Attack probe." }), IfMatch: "*")),
        Case("POST", Ws + "/productions/{productionId}/discard", ProtectedOperation.ProductionInclusion,
            new RouteProbe("spare draft production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.SpareProductionId}/discard", null, IfMatch: "*")),
        Case("GET", Ws + "/productions/{productionId}/documents", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/documents", HttpStatusCode.OK)),
        Case("POST", Ws + "/productions/{productionId}/verification", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/verification", HttpStatusCode.OK)),
        // Designations (E12-T04): the production and the document are both identifiers; a foreign document in an own production
        // must answer like an unknown one.
        Case("GET", Ws + "/productions/{productionId}/designations", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/designations", HttpStatusCode.OK)),
        Case("PUT", Ws + "/productions/{productionId}/designation-overrides/{documentId}", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production and document", HttpMethod.Put, (o, t) => $"{W(o)}/productions/{t.DraftProductionId}/designation-overrides/{t.DocumentId}",
                null, (_, _) => J(new JsonObject { ["choiceId"] = null, ["reason"] = "Attack probe" })),
            new RouteProbe("own draft, another workspace's document", HttpMethod.Put,
                (o, t) => $"{W(o)}/productions/{o.DraftProductionId}/designation-overrides/{t.DocumentId}", null,
                (_, _) => J(new JsonObject { ["choiceId"] = null, ["reason"] = "Attack probe" }))),
        Case("DELETE", Ws + "/productions/{productionId}/designation-overrides/{documentId}", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production and document", HttpMethod.Delete,
                (o, t) => $"{W(o)}/productions/{t.DraftProductionId}/designation-overrides/{t.DocumentId}", null),
            new RouteProbe("own draft, another workspace's document", HttpMethod.Delete,
                (o, t) => $"{W(o)}/productions/{o.DraftProductionId}/designation-overrides/{t.DocumentId}", null)),
        Case("GET", Ws + "/productions/{productionId}/redesignation-report", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/redesignation-report", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/redesignation-overlay", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/redesignation-overlay", HttpStatusCode.OK)),
        // Production QC gate (E12-T07): the draft is not allocated (409 for its owner), the finalized production keeps its finalization run.
        Case("POST", Ws + "/productions/{productionId}/qc", ProtectedOperation.ProductionInclusion,
            new RouteProbe("draft production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.DraftProductionId}/qc", null)),
        Case("GET", Ws + "/productions/{productionId}/qc", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/qc", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/qc/exceptions", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/qc/exceptions", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/qc/report", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production, CSV", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/qc/report", HttpStatusCode.OK),
            new RouteProbe("finalized production, PDF", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/qc/report?format=pdf", HttpStatusCode.OK)),
        // Production volumes (E12-T05): the production and the run are both identifiers; a foreign run under an own production
        // (or a foreign file under an own run) must answer like an unknown one.
        Case("POST", Ws + "/productions/{productionId}/volumes", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Post, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes", null)),
        Case("GET", Ws + "/productions/{productionId}/volumes", ProtectedOperation.ProductionInclusion,
            new RouteProbe("finalized production", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/volumes/{volumeId}", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production and run", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes/{t.ProductionVolumeId}", HttpStatusCode.OK),
            new RouteProbe("own production, another workspace's run", HttpMethod.Get,
                (o, t) => $"{W(o)}/productions/{o.FinalizedProductionId}/volumes/{t.ProductionVolumeId}", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/volumes/{volumeId}/files", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production and run", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes/{t.ProductionVolumeId}/files",
                HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/volumes/{volumeId}/files/{fileId}/content", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production, run and file", HttpMethod.Get,
                (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes/{t.ProductionVolumeId}/files/{t.ProductionVolumeFileId}/content", HttpStatusCode.OK),
            new RouteProbe("own run, another workspace's file", HttpMethod.Get,
                (o, t) => $"{W(o)}/productions/{o.FinalizedProductionId}/volumes/{o.ProductionVolumeId}/files/{t.ProductionVolumeFileId}/content", HttpStatusCode.OK)),
        Case("GET", Ws + "/productions/{productionId}/volumes/{volumeId}/package", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production and run", HttpMethod.Get, (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes/{t.ProductionVolumeId}/package",
                HttpStatusCode.OK)),
        // Burn-in verification report (E12-T06): same identifiers as the run's files.
        Case("GET", Ws + "/productions/{productionId}/volumes/{volumeId}/verification-report", ProtectedOperation.ProductionInclusion,
            new RouteProbe("production and run", HttpMethod.Get,
                (o, t) => $"{W(o)}/productions/{t.FinalizedProductionId}/volumes/{t.ProductionVolumeId}/verification-report", HttpStatusCode.OK),
            new RouteProbe("own production, another workspace's run", HttpMethod.Get,
                (o, t) => $"{W(o)}/productions/{o.FinalizedProductionId}/volumes/{t.ProductionVolumeId}/verification-report", HttpStatusCode.OK)),

        // Routes without a workspace-scoped identifier.
        Exempt("GET", V1 + "/me", "the caller's own session; no identifier"),
        Exempt("GET", V1 + "/workspaces", "lists the caller's memberships only (asserted by the suite: never another workspace)"),
        Exempt("POST", V1 + "/workspaces", "creates a workspace; installation permission, no identifier"),
        Exempt("GET", V1 + "/me/preferences", "own profile, addressed by the session user (cross-user check in the suite)"),
        Exempt("PUT", V1 + "/me/preferences/{key}", "own profile, addressed by the session user; the key is a name, not an identifier"),
        Exempt("DELETE", V1 + "/me/preferences/{key}", "own profile, addressed by the session user; the key is a name, not an identifier"),
        Exempt("GET", V1 + "/workspace-deletions",
            "installation-level: the caller's own deletion requests, or every one for a Retention Approver (asserted in WorkspaceDeletionApiTests)"),
        Exempt("GET", V1 + "/workspace-deletions/{deletionId}",
            "installation-level deletion record, outliving its workspace; 404 to anyone but its requester and Retention Approvers (WorkspaceDeletionApiTests)"),
        Exempt("POST", V1 + "/workspace-deletions/{deletionId}/approve",
            "installation permission Installation.ApproveDeletion with MFA; the record is not workspace-scoped (WorkspaceDeletionApiTests)"),
        Exempt("POST", V1 + "/workspace-deletions/{deletionId}/cancel",
            "installation-level deletion record; 404 to anyone but its requester and Retention Approvers (WorkspaceDeletionApiTests)"),
        Exempt("GET", V1 + "/workspace-deletions/{deletionId}/certificate",
            "installation-level destruction certificate; 404 to anyone but the requester and Retention Approvers (WorkspaceDeletionApiTests)"),
        Exempt("GET", "/bff/login", "anonymous sign-in redirect"),
        Exempt("POST", "/bff/logout", "ends the caller's own session"),
        Exempt("POST", "/bff/backchannel-logout", "IdP-signed logout token, validated by the authentication handler"),
        Exempt("*", "/health/live", "anonymous liveness probe"),
        Exempt("*", "/health/ready", "anonymous readiness probe"),
        Exempt("GET", "/openapi/{documentName}.json", "the public API description"),
    ];

    private static JsonObject BatchSetBody(Guid snapshotId) => new()
    {
        ["name"] = "Probe " + Guid.NewGuid().ToString("N"),
        ["snapshotId"] = snapshotId.ToString(),
        ["batchPrefix"] = "P" + Guid.NewGuid().ToString("N")[..12],
        ["maxBatchSize"] = 5,
    };

    private static RouteCase Exempt(string method, string pattern, string reason) => new(method, pattern, ProtectedOperation.Workspace, [], reason);

    public static MediaTypeHeaderValue? MediaType(HttpContent? content) => content?.Headers.ContentType;
}
