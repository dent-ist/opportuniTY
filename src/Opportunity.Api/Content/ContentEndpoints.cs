using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Content;
using Opportunity.Application.Fields;
using Opportunity.Application.Search.HighlightSets;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Documents;
using Opportunity.Hosting.Options;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;
using Opportunity.Storage;

namespace Opportunity.Api.Content;

/// <summary>
/// The document-level permission a gateway endpoint enforces per document (Q-18). PEP-1 checks workspace membership
/// only, so that the gateway can decide the permission itself and audit every attempt, allowed or denied, as one
/// specific <c>Document.*</c> event instead of a generic <c>AuthZ.Denied</c> (ADR-013 §4).
/// </summary>
/// <param name="Permissions">The permission(s), in order: the default one first, then purpose-specific ones (print).</param>
public sealed record DocumentPermissionMetadata(IReadOnlyList<Permission> Permissions);

public static class DocumentPermissionConventions
{
    /// <summary>Membership at PEP-1, then <paramref name="permissions"/> per document in the gateway (PDP, audited).</summary>
    public static TBuilder RequireDocumentPermission<TBuilder>(this TBuilder builder, params Permission[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentOutOfRangeException.ThrowIfZero(permissions.Length);
        foreach (var permission in permissions)
        {
            _ = PermissionCatalog.Get(permission);
        }

        builder.RequireWorkspaceMember();
        builder.Add(endpoint => endpoint.Metadata.Add(new DocumentPermissionMetadata([.. permissions])));
        return builder;
    }
}

/// <summary>
/// Document routes of the protected-content gateway (E05-T04, E11-T01): the viewer's metadata, page list, chunked text,
/// page images and thumbnails (<c>Document.View</c>), print images (<c>Document.Print</c>) and the native download
/// (<c>Document.DownloadNative</c>). Each endpoint declares its document permission; the gateway decides it per
/// document against PostgreSQL (restriction classes, walls, break-glass) so that every attempt, allowed or denied, is
/// audited as one <c>Document.*</c> event with the document ID, rendition, purpose and outcome before any content is
/// returned. A malformed, unknown, deleted, restricted or walled document gets the same 404. Responses are
/// <c>no-store</c>, <c>nosniff</c> and carry the retrieval's audit event ID for the <c>Document.Viewed</c> beacon.
/// </summary>
public sealed class ContentEndpoints : IApiEndpointModule
{
    private const string Tag = "Document content";

    private static readonly string[] Images = ["image/jpeg", "image/webp"];

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var documents = routes.Workspace.MapGroup("/documents/{documentId}")
            .WithTags(Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance);

        // Mapped on the workspace group so the route pattern has no trailing slash.
        routes.Workspace.MapGet("/documents/{documentId}", GetDocumentAsync)
            .WithTags(Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("GetDocument")
            .WithSummary("The document's fields with display hints and its artifacts (Document.View).")
            .WithDescription(
                "Every field the caller may see (system, imported and coding fields) with its canonical value, a display "
                + "value (instants in the workspace display time zone) and the raw imported string; plus whether text, "
                + "page images and a native exist. purpose: display (default) or prefetch.")
            .Produces<DocumentResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/pages", ListPagesAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("ListDocumentPages")
            .WithSummary("The pages of the document's active page set (Document.View).")
            .WithDescription(
                "Page geometry (points), rotation and which review image and thumbnail can be fetched, in page order. "
                + "limit: 1-500 (default 500). purpose: display (default) or prefetch.")
            .Produces<CursorPage<DocumentPageResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/text/chunks/{chunkIndex}", GetTextChunkAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("GetDocumentTextChunk")
            .WithSummary("One fixed-size chunk of the extracted text (Document.View).")
            .WithDescription(
                "Chunk n covers the characters that start in bytes [n * chunkSizeBytes, (n + 1) * chunkSizeBytes) of the "
                + "stored UTF-8 text, so chunks never split a character and concatenate to the whole text. A document "
                + "without text answers chunk 0 with missing: true. purpose: display (default) or prefetch.")
            .Produces<DocumentTextChunkResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/text/hits", GetTextHitsAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("GetDocumentTextHits")
            .WithSummary("Term hits in the extracted text: the current search's and Highlight Sets' (Document.View).")
            .WithDescription(
                "Server-computed hit spans over the stored text, a page of whole chunks at a time (up to 16 chunks or about "
                + "5,000 hits): chunk-local UTF-16 offsets per hit and per-unit counts for the page; ask again with "
                + "fromChunk = nextChunk until it is null. searchId: the caller's search handle (its terms, phrases, "
                + "wildcards and W/n proximities, each phrase and proximity as one span; NOT clauses and other fields are "
                + "not highlighted); an expired or unknown handle answers 404 (run the search again). highlightSetId "
                + "(repeatable, at most 20; an unknown one answers 404): Highlight Sets to apply. Audited like a text retrieval; no text is returned.")
            .Produces<DocumentTextHitsResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/native", DownloadNativeAsync)
            .RequireDocumentPermission(Permission.DocumentDownloadNative)
            .WithName("DownloadDocumentNative")
            .WithSummary("Download the native file (Document.DownloadNative).")
            .WithDescription(
                "Always an attachment of type application/octet-stream. Streamed with single-range support, or a 303 to a "
                + "60-second, single-object presigned URL when the installation enables presigned native delivery.")
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "application/octet-stream")
            .Produces(StatusCodes.Status303SeeOther)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status416RangeNotSatisfiable);

