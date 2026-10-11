using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Productions;
using Opportunity.Core.Security;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Productions;

public enum WhereProducedStatus
{
    Ok,
    NotFound,
    Forbidden,
}

/// <summary>
/// Where a document, its family and its duplicates were produced: every produced production's numbers of each, with the
/// production's completed volumes. <see cref="Documents"/> are the related documents the caller may see, produced or not.
/// </summary>
public sealed record WhereProducedResult(
    Guid DocumentId,
    IReadOnlyList<WhereProducedDocument> Documents,
    IReadOnlyList<ProducedDocumentRow> Rows,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Volumes,
    bool Truncated)
{
    public WhereProducedRelation RelationOf(Guid documentId) =>
        Documents.FirstOrDefault(d => d.DocumentId == documentId)?.Relation ?? WhereProducedRelation.None;
}

public sealed record WhereProducedOutcome(WhereProducedStatus Status, WhereProducedResult? Result = null);

/// <summary>What a cross-reference download holds.</summary>
public enum CrossReferenceScope
{
    /// <summary>Every produced production of the matter.</summary>
    Matter,

    /// <summary>One produced production.</summary>
    Production,

    /// <summary>A document, its family and its duplicates (the where-produced lookup).</summary>
    Document,
}

/// <summary>
/// The where-produced lookup and the Bates cross-reference (E13-T04, legal finding 3: clawback speed depends on finding
/// every volume and Bates range in which a document, its duplicates and its family were produced). Only produced
/// productions count: finalized ones and voided ones (they were produced before they were withdrawn); drafts never
/// issued numbers (Q-54). Documents the caller may not view are omitted entirely, never listed or counted (Q-52).
/// </summary>
public sealed class WhereProducedService(IWhereProducedStore store, IAuthorizationService authorization, TimeProvider time)
{
    /// <summary>The most related documents (the document, its family and its duplicates) one lookup considers.</summary>
    public const int MaxRelatedDocuments = 5_000;

    /// <summary>Rows per page of the matter-wide cross-reference.</summary>
    public const int PageSize = 1_000;

    public const string ContentType = "text/csv; charset=utf-8";

