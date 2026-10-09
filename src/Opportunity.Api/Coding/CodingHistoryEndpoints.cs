using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Content;
using Opportunity.Api.Conventions;
using Opportunity.Application.Coding;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Coding;

/// <summary>
/// Document coding history (E10-T05): <c>GET …/documents/{documentId}/coding-history</c> lists the document's field
/// changes newest first (field, old and new value, who, when, actor type, interactive or the job), cursor-paged and
/// optionally for one field. Document.View on the document (the PDP: hidden or unknown → the document 404); fields
/// hidden from the caller are never listed (E05-T06). Reading history changes nothing and is not audited.
/// </summary>
public sealed class CodingHistoryEndpoints : IApiEndpointModule
{
    public const string Path = "/documents/{documentId}/coding-history";

    private const string NotFoundDetail = ProtectedContentGateway.DocumentNotFoundDetail;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, GetAsync)
            .WithName("GetDocumentCodingHistory")
            .WithTags("Coding")
            .WithSummary("A document's coding history, newest first: field, old and new value, actor, time and job or interactive.")
            .WithDescription(
                "Built from CodingEvent provenance. source is interactive (jobId null) or job; actorType tells human review from " +
                "bulk jobs, system rules and models. Fields you may not see are never listed; fieldId narrows to one field.")
            .Produces<CursorPage<CodingHistoryEntryResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId,
        string documentId,
        [FromQuery] int? fieldId,
        [AsParameters] PageQuery page,
        HttpContext context,
        CodingHistoryService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(documentId, out var id) || id == Guid.Empty)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        CodingEventCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var docText, var atText, var eventText]
                || docText != id.ToString("N")
                || !long.TryParse(atText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParse(eventText, out var eventId))
            {
                return PageCursor.Invalid();
            }

            after = new CodingEventCursor(new DateTimeOffset(ticks, TimeSpan.Zero), eventId);
        }

        var result = await service.GetAsync(new CodingCaller(access.Principal, access.WorkspaceId), id, fieldId, after,
            Math.Min(page.EffectiveLimit, CodingHistoryService.MaxLimit), cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case CodingHistoryStatus.NotFound:
                return Problems.NotFound(NotFoundDetail);
            case CodingHistoryStatus.UnknownField:
                return Problems.Validation(new Dictionary<string, string[]> { ["fieldId"] = ["The field does not exist in this workspace."] });
        }

        var items = result.Entries.Select(ToResource).ToList();
        var next = result.Next is { } n
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, id.ToString("N"),
                n.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture), n.EventId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<CodingHistoryEntryResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static CodingHistoryEntryResource ToResource(CodingHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var e = entry.Event;
        return new CodingHistoryEntryResource(
            e.EventId,
            e.OccurredAt,
            e.FieldId,
            entry.Field.Name,
            e.Kind == CodingEventKind.BulkSkippedConcurrentEdit ? CodingHistoryKindResource.SkippedConcurrentEdit : CodingHistoryKindResource.Changed,
            e.PriorValue?.DeepClone(),
            e.NewValue?.DeepClone(),
            e.DocumentVersion,
            new CodingHistoryActorResource(e.ActorId, entry.ActorDisplayName),
            e.ActorType switch
            {
                CodingActorType.BulkHuman => CodingActorTypeResource.BulkHuman,
                CodingActorType.SystemRule => CodingActorTypeResource.SystemRule,
                CodingActorType.Model => CodingActorTypeResource.Model,
                _ => CodingActorTypeResource.Human,
            },
            e.JobId is null ? CodingChangeSourceResource.Interactive : CodingChangeSourceResource.Job,
            e.JobId,
            e.OriginEventId);
    }
}

public static class CodingHistoryEndpointRegistration
{
    /// <summary>
    /// The coding history endpoint and use case; the coding store, field catalogue and field access are registered by
    /// <see cref="CodingEndpointRegistration.AddCodingEndpoints"/>.
    /// </summary>
    public static IServiceCollection AddCodingHistoryEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<CodingHistoryService>();
        services.AddSingleton<IApiEndpointModule, CodingHistoryEndpoints>();
        return services;
    }
}
