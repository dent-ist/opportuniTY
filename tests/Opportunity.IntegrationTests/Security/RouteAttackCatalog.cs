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
    public const string Snapshot = "Frozen sets (snapshots)";
    public const string Import = "Import jobs, reports and profiles";
    public const string Job = "Job status";
    public const string Workspace = "Workspace administration and catalogues";

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

    public static IReadOnlyList<RouteCase> Cases { get; } =
    [
        // Search: query text, handles and cursors.
        Case("POST", Ws + "/query-validations", ProtectedOperation.Search,
            WorkspaceOnly(HttpMethod.Post, "/query-validations", HttpStatusCode.OK, _ => J(new JsonObject { ["query"] = "memo" }))),
        Case("POST", Ws + "/searches", ProtectedOperation.Search,
            new RouteProbe("query names another workspace's control number", HttpMethod.Post, (o, _) => W(o) + "/searches", HttpStatusCode.OK,
                (_, t) => J(new JsonObject { ["query"] = $"controlNumber:\"{t.ControlNumber}\"", ["facets"] = new JsonArray("fileType") }),
                Expectation: ForeignExpectation.EmptySet)),
        Case("GET", Ws + "/searches/{searchId}/pages", ProtectedOperation.Search,
            new RouteProbe("search handle and its cursor", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{t.SearchId:N}/pages?cursor={t.SearchCursor}", HttpStatusCode.OK),
            new RouteProbe("own handle, another workspace's cursor", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{o.SearchId:N}/pages?cursor={t.SearchCursor}", HttpStatusCode.OK),
            new RouteProbe("search handle, page jump", HttpMethod.Get, (o, t) => $"{W(o)}/searches/{t.SearchId:N}/pages?page=1", HttpStatusCode.OK)),
        Case("GET", Ws + "/query-history", ProtectedOperation.Search, WorkspaceOnly(HttpMethod.Get, "/query-history", HttpStatusCode.OK)),
        Case("POST", Ws + "/query-history", ProtectedOperation.Search,
            WorkspaceOnly(HttpMethod.Post, "/query-history", HttpStatusCode.NoContent, _ => J(new JsonObject { ["query"] = "memo" }))),

        // Jobs.
        Case("GET", Ws + "/jobs/{jobId}", ProtectedOperation.Job,
            new RouteProbe("import job", HttpMethod.Get, (o, t) => $"{W(o)}/jobs/{t.ImportJobId}", HttpStatusCode.OK),
            new RouteProbe("bulk coding job", HttpMethod.Get, (o, t) => $"{W(o)}/jobs/{t.BulkCodingJobId}", HttpStatusCode.OK)),

        // Workspace administration and catalogues (the workspace is the only identifier).
        Case("GET", Ws, ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, string.Empty, HttpStatusCode.OK)),
        Case("PUT", Ws, ProtectedOperation.Workspace,
            new RouteProbe("workspace", HttpMethod.Put, (o, _) => W(o), null, (o, _) => J(new JsonObject { ["name"] = "Workspace " + o.Name }),
                IfMatch: "*", HasForeignIdentifier: false)),
        Case("GET", Ws + "/members", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/members", HttpStatusCode.OK)),
        Case("GET", Ws + "/fields", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/fields", HttpStatusCode.OK)),
        Case("GET", Ws + "/coding-layouts", ProtectedOperation.Workspace, WorkspaceOnly(HttpMethod.Get, "/coding-layouts", HttpStatusCode.OK)),

        // Documents through the protected-content gateway.
        Case("GET", Ws + "/documents/{documentId}", ProtectedOperation.Open,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t), HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "?purpose=prefetch", HttpStatusCode.OK)),
        Case("GET", Ws + "/documents/{documentId}/pages", ProtectedOperation.Open,
            new RouteProbe("document", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages", HttpStatusCode.OK),
            new RouteProbe("document, review-mode prefetch", HttpMethod.Get, (o, t) => Doc(o, t) + "/pages?purpose=prefetch", HttpStatusCode.OK)),
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
        Case("GET", Ws + "/bulk-coding/{jobId}/report", ProtectedOperation.Coding,
            new RouteProbe("bulk coding job", HttpMethod.Get, (o, t) => $"{W(o)}/bulk-coding/{t.BulkCodingJobId}/report", HttpStatusCode.OK),
            new RouteProbe("bulk coding job, skippedHidden", HttpMethod.Get, (o, t) => $"{W(o)}/bulk-coding/{t.BulkCodingJobId}/report?outcome=skippedHidden", HttpStatusCode.OK)),

        // Frozen sets.
        Case("POST", Ws + "/snapshots", ProtectedOperation.Snapshot,
            new RouteProbe("source snapshot in the body", HttpMethod.Post, (o, _) => W(o) + "/snapshots", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["purpose"] = "Export", ["snapshotId"] = t.ExportSnapshotId.ToString() })),
            new RouteProbe("document IDs in the body", HttpMethod.Post, (o, _) => W(o) + "/snapshots", HttpStatusCode.Created,
                (_, t) => J(new JsonObject { ["purpose"] = "BulkCoding", ["documentIds"] = new JsonArray(t.DocumentId.ToString()) }),
                Expectation: ForeignExpectation.EmptySet)),
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

        // Routes without a workspace-scoped identifier.
        Exempt("GET", V1 + "/me", "the caller's own session; no identifier"),
        Exempt("GET", V1 + "/workspaces", "lists the caller's memberships only (asserted by the suite: never another workspace)"),
        Exempt("POST", V1 + "/workspaces", "creates a workspace; installation permission, no identifier"),
        Exempt("GET", V1 + "/me/preferences", "own profile, addressed by the session user (cross-user check in the suite)"),
        Exempt("PUT", V1 + "/me/preferences/{key}", "own profile, addressed by the session user; the key is a name, not an identifier"),
        Exempt("DELETE", V1 + "/me/preferences/{key}", "own profile, addressed by the session user; the key is a name, not an identifier"),
        Exempt("GET", "/bff/login", "anonymous sign-in redirect"),
        Exempt("POST", "/bff/logout", "ends the caller's own session"),
        Exempt("POST", "/bff/backchannel-logout", "IdP-signed logout token, validated by the authentication handler"),
        Exempt("*", "/health/live", "anonymous liveness probe"),
        Exempt("*", "/health/ready", "anonymous readiness probe"),
        Exempt("GET", "/openapi/{documentName}.json", "the public API description"),
    ];

    private static RouteCase Exempt(string method, string pattern, string reason) => new(method, pattern, ProtectedOperation.Workspace, [], reason);

    public static MediaTypeHeaderValue? MediaType(HttpContent? content) => content?.Headers.ContentType;
}
