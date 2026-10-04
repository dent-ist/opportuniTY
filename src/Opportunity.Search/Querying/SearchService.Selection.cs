using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Security;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>Snapshot selection (ADR-002 §5.1): every matching ID under one reader, re-checked page by page (Q-12).</summary>
internal sealed partial class SearchService
{
    private const string IntegrityReason = "Integrity";

    public async Task<SearchSelectionOutcome> SelectAsync(
        SearchCaller caller,
        SearchSelectionRequest request,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> onPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onPage);
        ArgumentNullException.ThrowIfNull(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MaxHits);
        if (request.PageSize < 1 || request.PageSize > Settings.MaxSelectionPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.PageSize, $"PageSize must be 1–{Settings.MaxSelectionPageSize}.");
        }

        var visibility = await VisibilityAsync(caller, cancellationToken).ConfigureAwait(false);
        if (visibility.Outcome is { } denied)
        {
            return new SearchSelectionOutcome
            {
                Status = denied.Status == SearchStatus.NotFound ? SearchSelectionStatus.NotFound : SearchSelectionStatus.Forbidden,
            };
        }

        var placement = await PlacementAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(caller.WorkspaceId, request.Query, placement?.Generation, cancellationToken).ConfigureAwait(false);
        if (plan.Errors is { } errors)
        {
            return new SearchSelectionOutcome { Status = SearchSelectionStatus.InvalidQuery, QueryErrors = errors };
        }

        var outcome = new SearchSelectionOutcome
        {
            Status = SearchSelectionStatus.Ok,
            Normalized = plan.Normalized,
            AstJson = QueryAstJson.Serialize(plan.Ast!),
            BreakGlass = visibility.Filter!.BreakGlass,
            ProjectionGeneration = placement?.Generation,
        };
        if (placement is null)
        {
            // Nothing was ever indexed for this workspace: nothing matches.
            return outcome;
        }

        if (request.RefreshFirst)
        {
            await connection.SendAsync(HttpMethod.Post, $"{Escape(placement.Read.Index)}/_refresh", null, cancellationToken).ConfigureAwait(false);
        }

        var query = SearchDsl.Query(placement.WorkspaceFilterValue, visibility.Filter, plan.Query!);
        var routing = placement.Read.Routing is { } r ? "&routing=" + Escape(r) : string.Empty;
        var opened = await connection.SendAsync(
            HttpMethod.Post,
            $"{Escape(placement.Read.Index)}/_search/point_in_time?keep_alive={SearchDsl.Seconds(Settings.SelectionKeepAlive)}{routing}",
            null,
            cancellationToken).ConfigureAwait(false);
        var pit = opened.Body?["pit_id"]?.GetValue<string>() ?? throw new OpenSearchRequestException("OpenSearch did not return a point-in-time id.");

        var dropped = new Dictionary<string, long>(StringComparer.Ordinal);
        long hits = 0, selected = 0;
        try
        {
            JsonArray? after = null;
            var first = true;
            while (true)
            {
                var body = Obj(
                    ("query", query.DeepClone()),
                    ("size", request.PageSize),
                    ("sort", new JsonArray(Obj((ProjectionFields.DocumentId, Obj(("order", "asc")))))),
                    ("track_total_hits", first),
                    ("_source", false),
                    ("docvalue_fields", new JsonArray((JsonNode)ProjectionFields.WorkspaceId, (JsonNode)ProjectionFields.DocumentId)),
                    ("timeout", string.Create(CultureInfo.InvariantCulture, $"{(long)Settings.QueryTimeout.TotalMilliseconds}ms")),
                    ("pit", Obj(("id", pit), ("keep_alive", SearchDsl.Seconds(Settings.SelectionKeepAlive)))));
                if (after is not null)
                {
                    body["search_after"] = after;
                }

                var response = await connection.SendAsync(HttpMethod.Post, "_search", body, cancellationToken, HttpStatusCode.NotFound)
                    .ConfigureAwait(false);
                if (IsPointInTimeGone(response) || response.Status == HttpStatusCode.NotFound)
                {
                    LogSelectionReaderLost(logger, caller.WorkspaceId);
                    return outcome with { Status = SearchSelectionStatus.ReaderLost, Hits = hits, Selected = selected, Dropped = dropped };
                }

                var json = response.Body as JsonObject ?? throw new OpenSearchRequestException("OpenSearch returned no search body.");
                if (json["timed_out"]?.GetValue<bool>() == true || (json["_shards"]?["failed"]?.GetValue<int>() ?? 0) > 0)
                {
                    // A partial page would silently drop members: never acceptable for a frozen set.
                    throw new OpenSearchRequestException("A selection page timed out or missed shards; the selection was abandoned.");
                }

                pit = json["pit_id"]?.GetValue<string>() ?? pit;
                if (first)
                {
                    hits = json["hits"]?["total"]?["value"]?.GetValue<long>() ?? 0;
                    first = false;
                    if (hits > request.MaxHits)
                    {
                        return outcome with { Status = SearchSelectionStatus.TooManyHits, Hits = hits };
                    }
                }

                var page = json["hits"]?["hits"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
                if (page.Count == 0)
                {
                    break;
                }

                var ids = await SelectionPostFilterAsync(caller, placement, page, dropped, cancellationToken).ConfigureAwait(false);
                if (ids.Count > 0)
                {
                    await onPage(ids, cancellationToken).ConfigureAwait(false);
                    selected += ids.Count;
                }

                if (page.Count < request.PageSize)
                {
                    break;
                }

                after = page[^1]["sort"]?.DeepClone().AsArray();
            }
        }
        finally
        {
            await ClosePointInTimeAsync(pit).ConfigureAwait(false);
        }

        await AuditSelectionDenialsAsync(caller, visibility.Filter.BreakGlass, dropped, selected, cancellationToken).ConfigureAwait(false);
        return outcome with { Hits = hits, Selected = selected, Dropped = dropped };
    }

    /// <summary>Q-12 for one selection page: only IDs the PDP allows <c>Document.View</c> on now are delivered.</summary>
    private async Task<IReadOnlyList<Guid>> SelectionPostFilterAsync(
        SearchCaller caller, Placement placement, List<JsonObject> hits, Dictionary<string, long> dropped, CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>(hits.Count);
        var integrity = 0;
        foreach (var hit in hits)
        {
            var fields = hit["fields"] as JsonObject;
            if (FirstValue(fields, ProjectionFields.WorkspaceId) != placement.WorkspaceFilterValue
                || !Guid.TryParse(FirstValue(fields, ProjectionFields.DocumentId), out var documentId))
            {
                integrity++;
                continue;
            }

            candidates.Add(documentId);
        }

        if (integrity > 0)
        {
            LogIntegrityDrop(logger, caller.WorkspaceId, integrity);
            Dropped(integrity, "integrity");
            dropped[IntegrityReason] = dropped.GetValueOrDefault(IntegrityReason) + integrity;
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var decisions = await authorization.AuthorizeManyAsync(
            caller.Principal, caller.WorkspaceId, Permission.DocumentView, candidates, DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        var allowed = new List<Guid>(candidates.Count);
        foreach (var id in candidates)
        {
            if (decisions.TryGetValue(id, out var decision) && decision.IsAllowed)
            {
                allowed.Add(id);
            }
            else
            {
                var reason = decisions.TryGetValue(id, out var d) ? d.Reason : AuthorizationReasons.DocumentNotFound;
                Dropped(1, reason);
                dropped[reason] = dropped.GetValueOrDefault(reason) + 1;
            }
        }

        return allowed;
    }

    private static string? FirstValue(JsonObject? fields, string name) =>
        fields?[name] is JsonArray { Count: > 0 } values && values[0] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task ClosePointInTimeAsync(string pit)
    {
        try
        {
            await connection.SendAsync(HttpMethod.Delete, "_search/point_in_time", Obj(("pit_id", new JsonArray(pit))), CancellationToken.None,
                HttpStatusCode.NotFound).ConfigureAwait(false);
        }
        catch (OpenSearchRequestException ex)
        {
            // The reader expires on its own within the keep-alive; failing to close it early is not an error.
            LogSelectionReaderNotClosed(logger, ex.Message);
        }
    }

    /// <summary>One summary <c>AuthZ.Denied</c> for every hit the selection's re-check dropped (Q-59), none when nothing was.</summary>
    private Task AuditSelectionDenialsAsync(
        SearchCaller caller, bool breakGlass, Dictionary<string, long> dropped, long selected, CancellationToken cancellationToken)
    {
        var denied = dropped.Where(d => d.Key != IntegrityReason && d.Value > 0).ToList();
        if (denied.Count == 0)
        {
            return Task.CompletedTask;
        }

        var total = denied.Sum(d => d.Value);
        var details = new Dictionary<string, string?>
        {
            ["permission"] = Permission.DocumentView.Name(),
            ["requested"] = Invariant(selected + total),
            ["denied"] = Invariant(total),
            ["context"] = "SnapshotSelection",
        };
        foreach (var (reason, count) in denied.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            details["denied." + reason] = Invariant(count);
        }

        return WriteAuditAsync(caller, breakGlass, AuditTaxonomy.AuthZ.Category, AuditTaxonomy.AuthZ.Denied, null, details, null, cancellationToken,
            AuditOutcome.Denied, denied.Count == 1 ? denied[0].Key : "Multiple");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot selection in workspace {WorkspaceId} lost its point-in-time reader")]
    private static partial void LogSelectionReaderLost(ILogger logger, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Selection reader not closed early: {Reason}")]
    private static partial void LogSelectionReaderNotClosed(ILogger logger, string reason);
}
