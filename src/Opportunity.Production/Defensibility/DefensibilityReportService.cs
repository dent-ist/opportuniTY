using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Defensibility;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;

namespace Opportunity.Production.Defensibility;

/// <summary>
/// The defensibility reports (E14-T05; legal finding 10, baseline §22 and §28): the review population and coding summary
/// by reviewer and date, the production history (volumes, Bates ranges, counts, hashes and QC results, from the
/// manifests and QC reports frozen at finalization), the search term hit reports (each tied to its snapshot and search
/// generation, with the index lag at execution) and a document's chain of custody (import source and load file, hashes
/// recorded at import next to the hashes of the bytes in storage now, derived artifacts, productions).
/// <para>
/// Every report states its as-of point and a fingerprint of its as-of content, and is reproducible for that point: it
/// reads only rows stamped at or before it. Each generation is audited (<c>Audit.DefensibilityReportGenerated</c>) before
/// anything is returned; a stored object whose bytes no longer match raises <c>Integrity.HashMismatch</c>. Callers
/// authorize the workspace permission (<c>Compliance.Report</c>) first; the chain of custody also needs the document to be
/// visible to the caller (else not found, as for a missing document).
/// </para>
/// </summary>
public sealed class DefensibilityReportService(
    IDefensibilityReportStore store, ICodingRepository coding, IAuthorizationService authorization, IAuditEventWriter audit, TimeProvider time)
{
    /// <summary>Reviewer-day rows a review summary lists (the totals cover all).</summary>
    public const int MaxReviewerDays = 20_000;

    /// <summary>Completed search term reports a hit report lists (oldest first).</summary>
    public const int MaxTermReports = 500;

    /// <summary>Stored objects a chain of custody lists.</summary>
    public const int MaxCustodyObjects = 500;

    /// <summary>Stored objects (and load files) a chain of custody re-reads from storage: the native and text first.</summary>
    public const int MaxVerifiedObjects = 100;

    private const string CorrelationTag = "opportunity.correlation_id";

    /// <summary>
    /// The as-of point of a request: now when none is given, else the given instant (UTC, microseconds like PostgreSQL);
    /// null when it lies in the future.
    /// </summary>
    public DefensibilityPoint? Point(DateTimeOffset? asOf)
    {
        var now = Truncate(time.GetUtcNow());
        if (asOf is not { } at)
        {
            return new DefensibilityPoint(now, IsNow: true);
        }

        var point = Truncate(at.ToUniversalTime());
        return point > now ? null : new DefensibilityPoint(point, IsNow: false);
    }

    public async Task<ReviewSummaryOutcome> ReviewSummaryAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, Guid? snapshotId, DefensibilityFormat format,
        CancellationToken cancellationToken = default) =>
        await ReviewSummaryAsync(caller, workspaceId, point, snapshotId, format, pack: null, cancellationToken).ConfigureAwait(false);

    public async Task<ProductionHistoryReport> ProductionHistoryAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, DefensibilityFormat format, CancellationToken cancellationToken = default) =>
        await ProductionHistoryAsync(caller, workspaceId, point, format, pack: null, cancellationToken).ConfigureAwait(false);

    public async Task<SearchTermHitsReport> SearchTermHitsAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, DefensibilityFormat format, CancellationToken cancellationToken = default) =>
        await SearchTermHitsAsync(caller, workspaceId, point, format, pack: null, cancellationToken).ConfigureAwait(false);

    /// <param name="objects">Object storage, to re-read the stored bytes; null leaves every object NotVerified.</param>
    public async Task<ChainOfCustodyOutcome> ChainOfCustodyAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid documentId, DefensibilityPoint point, IObjectStore? objects, DefensibilityFormat format,
        CancellationToken cancellationToken = default) =>
        await ChainOfCustodyAsync(caller, workspaceId, documentId, point, objects, format, pack: null, cancellationToken).ConfigureAwait(false);

    internal async Task<ReviewSummaryOutcome> ReviewSummaryAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, Guid? snapshotId, DefensibilityFormat format, PackContext? pack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        DefensibilitySnapshot? snapshot = null;
        if (snapshotId is { } id)
        {
            snapshot = await store.GetSnapshotAsync(workspaceId, id, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return new ReviewSummaryOutcome(DefensibilityOutcomeStatus.NotFound);
            }
        }

        var data = await coding.ReadReviewSummaryAsync(workspaceId, point.AsOf, snapshotId, MaxReviewerDays, cancellationToken).ConfigureAwait(false);
        var fingerprint = DefensibilityFingerprint.ReviewSummary(workspaceId, point.AsOf, snapshotId, data);
        var statement = await StateAsync(caller, workspaceId, DefensibilityReportKind.ReviewSummary, point, snapshotId, fingerprint, format, pack,
            new Dictionary<string, string?>
            {
                ["documents"] = N(data.Population.Documents),
                ["reviewed"] = N(data.Population.Reviewed),
                ["reviewers"] = N(data.Reviewers.Count),
            }, null, cancellationToken).ConfigureAwait(false);
        return new ReviewSummaryOutcome(DefensibilityOutcomeStatus.Ok, new ReviewSummaryReport(statement, snapshot, data));
    }

    internal async Task<ProductionHistoryReport> ProductionHistoryAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, DefensibilityFormat format, PackContext? pack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        var productions = await store.ReadProductionHistoryAsync(workspaceId, point.AsOf, cancellationToken).ConfigureAwait(false);
        var fingerprint = DefensibilityFingerprint.ProductionHistory(workspaceId, point.AsOf, productions);
        var statement = await StateAsync(caller, workspaceId, DefensibilityReportKind.ProductionHistory, point, null, fingerprint, format, pack,
            new Dictionary<string, string?>
            {
                ["productions"] = N(productions.Count),
                ["volumes"] = N(productions.Sum(p => p.Volumes.Count)),
            }, null, cancellationToken).ConfigureAwait(false);
        return new ProductionHistoryReport(statement, productions);
    }

    internal async Task<SearchTermHitsReport> SearchTermHitsAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, DefensibilityFormat format, PackContext? pack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        var reports = await store.ReadTermHitReportsAsync(workspaceId, point.AsOf, MaxTermReports + 1, cancellationToken).ConfigureAwait(false);
        var truncated = reports.Count > MaxTermReports;
        var listed = truncated ? reports.Take(MaxTermReports).ToList() : reports;
        var fingerprint = DefensibilityFingerprint.SearchTermHits(workspaceId, point.AsOf, listed);
        var statement = await StateAsync(caller, workspaceId, DefensibilityReportKind.SearchTermHits, point, null, fingerprint, format, pack,
            new Dictionary<string, string?>
            {
                ["reports"] = N(listed.Count),
                ["staleIndex"] = N(listed.Count(r => r.Report.IndexCurrent == false)),
            }, null, cancellationToken).ConfigureAwait(false);
        return new SearchTermHitsReport(statement, listed, truncated);
    }

    internal async Task<ChainOfCustodyOutcome> ChainOfCustodyAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid documentId, DefensibilityPoint point, IObjectStore? objects, DefensibilityFormat format,
        PackContext? pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(point);
        var decision = await authorization.AuthorizeAsync(caller, workspaceId, Permission.DocumentView, documentId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new ChainOfCustodyOutcome(decision.Outcome == AuthorizationOutcome.NotFound
                ? DefensibilityOutcomeStatus.NotFound
                : DefensibilityOutcomeStatus.Forbidden);
        }

        var data = await store.ReadChainOfCustodyAsync(workspaceId, documentId, point.AsOf, MaxCustodyObjects, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return new ChainOfCustodyOutcome(DefensibilityOutcomeStatus.NotFound);
        }

        // Re-read the bytes: the native and text first (the store orders them so), then load files, then the rest.
        var budget = MaxVerifiedObjects;
        var verified = new List<VerifiedCustodyObject>();
        foreach (var o in data.Objects.Take(2))
        {
            verified.Add(new VerifiedCustodyObject(o, await VerifyAsync(objects, o.LogicalKey, o.Sha256, budget-- > 0, cancellationToken).ConfigureAwait(false)));
        }

        var imports = new List<VerifiedCustodyImport>();
        foreach (var i in data.Imports)
        {
            imports.Add(new VerifiedCustodyImport(i, await VerifyAsync(objects, i.LoadFileKey, i.LoadFileSha256, budget-- > 0, cancellationToken).ConfigureAwait(false)));
        }

        foreach (var o in data.Objects.Skip(2))
        {
            verified.Add(new VerifiedCustodyObject(o, await VerifyAsync(objects, o.LogicalKey, o.Sha256, budget-- > 0, cancellationToken).ConfigureAwait(false)));
        }

        foreach (var bad in verified.Where(v => v.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing))
        {
            await audit.WriteAsync(Mismatch(caller, workspaceId, documentId, bad.Stored.ObjectId.ToString("D"), bad.Stored.Area.ToString(), bad.Stored.Sha256,
                bad.Verification), cancellationToken).ConfigureAwait(false);
        }

        foreach (var bad in imports.Where(v => v.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing))
        {
            await audit.WriteAsync(Mismatch(caller, workspaceId, documentId, bad.Import.ImportId.ToString("D"), "ImportSource", bad.Import.LoadFileSha256,
                bad.Verification), cancellationToken).ConfigureAwait(false);
        }

        var fingerprint = DefensibilityFingerprint.ChainOfCustody(workspaceId, point.AsOf, data);
        var mismatches = verified.Count(v => v.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing)
            + imports.Count(v => v.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing);
        var statement = await StateAsync(caller, workspaceId, DefensibilityReportKind.ChainOfCustody, point, null, fingerprint, format, pack,
            new Dictionary<string, string?>
            {
                ["documentId"] = documentId.ToString("D"),
                ["objects"] = N(data.Objects.Count),
                ["verified"] = N(verified.Count(v => v.Verification.Status != ObjectVerificationStatus.NotVerified)
                    + imports.Count(v => v.Verification.Status != ObjectVerificationStatus.NotVerified)),
                ["mismatches"] = N(mismatches),
            }, (documentId, decision.BreakGlass), cancellationToken).ConfigureAwait(false);
        return new ChainOfCustodyOutcome(DefensibilityOutcomeStatus.Ok, new ChainOfCustodyReport(statement, data, imports, verified));
    }

    /// <summary>Writes the pack's own audit event (before the first byte) and returns its statement.</summary>
    internal async Task<DefensibilityStatement> StatePackAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPoint point, Guid? snapshotId, PackContext pack, IReadOnlyList<string> fingerprints,
        IReadOnlyDictionary<string, string?> details, CancellationToken cancellationToken)
    {
        var fingerprint = DefensibilityFingerprint.Pack(workspaceId, point.AsOf, fingerprints);
        return await StateAsync(caller, workspaceId, DefensibilityReportKind.Pack, point, snapshotId, fingerprint, DefensibilityFormat.Pack, null,
            details, null, cancellationToken, pack.AuditEventId).ConfigureAwait(false);
    }

    private async Task<ObjectVerification> VerifyAsync(IObjectStore? objects, string key, byte[] expected, bool withinBudget, CancellationToken cancellationToken)
    {
        if (objects is null || !withinBudget)
        {
            return new ObjectVerification(ObjectVerificationStatus.NotVerified, null, null);
        }

        try
        {
            var stream = await objects.OpenReadAsync(ObjectKey.Parse(key), null, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return new ObjectVerification(
                    CryptographicOperations.FixedTimeEquals(hash, expected) ? ObjectVerificationStatus.Match : ObjectVerificationStatus.Mismatch,
                    hash, time.GetUtcNow());
            }
        }
        catch (ObjectNotFoundException)
        {
            return new ObjectVerification(ObjectVerificationStatus.Missing, null, time.GetUtcNow());
        }
        catch (ObjectIntegrityException)
        {
            // The store itself refused the bytes (an envelope that no longer authenticates): they are not the evidence.
            return new ObjectVerification(ObjectVerificationStatus.Mismatch, null, time.GetUtcNow());
        }
    }

    private async Task<DefensibilityStatement> StateAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityReportKind report, DefensibilityPoint point, Guid? snapshotId, string fingerprint,
        DefensibilityFormat format, PackContext? pack, IReadOnlyDictionary<string, string?> details, (Guid DocumentId, bool BreakGlass)? document,
        CancellationToken cancellationToken, Guid? eventId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        long? generation = point.IsNow ? await store.ReadSearchGenerationAsync(workspaceId, cancellationToken).ConfigureAwait(false) : null;
        var generatedAt = pack?.GeneratedAt ?? time.GetUtcNow();
        var auditEventId = pack?.AuditEventId ?? eventId ?? Guid.CreateVersion7();
        if (pack is null)
        {
            var all = new Dictionary<string, string?>(details)
            {
                ["report"] = report.ToString(),
                ["format"] = format.ToString(),
                ["asOf"] = Timestamp(point.AsOf),
                ["asOfRequested"] = point.IsNow ? "false" : "true",
                ["snapshotId"] = snapshotId?.ToString("D"),
                ["fingerprint"] = fingerprint,
            };
            await audit.WriteAsync(new AuditEvent
            {
                EventId = auditEventId,
                WorkspaceId = workspaceId,
                OccurredAt = generatedAt,
                Category = AuditTaxonomy.Audit.Category,
                Action = AuditTaxonomy.Audit.DefensibilityReportGenerated,
                ActorType = AuditActorType.User,
                ActorId = caller.UserId.ToString(),
                ActorDisplay = Display(caller),
                AccessPath = document?.BreakGlass == true ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
                ClientIp = caller.ClientIp,
                UserAgent = caller.UserAgent,
                ResourceType = document is null ? "Workspace" : "Document",
                ResourceId = document is { } d ? d.DocumentId.ToString() : workspaceId.ToString(),
                Outcome = AuditOutcome.Success,
                CorrelationId = Correlation(caller),
                SnapshotId = snapshotId,
                SearchGeneration = generation,
                Details = all.Where(p => p.Value is not null).ToDictionary(),
            }, cancellationToken).ConfigureAwait(false);
        }

        return new DefensibilityStatement(report, workspaceId, point.AsOf, snapshotId, generation, generatedAt, caller.DisplayName, fingerprint, auditEventId);
    }

    private AuditEvent Mismatch(
        SecurityPrincipal caller, Guid workspaceId, Guid documentId, string objectId, string area, byte[] expected, ObjectVerification verification) => new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Integrity.Category,
            Action = AuditTaxonomy.Integrity.HashMismatch,
            ActorType = AuditActorType.User,
            ActorId = caller.UserId.ToString(),
            ActorDisplay = Display(caller),
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            ResourceType = "Document",
            ResourceId = documentId.ToString(),
            Outcome = AuditOutcome.Failure,
            ReasonCode = verification.Status == ObjectVerificationStatus.Missing ? "ObjectMissing" : "HashMismatch",
            CorrelationId = Correlation(caller),
            Details = new Dictionary<string, string?>
            {
                ["source"] = "ChainOfCustody",
                ["objectId"] = objectId,
                ["area"] = area,
                ["expectedSha256"] = Convert.ToHexStringLower(expected),
                ["actualSha256"] = verification.Sha256 is { } actual ? Convert.ToHexStringLower(actual) : null,
            }.Where(p => p.Value is not null).ToDictionary(),
        };

    private static string Display(SecurityPrincipal caller) =>
        caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength] : caller.DisplayName;

    private static string? Correlation(SecurityPrincipal caller) => caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string;

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The as-of point of a report; <see cref="IsNow"/> when the request named none (the report is as of its generation).</summary>
public sealed record DefensibilityPoint(DateTimeOffset AsOf, bool IsNow);

/// <summary>The reports of one pack share its audit event (written once for the pack) and its generation time.</summary>
internal sealed record PackContext(Guid AuditEventId, DateTimeOffset GeneratedAt);

public static class DefensibilityServiceRegistration
{
    public static IServiceCollection AddDefensibilityReportService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<DefensibilityReportService>();
        services.TryAddScoped<DefensibilityPackService>();
        return services;
    }
}