    public async Task<WhereProducedOutcome> LookupAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.DocumentView, documentId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new WhereProducedOutcome(decision.Outcome == AuthorizationOutcome.NotFound ? WhereProducedStatus.NotFound : WhereProducedStatus.Forbidden);
        }

        var related = await store.ReadRelatedAsync(workspaceId, documentId, MaxRelatedDocuments + 1, cancellationToken).ConfigureAwait(false);
        if (related is null)
        {
            return new WhereProducedOutcome(WhereProducedStatus.NotFound);
        }

        var truncated = related.Count > MaxRelatedDocuments;
        var visible = await VisibleAsync(principal, workspaceId, related.Take(MaxRelatedDocuments).Select(d => d.DocumentId), cancellationToken)
            .ConfigureAwait(false);
        visible.Add(documentId);
        List<WhereProducedDocument> documents = [.. related.Take(MaxRelatedDocuments).Where(d => visible.Contains(d.DocumentId))];
        var rows = await store.ReadProducedAsync(workspaceId, [.. documents.Select(d => d.DocumentId)], cancellationToken).ConfigureAwait(false);
        var volumes = await VolumesAsync(workspaceId, rows.Select(r => r.ProductionId), cancellationToken).ConfigureAwait(false);
        return new WhereProducedOutcome(WhereProducedStatus.Ok, new WhereProducedResult(documentId, documents, rows, volumes, truncated));
    }

    /// <summary>
    /// Writes the cross-reference CSV (RFC 4180, UTF-8 with a byte-order mark, formula-neutralized) of the matter, one
    /// production or a where-produced result; returns the number of rows written. Pages of the matter are re-authorized
    /// page by page, so memory stays bounded.
    /// </summary>
    public async Task<long> WriteCsvAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid? productionId, WhereProducedResult? lookup, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, LoadFileText.CsvRow(
            "Production", "Production Version", "Production Status", "Produced At (UTC)", "Voided At (UTC)", "Volumes", "Control Number",
            "Document ID", "Relation", "Output", "ProdBegBates", "ProdEndBates", "ProdBegAttach", "ProdEndAttach"), cancellationToken).ConfigureAwait(false);
        long written = 0;
        if (lookup is not null)
        {
            foreach (var row in lookup.Rows)
            {
                await WriteAsync(output, Row(row, lookup.Volumes, lookup.RelationOf(row.DocumentId)), cancellationToken).ConfigureAwait(false);
                written++;
            }
        }
        else
        {
            var volumes = new Dictionary<Guid, IReadOnlyList<string>>();
            ProducedDocumentCursor? after = null;
            while (true)
            {
                var page = await store.ReadProducedPageAsync(workspaceId, productionId, after, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                var unseen = page.Select(r => r.ProductionId).Where(id => !volumes.ContainsKey(id)).Distinct().ToList();
                foreach (var (id, names) in await VolumesAsync(workspaceId, unseen, cancellationToken).ConfigureAwait(false))
                {
                    volumes[id] = names;
                }

                var visible = await VisibleAsync(principal, workspaceId, page.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
                var text = new StringBuilder();
                foreach (var row in page.Where(r => visible.Contains(r.DocumentId)))
                {
                    text.Append(Row(row, volumes, WhereProducedRelation.None));
                    written++;
                }

                await WriteAsync(output, text.ToString(), cancellationToken).ConfigureAwait(false);
                if (page.Count < PageSize)
                {
                    break;
                }

                after = new ProducedDocumentCursor(page[^1].FinalizedAt, page[^1].ProductionId, page[^1].Sequence);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    public Task<bool> IsProducedAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default) =>
        store.IsProducedAsync(workspaceId, productionId, cancellationToken);

    /// <summary>The <c>Production.CrossReferenceExported</c> event of a download (written before its first byte).</summary>
    public AuditEvent ExportAudit(SecurityPrincipal principal, Guid workspaceId, CrossReferenceScope scope, Guid? productionId, Guid? documentId, long rows)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            WorkspaceId = workspaceId,
            Category = AuditTaxonomy.Production.Category,
            Action = AuditTaxonomy.Production.CrossReferenceExported,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId,
            Outcome = AuditOutcome.Success,
            ResourceType = productionId is null ? null : AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId?.ToString(),
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Scope"] = scope.ToString(),
                ["ProductionId"] = productionId?.ToString(),
                ["DocumentId"] = documentId?.ToString(),
                ["Rows"] = rows.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    public string FileName(CrossReferenceScope scope, string? controlNumber) =>
        "bates-cross-reference" + (scope == CrossReferenceScope.Document && controlNumber is not null ? "-" + SafeName(controlNumber) : string.Empty)
        + "-" + time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

    public static IEnumerable<string> RelationNames(WhereProducedRelation relation)
    {
        if (relation.HasFlag(WhereProducedRelation.Self))
        {
            yield return "Self";
        }

        if (relation.HasFlag(WhereProducedRelation.Family))
        {
            yield return "Family";
        }

        if (relation.HasFlag(WhereProducedRelation.Duplicate))
        {
            yield return "Duplicate";
        }
    }

    private static string Row(ProducedDocumentRow row, IReadOnlyDictionary<Guid, IReadOnlyList<string>> volumes, WhereProducedRelation relation) =>
        LoadFileText.CsvRow(
            row.ProductionName,
            row.ProductionVersion.ToString(CultureInfo.InvariantCulture),
            row.ProductionStatus.ToString(),
            Utc(row.FinalizedAt),
            row.VoidedAt is { } voided ? Utc(voided) : string.Empty,
            string.Join("; ", volumes.GetValueOrDefault(row.ProductionId) ?? []),
            row.ControlNumber ?? string.Empty,
            row.DocumentId.ToString("D"),
            string.Join("; ", RelationNames(relation)),
            row.Output.ToString(),
            row.ProdBegBates,
            row.ProdEndBates,
            row.ProdBegAttach,
            row.ProdEndAttach);

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string SafeName(string value)
    {
        var name = new string([.. value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return name.Length <= 80 ? name : name[..80];
    }

    private static Task WriteAsync(Stream output, string text, CancellationToken cancellationToken) =>
        output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).AsTask();

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> VolumesAsync(Guid workspaceId, IEnumerable<Guid> productionIds, CancellationToken cancellationToken)
    {
        var ids = productionIds.Distinct().ToList();
        var runs = await store.ReadVolumesAsync(workspaceId, ids, cancellationToken).ConfigureAwait(false);
        var volumes = ids.ToDictionary(id => id, id => (IReadOnlyList<string>)
            [.. runs.Where(r => r.ProductionId == id).Select(r => ExportSettings.Deserialize(r.SettingsJson).VolumeName).Distinct(StringComparer.Ordinal)]);
        return volumes;
    }

    private async Task<HashSet<Guid>> VisibleAsync(SecurityPrincipal principal, Guid workspaceId, IEnumerable<Guid> documentIds, CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, ids, DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        return [.. decisions.Where(d => d.Value.IsAllowed).Select(d => d.Key)];
    }
}

public static class WhereProducedServiceRegistration
{
    public static IServiceCollection AddWhereProducedService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<WhereProducedService>();
        return services;
    }
}
