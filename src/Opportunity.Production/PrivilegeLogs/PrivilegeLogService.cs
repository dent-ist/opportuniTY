using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Content;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.PrivilegeLogs;
using Opportunity.Application.Productions;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Production.PrivilegeLogs;

public enum PrivilegeLogStatus
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,

    /// <summary>The source is in the wrong state (a draft or voided production, a frozen set that is not Ready): 409.</summary>
    InvalidState,

    /// <summary>More candidates than one log holds: 422.</summary>
    TooLarge,

    NameConflict,
    VersionConflict,

    /// <summary>A rendered file does not match the SHA-256 recorded with its version: 500, nothing is sent.</summary>
    IntegrityFailure,
}

public sealed record PrivilegeLogOutcome<T>(PrivilegeLogStatus Status, T? Value = default, IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Reason = null);

public static class PrivilegeLogOutcome
{
    public static PrivilegeLogOutcome<T> Ok<T>(T value) => new(PrivilegeLogStatus.Ok, value);

    public static PrivilegeLogOutcome<T> Of<T>(PrivilegeLogStatus status, string? reason = null) => new(status, default, null, reason);

    public static PrivilegeLogOutcome<T> Invalid<T>(string key, string message) =>
        new(PrivilegeLogStatus.Invalid, default, new Dictionary<string, string[]> { [key] = [message] });

    public static PrivilegeLogOutcome<T> Invalid<T>(IReadOnlyDictionary<string, string[]> errors) => new(PrivilegeLogStatus.Invalid, default, errors);
}

/// <summary>A rendered log file, verified against its version's SHA-256.</summary>
public sealed record PrivilegeLogFile(PrivilegeLogRecord Log, string Format, byte[] Content, string ContentType, string FileName);

/// <summary>Templates with the presets resolved for the caller.</summary>
public sealed record PrivilegeLogTemplates(IReadOnlyList<PrivilegeLogTemplateRecord> Templates, IReadOnlyList<(PrivilegeLogPreset Preset, PrivilegeLogTemplateDefinition Definition)> Presets);