        documents.MapGet("/text", GetTextAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("GetDocumentText")
            .WithSummary("The document's extracted text (Document.View).")
            .WithDescription("purpose: display (default) or prefetch. Prefetch is audited as such and never counts as viewed.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/plain")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "text/plain")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status416RangeNotSatisfiable);

        documents.MapGet("/pages/{pageNumber}/image", GetPageImageAsync)
            .RequireDocumentPermission(Permission.DocumentView, Permission.DocumentPrint)
            .WithName("GetDocumentPageImage")
            .WithSummary("A review image of a page of the active page set (Document.View; Document.Print with purpose=print).")
            .WithDescription("purpose: display (default), prefetch or print.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/png", Images)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/pages/{pageNumber}/thumbnail", GetThumbnailAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("GetDocumentPageThumbnail")
            .WithSummary("A thumbnail of a page of the active page set (Document.View).")
            .WithDescription("purpose: display (default) or prefetch.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/webp", "image/png", "image/jpeg")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapPost("/views", RecordViewAsync)
            .RequireDocumentPermission(Permission.DocumentView)
            .WithName("RecordDocumentView")
            .WithSummary("Record that the viewer displayed the document (Document.Viewed).")
            .WithDescription("Re-checks Document.View. Send the X-Opportunity-Retrieval-Id of the response that was displayed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static Task<IResult> GetDocumentAsync(
        string workspaceId,
        string documentId,
        string? purpose,
        HttpContext context,
        ProtectedContentGateway gateway,
        IDocumentViewerCatalog viewer,
        IFieldAccessFilter fieldAccess,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (ParsePurpose(purpose, allowPrint: false) is not { } p)
        {
            return Task.FromResult<IResult>(InvalidPurpose());
        }

        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var id))
        {
            return Task.FromResult(ProtectedContentGateway.DocumentNotFound());
        }

        return gateway.ReadAsync(
            context,
            workspace,
            new ContentRequest(workspace.WorkspaceId, id, ContentRendition.Metadata, p),
            async ct =>
            {
                if (await viewer.GetDocumentAsync(workspace.WorkspaceId, id, ct).ConfigureAwait(false) is not { } record)
                {
                    return null;
                }

                var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspace.WorkspaceId, workspace.Principal, record.Catalog, ct)
                    .ConfigureAwait(false);
                return DocumentResources.ToResource(record, restricted);
            },
            resource => TypedResults.Ok(resource),
            cancellationToken);
    }

    internal static Task<IResult> ListPagesAsync(
        string workspaceId,
        string documentId,
        string? purpose,
        string? cursor,
        int? limit,
        HttpContext context,
        ProtectedContentGateway gateway,
        IDocumentViewerCatalog viewer,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (ParsePurpose(purpose, allowPrint: false) is not { } p)
        {
            return Task.FromResult<IResult>(InvalidPurpose());
        }

        var size = limit ?? Pagination.MaxLimit;
        if (size is < 1 or > Pagination.MaxLimit)
        {
            return Task.FromResult<IResult>(Problems.Validation(new Dictionary<string, string[]>
            {
                ["limit"] = [$"Must be between 1 and {Pagination.MaxLimit}."],
            }));
        }

        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var id))
        {
            return Task.FromResult(ProtectedContentGateway.DocumentNotFound());
        }

        var after = 0;
        if (cursor is not null
            && (PageCursor.Decode(cursor, workspace.Principal.UserId, workspace.WorkspaceId, 2) is not [var cursorDocument, var position]
                || cursorDocument != id.ToString("N")
                || !int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out after)))
        {
            return Task.FromResult<IResult>(PageCursor.Invalid());
        }

        return gateway.ReadAsync(
            context,
            workspace,
            new ContentRequest(workspace.WorkspaceId, id, ContentRendition.PageList, p),
            ct => viewer.GetPagesAsync(workspace.WorkspaceId, id, after, size, ct),
            list =>
            {
                var items = list.Pages.Select(DocumentResources.ToResource).ToList();
                var total = list.ActivePageSet?.PageCount ?? 0;
                var next = items.Count == size && items[^1].PageNumber < total
                    ? PageCursor.Encode(workspace.Principal.UserId, workspace.WorkspaceId, id.ToString("N"),
                        items[^1].PageNumber.ToString(CultureInfo.InvariantCulture))
                    : null;
                return TypedResults.Ok(new CursorPage<DocumentPageResource>(items, next, new TotalCount(total, TotalRelation.Eq)));
            },
            cancellationToken);
    }

    internal static Task<IResult> GetTextChunkAsync(
        string workspaceId,
        string documentId,
        string chunkIndex,
        string? purpose,
        HttpContext context,
        ProtectedContentGateway gateway,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (ParsePurpose(purpose, allowPrint: false) is not { } p)
        {
            return Task.FromResult<IResult>(InvalidPurpose());
        }

        if (!int.TryParse(chunkIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var chunk))
        {
            return Task.FromResult<IResult>(Problems.Validation(new Dictionary<string, string[]>
            {
                ["chunkIndex"] = ["Must be a non-negative integer."],
            }));
        }

        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var id))
        {
            return Task.FromResult(ProtectedContentGateway.DocumentNotFound());
        }

        var request = new ContentRequest(workspace.WorkspaceId, id, ContentRendition.Text, p, TextChunk: chunk);
        return gateway.DeliverTextChunkAsync(context, workspace, request, cancellationToken);
    }

    internal static async Task<IResult> GetTextHitsAsync(
        string workspaceId,
        string documentId,
        [FromQuery] string? searchId,
        [FromQuery(Name = "highlightSetId")] string[]? highlightSetIds,
        [FromQuery] string? fromChunk,
        HttpContext context,
        ProtectedContentGateway gateway,
        TermHitUnitResolver resolver,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        var errors = new Dictionary<string, string[]>();
        var from = 0;
        if (fromChunk is not null && !int.TryParse(fromChunk, NumberStyles.None, CultureInfo.InvariantCulture, out from))
        {
            errors["fromChunk"] = ["Must be a non-negative integer."];
        }

        Guid? search = null;
        if (!string.IsNullOrEmpty(searchId))
        {
            if (Guid.TryParse(searchId, out var parsed))
            {
                search = parsed;
            }
            else
            {
                errors["searchId"] = ["Must be a search ID."];
            }
        }

        var sets = new List<Guid>();
        foreach (var value in highlightSetIds ?? [])
        {
            if (Guid.TryParse(value, out var id))
            {
                sets.Add(id);
            }
            else
            {
                errors["highlightSetId"] = ["Must be highlight set IDs."];
            }
        }

        if (sets.Distinct().Count() > TermHitUnitResolver.MaxSets)
        {
            errors["highlightSetId"] = [$"At most {TermHitUnitResolver.MaxSets} highlight sets per request."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var document))
        {
            return ProtectedContentGateway.DocumentNotFound();
        }

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var sid) ? sid : (Guid?)null;
        var units = await resolver.ResolveAsync(workspace.Principal, workspace.WorkspaceId, session, search, sets, cancellationToken).ConfigureAwait(false);
        switch (units.Status)
        {
            case TermHitUnitStatus.SearchNotFound:
                return Problems.NotFound("No such search; it may have expired. Run the search again.");
            case TermHitUnitStatus.HighlightSetNotFound:
                return Problems.NotFound("No such highlight set.");
        }

        var request = new ContentRequest(workspace.WorkspaceId, document, ContentRendition.Text, ContentPurpose.Display, TextChunk: from, Use: "termHits");
        return await gateway.DeliverTextHitsAsync(context, workspace, request, units, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<IResult> DownloadNativeAsync(
        string workspaceId, string documentId, HttpContext context, ProtectedContentGateway gateway, CancellationToken cancellationToken) =>
        DeliverAsync(context, gateway, documentId, ContentRendition.Native, ContentPurpose.Download, null, cancellationToken);

    internal static Task<IResult> GetTextAsync(
        string workspaceId, string documentId, string? purpose, HttpContext context, ProtectedContentGateway gateway, CancellationToken cancellationToken) =>
        ParsePurpose(purpose, allowPrint: false) is not { } p
            ? Task.FromResult<IResult>(InvalidPurpose())
            : DeliverAsync(context, gateway, documentId, ContentRendition.Text, p, null, cancellationToken);

    internal static Task<IResult> GetPageImageAsync(
        string workspaceId, string documentId, string pageNumber, string? purpose, HttpContext context, ProtectedContentGateway gateway,
        CancellationToken cancellationToken) =>
        ParsePurpose(purpose, allowPrint: true) is not { } p
            ? Task.FromResult<IResult>(InvalidPurpose())
            : DeliverAsync(context, gateway, documentId, ContentRendition.PageImage, p, pageNumber, cancellationToken);

    internal static Task<IResult> GetThumbnailAsync(
        string workspaceId, string documentId, string pageNumber, string? purpose, HttpContext context, ProtectedContentGateway gateway,
        CancellationToken cancellationToken) =>
        ParsePurpose(purpose, allowPrint: false) is not { } p
            ? Task.FromResult<IResult>(InvalidPurpose())
            : DeliverAsync(context, gateway, documentId, ContentRendition.Thumbnail, p, pageNumber, cancellationToken);

    internal static async Task<IResult> RecordViewAsync(
        string workspaceId, string documentId, DocumentViewRecord? body, HttpContext context, IDocumentAccessService access,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var id))
        {
            return ProtectedContentGateway.DocumentNotFound();
        }

        var decision = await access.RecordViewedAsync(workspace.Principal, workspace.WorkspaceId, id, body?.RetrievalId, cancellationToken)
            .ConfigureAwait(false);
        return decision.IsAllowed
            ? TypedResults.NoContent()
            : decision.Outcome == Application.Authorization.AuthorizationOutcome.NotFound
                ? ProtectedContentGateway.DocumentNotFound()
                : AuthorizationResults.Problem(decision);
    }

    private static async Task<IResult> DeliverAsync(
        HttpContext context,
        ProtectedContentGateway gateway,
        string documentId,
        ContentRendition rendition,
        ContentPurpose purpose,
        string? pageNumber,
        CancellationToken cancellationToken)
    {
        // PEP-1 has resolved and authorized the route's workspace; use its result, not the raw route value.
        if (context.GetWorkspaceAccess() is not { } workspace || !TryParseId(documentId, out var id))
        {
            return ProtectedContentGateway.DocumentNotFound();
        }

        // An unparsable page is page 0: it still goes through the PDP and the audit, and is then simply unavailable.
        int? page = pageNumber is null ? null : int.TryParse(pageNumber, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        var request = new ContentRequest(workspace.WorkspaceId, id, rendition, purpose, page, ProtectedContentGateway.ParseRange(context.Request));
        return await gateway.DeliverAsync(context, workspace, request, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static ContentPurpose? ParsePurpose(string? value, bool allowPrint) => value?.ToLowerInvariant() switch
    {
        null or "" or "display" => ContentPurpose.Display,
        "prefetch" => ContentPurpose.Prefetch,
        "print" when allowPrint => ContentPurpose.Print,
        _ => null,
    };

    private static ValidationProblem InvalidPurpose() =>
        Problems.Validation(new Dictionary<string, string[]> { ["purpose"] = ["Must be display, prefetch or (page images only) print."] });
}

public static class ContentEndpointRegistration
{
    /// <summary>
    /// The protected-content gateway: object storage (section <c>ObjectStorage</c>; without it content routes answer
    /// 503), the PostgreSQL content catalog, the access service and the endpoints.
    /// </summary>
    public static IServiceCollection AddProtectedContentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddValidatedOptions<ProtectedContentOptions>(ProtectedContentOptions.SectionName);
        var storage = configuration.GetSection(ObjectStorageOptions.SectionName);
        if (storage.Exists())
        {
            var options = new ObjectStorageOptions();
            storage.Bind(options);
            options.Validate();
            services.AddObjectStorage(options);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDocumentContentCatalog, DocumentContentCatalog>();
        services.TryAddSingleton<IDocumentViewerCatalog, DocumentViewerCatalog>();
        services.TryAddScoped<IDocumentAccessService, DocumentAccessService>();
        services.TryAddScoped<ProtectedContentGateway>();
        services.TryAddScoped<TermHitUnitResolver>();
        services.AddSingleton<IApiEndpointModule, ContentEndpoints>();
        return services;
    }
}
