using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Content;
using Opportunity.Contracts.Api;
using Opportunity.Data.Documents;
using Opportunity.Hosting.Options;
using Opportunity.Security.Authorization;
using Opportunity.Storage;

namespace Opportunity.Api.Content;

/// <summary>
/// Document content routes of the protected-content gateway (E05-T04). PEP-1 checks workspace membership only; the
/// gateway decides the document-level permission itself (view, print and native download are distinct permissions,
/// Q-18) so that every attempt, allowed or denied, is audited as one <c>Document.*</c> event with the document ID,
/// rendition and outcome. A malformed, unknown, deleted, restricted or walled document gets the same 404.
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
            .RequireWorkspaceMember()
            .WithMetadata(ProtectedContentEndpointMetadata.Instance);

        documents.MapGet("/native", DownloadNativeAsync)
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
            .WithName("GetDocumentText")
            .WithSummary("The document's extracted text (Document.View).")
            .WithDescription("purpose: display (default) or prefetch. Prefetch is audited as such and never counts as viewed.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/plain")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "text/plain")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status416RangeNotSatisfiable);

        documents.MapGet("/pages/{pageNumber}/image", GetPageImageAsync)
            .WithName("GetDocumentPageImage")
            .WithSummary("A review image of a page of the active page set (Document.View; Document.Print with purpose=print).")
            .WithDescription("purpose: display (default), prefetch or print.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/png", Images)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/pages/{pageNumber}/thumbnail", GetThumbnailAsync)
            .WithName("GetDocumentPageThumbnail")
            .WithSummary("A thumbnail of a page of the active page set (Document.View).")
            .WithDescription("purpose: display (default) or prefetch.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/webp", "image/png", "image/jpeg")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapPost("/views", RecordViewAsync)
            .WithName("RecordDocumentView")
            .WithSummary("Record that the viewer displayed the document (Document.Viewed).")
            .WithDescription("Re-checks Document.View. Send the X-Opportunity-Retrieval-Id of the response that was displayed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
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
        services.TryAddScoped<IDocumentAccessService, DocumentAccessService>();
        services.TryAddScoped<ProtectedContentGateway>();
        services.AddSingleton<IApiEndpointModule, ContentEndpoints>();
        return services;
    }
}