/// <summary>
/// Privilege logs (E13-T03; §14; FRCP 26(b)(5)(A); Q-19, Q-20): generated, versioned artifacts tied to a finalized
/// production (and its frozen set) or to a frozen set.
/// <list type="bullet">
/// <item>Which documents: <see cref="PrivilegeLogRules.Classify"/> over the candidates the store reads set-based in
/// PostgreSQL; each appears at most once (the entry table's unique key) and only documents the generator may see take
/// part (Q-52: the others are neither listed nor counted). Recorded exclusion rules leave matching documents off and are
/// written, with their counts, into the version's metadata.</item>
/// <item>Versions: the entries' cells and the metadata are frozen with the version, so its CSV and XLSX are pure
/// functions of stored data; their SHA-256 are recorded and every download is verified against them before a byte is
/// sent. A generation whose content equals the series' latest version returns that version unchanged.</item>
/// <item>Access: <c>PrivilegeLog.Generate</c> (PEP-1); generating from a production also needs <c>Production.Create</c>.
/// A version is shown only to a reader who may see every document it lists and every field it reads (field-level
/// restrictions); to anyone else it does not exist.</item>
/// </list>
/// </summary>
public sealed class PrivilegeLogService(
    IPrivilegeLogStore store,
    IProductionStore productions,
    IDocumentSetSnapshotStore snapshots,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    IExportStore documents,
    TimeProvider time)
{
    /// <summary>The most documents one log lists (and candidates it considers).</summary>
    public const int MaxEntries = 50_000;

    private const int Batch = 5_000;

    // ------------------------------------------------------------------------------------------------ templates

    public async Task<PrivilegeLogOutcome<PrivilegeLogTemplates>> ListTemplatesAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (catalog, restricted) = await CatalogAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var templates = await store.ListTemplatesAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var presets = Enum.GetValues<PrivilegeLogPreset>()
            .Select(p => (p, PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(Preset: p), catalog, restricted).Definition!))
            .ToList();
        return PrivilegeLogOutcome.Ok<PrivilegeLogTemplates>(new PrivilegeLogTemplates(templates, presets));
    }

    public Task<PrivilegeLogTemplateRecord?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken = default) =>
        store.GetTemplateAsync(workspaceId, templateId, cancellationToken);

    public async Task<PrivilegeLogOutcome<PrivilegeLogTemplateRecord>> SaveTemplateAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid? templateId, long? expectedVersion, PrivilegeLogTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (request is null)
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogTemplateRecord>("body", "Send the template.");
        }

        var name = PrivilegeLogTemplateRules.NormalizeName(request.Name);
        var (catalog, restricted) = await CatalogAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var (definition, errors) = PrivilegeLogTemplateRules.Normalize(request.Definition, catalog, restricted);
        if (name is null || definition is null)
        {
            var all = errors.ToDictionary(e => "definition." + e.Key, e => e.Value, StringComparer.Ordinal);
            if (name is null)
            {
                all["name"] = [$"A name has 1 to {PrivilegeLogTemplateRules.MaxNameLength} characters and no control characters."];
            }

            return PrivilegeLogOutcome.Invalid<PrivilegeLogTemplateRecord>(all);
        }

        var json = PrivilegeLogTemplateRules.Serialize(definition);
        var id = templateId ?? Guid.CreateVersion7();
        var audit = Event(principal, workspaceId, templateId is null ? AuditTaxonomy.Privilege.LogTemplateCreated : AuditTaxonomy.Privilege.LogTemplateModified)
            with
        {
            ResourceType = AuditTaxonomy.Privilege.LogTemplateResourceType,
            ResourceId = id.ToString(),
            Details = new Dictionary<string, string?>
            {
                ["Name"] = name,
                ["Columns"] = Invariant(definition.Columns!.Count),
                ["ExclusionRules"] = Invariant(definition.ExclusionRules!.Count),
                ["IncludePrivacyRedactions"] = definition.IncludePrivacyRedactions ? "true" : "false",
            },
        };
        var display = DisplayOf(principal);
        var write = templateId is null
            ? await store.CreateTemplateAsync(workspaceId, id, name, json, principal.UserId, display, audit, cancellationToken).ConfigureAwait(false)
            : await store.UpdateTemplateAsync(workspaceId, id, expectedVersion ?? 0, name, json, principal.UserId, display, audit, cancellationToken)
                .ConfigureAwait(false);
        return write.Status switch
        {
            PrivilegeLogTemplateWriteStatus.Applied => PrivilegeLogOutcome.Ok<PrivilegeLogTemplateRecord>(write.Template!),
            PrivilegeLogTemplateWriteStatus.NameConflict => PrivilegeLogOutcome.Of<PrivilegeLogTemplateRecord>(PrivilegeLogStatus.NameConflict),
            PrivilegeLogTemplateWriteStatus.VersionConflict => PrivilegeLogOutcome.Of<PrivilegeLogTemplateRecord>(PrivilegeLogStatus.VersionConflict),
            _ => PrivilegeLogOutcome.Of<PrivilegeLogTemplateRecord>(PrivilegeLogStatus.NotFound),
        };
    }

    // ------------------------------------------------------------------------------------------------ generation

    public async Task<PrivilegeLogOutcome<PrivilegeLogCreation>> GenerateAsync(
        SecurityPrincipal principal, Guid workspaceId, GeneratePrivilegeLogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (request is null || (request.ProductionId is null && request.SnapshotId is null))
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogCreation>("productionId", "Name a finalized production (productionId) or a frozen set (snapshotId).");
        }

        if (request.TemplateId is not null && request.Preset is not null)
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogCreation>("preset", "Give a template or a preset, not both.");
        }

        if (request.Preset is { } p && !Enum.IsDefined(p))
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogCreation>("preset", "Use documentByDocument or metadataOnly.");
        }

        if (!(await authorization.AuthorizeAsync(principal, workspaceId, Permission.PrivilegeLogGenerate, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.Forbidden);
        }

        var (catalog, restricted) = await CatalogAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (catalog.Find(PrivilegeFields.Status) is not { IsSystem: true, IsDeleted: false } || restricted.Contains(PrivilegeFields.Status))
        {
            // The log is about Privilege Status values the caller may not read.
            return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.Forbidden);
        }

        // The source.
        ProductionRecord? production = null;
        SnapshotRecord? reviewSet = null;
        Guid snapshotId;
        if (request.ProductionId is { } productionId)
        {
            if (!(await authorization.AuthorizeAsync(principal, workspaceId, Permission.ProductionCreate, cancellationToken).ConfigureAwait(false)).IsAllowed)
            {
                return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.Forbidden);
            }

            production = await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false);
            if (production is null)
            {
                return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.NotFound, "No such production.");
            }

            if (production.Status != ProductionStatus.Finalized)
            {
                return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.InvalidState,
                    "A privilege log is generated from a finalized production: its members, Bates numbers and redactions are frozen.");
            }

            snapshotId = production.SnapshotId;
            if (request.SnapshotId is { } scopeId)
            {
                var (scope, refusal) = await ReadySnapshotAsync(principal, workspaceId, scopeId, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return refusal;
                }

                reviewSet = scope;
            }
        }
        else
        {
            var (scope, refusal) = await ReadySnapshotAsync(principal, workspaceId, request.SnapshotId!.Value, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            snapshotId = scope!.SnapshotId;
        }

        // The template, normalized against the current catalogue as the caller sees it.
        PrivilegeLogTemplateRecord? template = null;
        PrivilegeLogTemplateDefinition input;
        if (request.TemplateId is { } templateId)
        {
            template = await store.GetTemplateAsync(workspaceId, templateId, cancellationToken).ConfigureAwait(false);
            if (template is null)
            {
                return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.NotFound, "No such privilege log template.");
            }

            input = PrivilegeLogTemplateRules.Deserialize(template.DefinitionJson);
        }
        else
        {
            input = new PrivilegeLogTemplateDefinition(Preset: request.Preset ?? PrivilegeLogPreset.DocumentByDocument);
        }

        var (definition, errors) = PrivilegeLogTemplateRules.Normalize(input, catalog, restricted);
        if (definition is null)
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogCreation>(errors.ToDictionary(e => "template." + e.Key, e => e.Value, StringComparer.Ordinal));
        }

        var source = production is null ? PrivilegeLogSource.Snapshot : PrivilegeLogSource.Production;
        var candidates = await store.ReadCandidatesAsync(
            new PrivilegeLogCandidateQuery(workspaceId, production?.ProductionId, snapshotId, reviewSet?.SnapshotId, MaxEntries + 1), cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count > MaxEntries)
        {
            return PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.TooLarge,
                $"More than {MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} documents may belong on this log; generate it from smaller productions or frozen sets.");
        }

        // Q-52: only documents the caller may see take part.
        var visible = await VisibleAsync(principal, workspaceId, candidates.Select(c => c.DocumentId).ToList(), DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        var listed = new List<(PrivilegeLogCandidate Candidate, PrivilegeLogEntryTreatment Treatment)>();
        foreach (var candidate in candidates)
        {
            if (visible.Contains(candidate.DocumentId)
                && PrivilegeLogRules.Classify(candidate, source, definition.IncludePrivacyRedactions) is { } treatment)
            {
                listed.Add((candidate, treatment));
            }
        }

        var sources = await ReadDocumentsAsync(workspaceId, listed.Select(l => l.Candidate.DocumentId).ToList(), cancellationToken).ConfigureAwait(false);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone!);

        // Recorded exclusion rules: the first rule a document matches excludes it.
        var rules = definition.ExclusionRules!;
        var excludedBy = new int[rules.Count];
        var kept = new List<(PrivilegeLogCandidate Candidate, PrivilegeLogEntryTreatment Treatment, ExportSourceDocument? Source)>();
        foreach (var (candidate, treatment) in listed)
        {
            var document = sources.GetValueOrDefault(candidate.DocumentId);
            var rule = document is null ? -1 : FirstMatchingRule(rules, document, catalog, zone);
            if (rule >= 0)
            {
                excludedBy[rule]++;
                continue;
            }

            kept.Add((candidate, treatment, document));
        }

        // Log identifiers: produced Bates (or a placeholder's), else the next Priv ID.
        var privIds = new Dictionary<Guid, (string Begin, string End)>();
        var nextPrivId = definition.PrivIdStart!.Value;
        foreach (var (candidate, _, _) in kept)
        {
            privIds[candidate.DocumentId] = candidate.Member is { ProdBegBates: { } begin } member
                ? (begin, member.ProdEndBates ?? begin)
                : (PrivilegeLogRules.PrivId(definition.PrivIdPrefix!, nextPrivId, definition.PrivIdPadding!.Value),
                    PrivilegeLogRules.PrivId(definition.PrivIdPrefix!, nextPrivId++, definition.PrivIdPadding!.Value));
        }

        var families = await FamilyRangesAsync(principal, workspaceId, production?.ProductionId, kept.Select(k => k.Candidate).ToList(), privIds,
            definition.Columns!.Any(c => c.Kind == PrivilegeLogColumnKind.FamilyRange), cancellationToken).ConfigureAwait(false);

        var subject = PrivilegeLogTemplateRules.FindByName(catalog, "Subject") is { } s && !restricted.Contains(s.FieldId) ? s : null;
        var formatter = new FieldFormatter(catalog, definition.MultiValueSeparator!, definition.DateFormat!, zone);
        var entries = new List<PrivilegeLogEntryRow>(kept.Count);
        foreach (var (candidate, treatment, document) in kept)
        {
            var cells = definition.Columns!.Select(column => PrivilegeLogRules.Clean(Cell(column, candidate, treatment, document, privIds, families,
                formatter, subject, definition.MultiValueSeparator!))).ToArray();
            entries.Add(new PrivilegeLogEntryRow(entries.Count + 1, candidate.DocumentId, treatment, cells));
        }

        var metadata = new PrivilegeLogMetadata(
            PrivilegeLogFiles.FormatVersion,
            source == PrivilegeLogSource.Production ? PrivilegeLogSourceKind.Production : PrivilegeLogSourceKind.Snapshot,
            production?.ProductionId,
            production?.Name,
            production?.Version,
            snapshotId,
            reviewSet?.SnapshotId,
            template?.TemplateId,
            template?.Name ?? PrivilegeLogTemplateRules.PresetName(definition.Preset!.Value),
            [.. definition.Columns!.Select(c => c.Header!)],
            definition.IncludePrivacyRedactions,
            [.. rules.Select((r, i) => Applied(r, excludedBy[i], catalog))],
            entries.Count,
            entries.Count(e => e.Treatment == PrivilegeLogEntryTreatment.Withheld),
            entries.Count(e => e.Treatment == PrivilegeLogEntryTreatment.Redacted),
            entries.Count(e => e.Treatment == PrivilegeLogEntryTreatment.RedactedPrivacy),
            excludedBy.Sum());
        var metadataJson = PrivilegeLogFiles.SerializeMetadata(metadata);
        // Render from the stored forms exactly as a download will.
        var stored = PrivilegeLogFiles.DeserializeMetadata(metadataJson);
        var csv = PrivilegeLogFiles.Csv(stored, entries.Select(e => e.Cells));
        var xlsx = PrivilegeLogFiles.Xlsx(stored, entries.Select(e => e.Cells));
        var content = PrivilegeLogFiles.ContentSha256(metadataJson, csv);

        var seriesKey = string.Join('|',
            production is null ? "snapshot:" + snapshotId.ToString("N") : "production:" + production.ProductionId.ToString("N"),
            reviewSet is null ? "-" : "review:" + reviewSet.SnapshotId.ToString("N"),
            template is null ? "preset:" + definition.Preset!.Value.ToString() : "template:" + template.TemplateId.ToString("N"));
        var audit = Event(principal, workspaceId, AuditTaxonomy.Privilege.LogGenerated) with
        {
            ResourceType = AuditTaxonomy.Privilege.LogResourceType,
            SnapshotId = snapshotId,
            Details = new Dictionary<string, string?>
            {
                ["ProductionId"] = production?.ProductionId.ToString(),
                ["ReviewSetSnapshotId"] = reviewSet?.SnapshotId.ToString(),
                ["TemplateId"] = template?.TemplateId.ToString(),
                ["Template"] = metadata.TemplateName,
                ["Entries"] = Invariant(metadata.Entries),
                ["Withheld"] = Invariant(metadata.Withheld),
                ["Redacted"] = Invariant(metadata.Redacted + metadata.RedactedPrivacy),
                ["ExcludedByRules"] = Invariant(metadata.ExcludedByRules),
                ["ContentSha256"] = Convert.ToHexStringLower(content),
            },
        };
        var creation = await store.CreateVersionAsync(new NewPrivilegeLogVersion
        {
            WorkspaceId = workspaceId,
            LogId = Guid.CreateVersion7(),
            SeriesKey = seriesKey,
            Source = source,
            ProductionId = production?.ProductionId,
            SnapshotId = snapshotId,
            ScopeSnapshotId = reviewSet?.SnapshotId,
            TemplateId = template?.TemplateId,
            TemplateName = metadata.TemplateName,
            TemplateDefinition = PrivilegeLogTemplateRules.Serialize(definition),
            Metadata = metadataJson,
            ContentSha256 = content,
            CsvSha256 = SHA256.HashData(csv),
            CsvBytes = csv.Length,
            XlsxSha256 = SHA256.HashData(xlsx),
            XlsxBytes = xlsx.Length,
            Withheld = metadata.Withheld,
            Redacted = metadata.Redacted + metadata.RedactedPrivacy,
            Excluded = metadata.ExcludedByRules,
            FieldIds = PrivilegeLogTemplateRules.FieldIdsOf(definition, catalog),
            GeneratedBy = principal.UserId,
            GeneratedByDisplay = DisplayOf(principal),
            GeneratedAt = time.GetUtcNow(),
            Entries = entries,
        }, audit, cancellationToken).ConfigureAwait(false);
        return PrivilegeLogOutcome.Ok<PrivilegeLogCreation>(creation);
    }

    // ------------------------------------------------------------------------------------------------ reading

    /// <summary>The version, when the caller may see every document it lists and every field it reads.</summary>
    public async Task<PrivilegeLogRecord?> GetAsync(SecurityPrincipal principal, Guid workspaceId, Guid logId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var log = await store.GetAsync(workspaceId, logId, cancellationToken).ConfigureAwait(false);
        return log is not null && await ReadableAsync(principal, log, cancellationToken).ConfigureAwait(false) ? log : null;
    }

    /// <summary>A page of versions the caller may read, newest first, and the raw position after the page when there are more.</summary>
    public async Task<(IReadOnlyList<PrivilegeLogRecord> Items, (DateTimeOffset At, Guid LogId)? Next)> ListAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid? productionId, Guid? snapshotId, (DateTimeOffset At, Guid LogId)? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var rows = await store.ListAsync(workspaceId, productionId, snapshotId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var items = new List<PrivilegeLogRecord>(page.Count);
        foreach (var log in page)
        {
            if (await ReadableAsync(principal, log, cancellationToken).ConfigureAwait(false))
            {
                items.Add(log);
            }
        }

        return (items, rows.Count > limit ? (page[^1].GeneratedAt, page[^1].LogId) : null);
    }

    public Task<IReadOnlyList<PrivilegeLogEntryRow>> ReadEntriesAsync(PrivilegeLogRecord log, int afterOrdinal, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        return store.ReadEntriesAsync(log.WorkspaceId, log.LogId, afterOrdinal, limit, cancellationToken);
    }

    /// <summary>
    /// Renders a version's file again from its frozen metadata and entries and checks it against the recorded SHA-256
    /// (E13-T03 AC 2: regenerating the same version is byte-identical); a mismatch sends nothing.
    /// </summary>
    public async Task<PrivilegeLogOutcome<PrivilegeLogFile>> RenderAsync(PrivilegeLogRecord log, string format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (!csv && !string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return PrivilegeLogOutcome.Invalid<PrivilegeLogFile>("format", "Use csv or xlsx.");
        }

        var rows = new List<IReadOnlyList<string>>(log.EntryCount);
        var after = 0;
        while (true)
        {
            var page = await store.ReadEntriesAsync(log.WorkspaceId, log.LogId, after, Batch, cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Select(e => e.Cells));
            if (page.Count < Batch)
            {
                break;
            }

            after = page[^1].Ordinal;
        }

        var metadata = PrivilegeLogFiles.DeserializeMetadata(log.Metadata);
        var bytes = csv ? PrivilegeLogFiles.Csv(metadata, rows) : PrivilegeLogFiles.Xlsx(metadata, rows);
        var expected = csv ? log.CsvSha256 : log.XlsxSha256;
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(expected))
        {
            return PrivilegeLogOutcome.Of<PrivilegeLogFile>(PrivilegeLogStatus.IntegrityFailure,
                "The privilege log file does not match the SHA-256 recorded with its version.");
        }

        var extension = csv ? "csv" : "xlsx";
        return PrivilegeLogOutcome.Ok<PrivilegeLogFile>(new PrivilegeLogFile(log, extension, bytes,
            csv ? PrivilegeLogFiles.CsvContentType : PrivilegeLogFiles.XlsxContentType, FileName(log, metadata, extension)));
    }

    /// <summary>A download file name: letters, digits, space, dot, dash and underscore only.</summary>
    public static string FileName(PrivilegeLogRecord log, PrivilegeLogMetadata metadata, string extension)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(metadata);
        var name = (metadata.ProductionName ?? "Frozen set") + " privilege log v" + log.Version.ToString(CultureInfo.InvariantCulture);
        var safe = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_')]).Trim();
        return (safe.Length > 120 ? safe[..120] : safe) + "." + extension;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<bool> ReadableAsync(SecurityPrincipal principal, PrivilegeLogRecord log, CancellationToken cancellationToken)
    {
        var (catalog, restricted) = await CatalogAsync(principal, log.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (restricted.Contains(PrivilegeFields.Status) || log.FieldIds.Any(restricted.Contains) || catalog.Find(PrivilegeFields.Status) is null)
        {
            return false;
        }

        var ids = await store.ReadDocumentIdsAsync(log.WorkspaceId, log.LogId, cancellationToken).ConfigureAwait(false);
        var visible = await VisibleAsync(principal, log.WorkspaceId, ids, DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        return visible.Count == ids.Count;
    }

    private async Task<HashSet<Guid>> VisibleAsync(
        SecurityPrincipal principal, Guid workspaceId, IReadOnlyCollection<Guid> ids, DenialAudit audit, CancellationToken cancellationToken)
    {
        var visible = new HashSet<Guid>();
        foreach (var batch in ids.Distinct().Chunk(Batch))
        {
            var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, batch, audit, cancellationToken)
                .ConfigureAwait(false);
            visible.UnionWith(batch.Where(id => decisions.TryGetValue(id, out var d) && d.IsAllowed));
        }

        return visible;
    }

    private async Task<(FieldCatalog Catalog, IReadOnlySet<int> Restricted)> CatalogAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
        return (catalog, restricted);
    }

    private async Task<(SnapshotRecord? Snapshot, PrivilegeLogOutcome<PrivilegeLogCreation>? Refusal)> ReadySnapshotAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await snapshots.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        var visible = snapshot is not null && (snapshot.CreatedBy == principal.UserId
            || (await authorization.AuthorizeAsync(principal, workspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed);
        if (!visible)
        {
            return (null, PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.NotFound, "No such frozen set."));
        }

        return snapshot!.Status == SnapshotStatus.Ready
            ? (snapshot, null)
            : (null, PrivilegeLogOutcome.Of<PrivilegeLogCreation>(PrivilegeLogStatus.InvalidState, "The frozen set is not Ready."));
    }

    private async Task<Dictionary<Guid, ExportSourceDocument>> ReadDocumentsAsync(Guid workspaceId, List<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ExportSourceDocument>(ids.Count);
        foreach (var batch in ids.Chunk(Batch))
        {
            var read = await documents.ReadDocumentsAsync(workspaceId, batch, cancellationToken).ConfigureAwait(false);
            foreach (var (id, document) in read.Documents)
            {
                result[id] = document;
            }
        }

        return result;
    }

    /// <summary>
    /// Each listed document's family range: the first and last log identifier among its family's members the caller may
    /// see that are produced (Bates) or listed (Priv ID), in family order. Members neither produced nor listed are skipped.
    /// </summary>
    private async Task<Dictionary<Guid, string>> FamilyRangesAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid? productionId, List<PrivilegeLogCandidate> listed,
        Dictionary<Guid, (string Begin, string End)> privIds, bool wanted, CancellationToken cancellationToken)
    {
        var ranges = new Dictionary<Guid, string>();
        if (!wanted || listed.Count == 0)
        {
            return ranges;
        }

        var familyIds = listed.Select(c => c.FamilyId).Distinct().ToList();
        var members = new List<PrivilegeLogFamilyMember>();
        foreach (var batch in familyIds.Chunk(Batch))
        {
            members.AddRange(await store.ReadFamilyMembersAsync(workspaceId, batch, productionId, cancellationToken).ConfigureAwait(false));
        }

        var visible = await VisibleAsync(principal, workspaceId, members.Select(m => m.DocumentId).ToList(), DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        var byFamily = new Dictionary<Guid, string>();
        foreach (var family in members.Where(m => visible.Contains(m.DocumentId)).GroupBy(m => m.FamilyId))
        {
            var identified = new List<(string Begin, string End)>();
            foreach (var member in family.OrderBy(m => m.FamilySequence).ThenBy(m => m.DocumentId))
            {
                if (member.ProdBegBates is { } begin)
                {
                    identified.Add((begin, member.ProdEndBates ?? begin));
                }
                else if (privIds.TryGetValue(member.DocumentId, out var id))
                {
                    identified.Add(id);
                }
            }

            byFamily[family.Key] = PrivilegeLogRules.FamilyRange(identified);
        }

        foreach (var candidate in listed)
        {
            ranges[candidate.DocumentId] = byFamily.GetValueOrDefault(candidate.FamilyId) ?? string.Empty;
        }

        return ranges;
    }

    private static string Cell(
        PrivilegeLogColumn column, PrivilegeLogCandidate candidate, PrivilegeLogEntryTreatment treatment, ExportSourceDocument? document,
        Dictionary<Guid, (string Begin, string End)> privIds, Dictionary<Guid, string> families, FieldFormatter formatter, FieldDefinition? subject,
        string separator)
    {
        switch (column.Kind)
        {
            case PrivilegeLogColumnKind.PrivId:
                return privIds[candidate.DocumentId].Begin;
            case PrivilegeLogColumnKind.BegBates:
                return candidate.Member?.ProdBegBates ?? string.Empty;
            case PrivilegeLogColumnKind.EndBates:
                return candidate.Member?.ProdEndBates ?? string.Empty;
            case PrivilegeLogColumnKind.ControlNumber:
                return candidate.ControlNumber;
            case PrivilegeLogColumnKind.FamilyRange:
                return families.GetValueOrDefault(candidate.DocumentId) ?? string.Empty;
            case PrivilegeLogColumnKind.Treatment:
                return PrivilegeLogRules.TreatmentText(treatment);
            case PrivilegeLogColumnKind.RedactionReasons:
                return string.Join(separator, candidate.Member?.RedactionReasons.Select(r => r.Name) ?? []);
            case PrivilegeLogColumnKind.Basis:
                var basis = document is null ? string.Empty : formatter.Format(PrivilegeFields.Basis, document);
                if (basis.Length > 0 || candidate.Member is not { } member)
                {
                    return basis;
                }

                // A redacted document without a coded Basis: the reasons of its redactions in the listed category.
                var category = treatment == PrivilegeLogEntryTreatment.RedactedPrivacy ? RedactionReasonCategory.Privacy : RedactionReasonCategory.Privilege;
                return string.Join(separator, member.RedactionReasons.Where(r => r.Category == category).Select(r => r.Name));
            case PrivilegeLogColumnKind.SubjectOrFileName:
                if (document is null)
                {
                    return string.Empty;
                }

                var value = subject is null ? string.Empty : formatter.Format(subject.FieldId, document);
                return value.Length > 0 ? value : formatter.Format(SystemFields.FileName, document);
            default:
                return document is null ? string.Empty : formatter.Format(column.FieldId!.Value, document);
        }
    }

    private static int FirstMatchingRule(IReadOnlyList<PrivilegeLogExclusionRule> rules, ExportSourceDocument document, FieldCatalog catalog, TimeZoneInfo zone)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.OnOrAfter is not null || rule.Before is not null)
            {
                var day = FieldFormatter.Day(catalog.Find(rule.DateFieldId!.Value), document, zone);
                if (day is not { } d || (rule.OnOrAfter is { } from && d < from) || (rule.Before is { } to && d >= to))
                {
                    continue;
                }
            }

            if (rule.LogCategoryChoiceIds is { Count: > 0 } categories
                && !FieldValues.ChoiceIds(document.Coding.GetValueOrDefault(PrivilegeFields.LogCategory)).Any(categories.Contains))
            {
                continue;
            }

            if (rule.AttorneysInvolved is { Count: > 0 } attorneys)
            {
                var names = document.Coding.GetValueOrDefault(PrivilegeFields.AttorneysInvolved) switch
                {
                    JsonArray array => array.Select(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>().Trim() : null),
                    JsonValue v when v.GetValueKind() == JsonValueKind.String => [v.GetValue<string>().Trim()],
                    _ => [],
                };
                if (!names.Any(n => n is not null && attorneys.Contains(n, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            return i;
        }

        return -1;
    }

    private static PrivilegeLogAppliedRule Applied(PrivilegeLogExclusionRule rule, int excluded, FieldCatalog catalog) => new(
        rule.Label,
        rule.OnOrAfter is null && rule.Before is null ? null : catalog.Find(rule.DateFieldId!.Value)?.Name,
        rule.OnOrAfter,
        rule.Before,
        [.. (rule.LogCategoryChoiceIds ?? []).Select(id => catalog.ChoicesOf(PrivilegeFields.LogCategory).FirstOrDefault(c => c.ChoiceId == id)?.Name
            ?? id.ToString(CultureInfo.InvariantCulture))],
        rule.AttorneysInvolved ?? [],
        excluded);

    private AuditEvent Event(SecurityPrincipal principal, Guid workspaceId, string action) => new()
    {
        OccurredAt = time.GetUtcNow(),
        WorkspaceId = workspaceId,
        Category = AuditTaxonomy.Privilege.Category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = DisplayOf(principal),
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        Outcome = AuditOutcome.Success,
        CorrelationId = principal.CorrelationId,
    };

    /// <summary>The audit event of a download attempt (written by the gateway endpoint before the first byte).</summary>
    public AuditEvent DownloadEvent(SecurityPrincipal principal, Guid workspaceId, Guid logId, string? format, AuditOutcome outcome, string? reasonCode) =>
        Event(principal, workspaceId, AuditTaxonomy.Privilege.LogDownloaded) with
        {
            ResourceType = AuditTaxonomy.Privilege.LogResourceType,
            ResourceId = logId.ToString(),
            Outcome = outcome,
            ReasonCode = reasonCode,
            Details = new Dictionary<string, string?> { ["Format"] = format },
        };

    private static string DisplayOf(SecurityPrincipal principal) =>
        string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Field values as log text: choice names, multiple values joined, dates in the template's format and zone.</summary>
    private sealed class FieldFormatter(FieldCatalog catalog, string separator, string dateFormat, TimeZoneInfo zone)
    {
        public string Format(int fieldId, ExportSourceDocument document)
        {
            var field = catalog.Find(fieldId);
            if (field is null || field.IsDeleted)
            {
                return string.Empty;
            }

            var value = Value(field, document);
            if (value is null)
            {
                return string.Empty;
            }

            if (field.Type == FieldType.Date && value is JsonValue date && date.GetValueKind() == JsonValueKind.String)
            {
                return FormatDate(date.GetValue<string>());
            }

            if (field.IsChoice)
            {
                var ids = FieldValues.ChoiceIds(value).ToHashSet();
                return string.Join(separator, catalog.ChoicesOf(field.FieldId).Where(c => ids.Contains(c.ChoiceId))
                    .OrderBy(c => c.SortOrder).ThenBy(c => c.ChoiceId).Select(c => c.Name));
            }

            if (value is JsonArray array)
            {
                return string.Join(separator, array.Select(item => item is null ? null : Scalar(item)).OfType<string>());
            }

            return Scalar(value) ?? string.Empty;
        }

        /// <summary>The calendar day of a date field's value in <paramref name="zone"/>.</summary>
        public static DateOnly? Day(FieldDefinition? field, ExportSourceDocument document, TimeZoneInfo zone)
        {
            if (field is null || Value(field, document) is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            var stored = value.GetValue<string>();
            if (stored.Length == 10 && DateOnly.TryParseExact(stored, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day;
            }

            return DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime)
                : null;
        }

        private static JsonNode? Value(FieldDefinition field, ExportSourceDocument document) => field.Storage switch
        {
            FieldStorage.Column => field.ColumnName is { } column ? DocumentFieldDisplay.ColumnValue(document.Document, column) : null,
            FieldStorage.Coding => document.Coding.GetValueOrDefault(field.FieldId),
            _ => Metadata(document)?[field.Key],
        };

        private static JsonObject? Metadata(ExportSourceDocument document)
        {
            try
            {
                return string.IsNullOrWhiteSpace(document.Document.Metadata) ? null : JsonNode.Parse(document.Document.Metadata) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private string FormatDate(string stored)
        {
            if (stored.Length == 10 && DateOnly.TryParseExact(stored, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day.ToDateTime(TimeOnly.MinValue).ToString(dateFormat, CultureInfo.InvariantCulture);
            }

            return DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                ? TimeZoneInfo.ConvertTime(instant, zone).DateTime.ToString(dateFormat, CultureInfo.InvariantCulture)
                : stored;
        }

        private static string? Scalar(JsonNode node)
        {
            if (node is not JsonValue value)
            {
                return node.ToJsonString();
            }

            return value.GetValueKind() switch
            {
                JsonValueKind.True => "Yes",
                JsonValueKind.False => "No",
                JsonValueKind.Number => value.ToJsonString(),
                JsonValueKind.String => value.GetValue<string>(),
                _ => null,
            };
        }
    }
}
