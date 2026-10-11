using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Authorization;
using Opportunity.Application.Keys;

namespace Opportunity.Application.Compliance;

public enum ComplianceReportFormat
{
    Json,
    Csv,
}

/// <summary>The period of a wall proof: from (inclusive) to (exclusive), UTC.</summary>
public sealed record CompliancePeriod(DateTimeOffset From, DateTimeOffset To);

/// <summary>An audit export after its first pass: the range, the manifest with the verification result, the keys to embed.</summary>
public sealed record PreparedAuditExport(
    AuditExportPlan Plan,
    AuditExportManifest Manifest,
    IReadOnlyList<(string KeyId, string Pem)> PublicKeys,
    bool IncludeRestricted);

/// <summary>
/// Ethical-wall proof, workspace access review and verifiable audit export (E14-T06; legal finding 12, SEC-13). Each
/// generation is audited before anything is returned: <c>Security.WallProofGenerated</c>,
/// <c>Security.AccessReviewGenerated</c> and <c>Audit.Exported</c>. Callers authorize first (<c>Compliance.Report</c>,
/// <c>Audit.Export</c>); documents are named by id only, as in the audit trail itself.
/// </summary>
public sealed class ComplianceReportService(
    IComplianceReportStore store, IAuditEventWriter audit, ISigningKeyProvider signingKeys, TimeProvider time)
{
    /// <summary>Attempts listed in the JSON report (the counts cover all).</summary>
    public const int MaxListedAttempts = 500;

    /// <summary>Attempts listed in the CSV report.</summary>
    public const int MaxExportedAttempts = 50_000;

    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    private const string CorrelationTag = "opportunity.correlation_id";

    /// <summary>The period of a report: <paramref name="to"/> defaults to now, <paramref name="from"/> to 30 days before it.</summary>
    public CompliancePeriod? Period(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - DefaultPeriod;
        return start < end ? new CompliancePeriod(start, end) : null;
    }

    public async Task<WallProofReport> WallProofAsync(
        SecurityPrincipal caller, Guid workspaceId, CompliancePeriod period, ComplianceReportFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        var data = await store.ReadWallProofAsync(workspaceId, period.From, period.To,
            format == ComplianceReportFormat.Csv ? MaxExportedAttempts : MaxListedAttempts, cancellationToken).ConfigureAwait(false);
        var report = new WallProofReport(workspaceId, period.From, period.To, time.GetUtcNow(), data);
        await audit.WriteAsync(Event(caller, workspaceId, AuditTaxonomy.Security.Category, AuditTaxonomy.Security.WallProofGenerated,
            new Dictionary<string, string?>
            {
                ["from"] = Timestamp(period.From),
                ["to"] = Timestamp(period.To),
                ["format"] = format.ToString(),
                ["walls"] = N(data.Walls.Count),
                ["attempts"] = N(data.Counts.Attempts),
                ["denied"] = N(data.Counts.Denied),
                ["breakGlass"] = N(data.Counts.BreakGlass),
                ["allowed"] = N(data.Counts.Allowed),
                ["failed"] = N(data.Counts.Failed),
                ["result"] = report.Held ? "Held" : "NotHeld",
            }), cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<AccessReviewReport> AccessReviewAsync(
        SecurityPrincipal caller, Guid workspaceId, ComplianceReportFormat format, CancellationToken cancellationToken = default)
    {
        var entries = await store.ReadAccessReviewAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var report = new AccessReviewReport(workspaceId, time.GetUtcNow(), entries);
        await audit.WriteAsync(Event(caller, workspaceId, AuditTaxonomy.Security.Category, AuditTaxonomy.Security.AccessReviewGenerated,
            new Dictionary<string, string?>
            {
                ["format"] = format.ToString(),
                ["users"] = N(report.Users),
                ["groups"] = N(report.Groups),
            }), cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Plans the checkpoint-aligned range, verifies it (hashes, links, checkpoint heads and Merkle roots, and signatures
    /// where the installation's public keys are available) and records <c>Audit.Exported</c> with the result.
    /// </summary>
    /// <param name="includeRestricted">The caller may read search text (<c>Audit.ReadSearchText</c>); otherwise it is withheld.</param>
    public async Task<PreparedAuditExport> PrepareAuditExportAsync(
        SecurityPrincipal caller, Guid workspaceId, DateTimeOffset? from, DateTimeOffset? to, bool includeRestricted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var plan = await store.PlanAuditExportAsync(workspaceId, from, to, cancellationToken).ConfigureAwait(false);
        var keys = new CollectingKeys(signingKeys);
        var walk = new AuditChainWalk(workspaceId, plan.FromSequence, plan.AnchorHash);
        walk.Checkpoints.AddRange(plan.Checkpoints);
        walk.Gaps.AddRange(plan.Gaps);
        var draft = Manifest(caller, workspaceId, from, to, plan, null, new AuditExportServerVerification { Intact = true });
        await AuditExportVerifier.VerifyAnchorAsync(walk, draft, plan.Anchor, keys, missingKeyIsIssue: false, cancellationToken).ConfigureAwait(false);
        await walk.VerifySignaturesAsync(keys, missingKeyIsIssue: false, cancellationToken).ConfigureAwait(false);
        if (plan.Events > 0)
        {
            await foreach (var row in store.ReadChainAsync(workspaceId, plan.FromSequence, plan.ToSequence, cancellationToken).ConfigureAwait(false))
            {
                var withheld = !includeRestricted && row.Envelope.RestrictedDetails is not null;
                walk.Visit(row.Envelope, row.Sequence, row.PrevHash, row.EventHash, withheld);
            }

            walk.Finish(plan.ToSequence);
        }

        // Signatures over the anchor and the in-range checkpoints that a key was available for.
        var signatures = (plan.Anchor is null ? 0 : 1) + plan.Checkpoints.Count;
        var verification = AuditExportVerifier.Summarize(walk, Math.Max(0, signatures - walk.UncheckedSignatures - CountSignatureIssues(walk)));
        var auditEvent = Event(caller, workspaceId, AuditTaxonomy.Audit.Category, AuditTaxonomy.Audit.Exported, new Dictionary<string, string?>
        {
            ["from"] = from is { } f ? Timestamp(f) : null,
            ["to"] = to is { } t ? Timestamp(t) : null,
            ["fromSequence"] = N(plan.FromSequence),
            ["toSequence"] = N(plan.ToSequence),
            ["events"] = N(plan.Events),
            ["checkpoints"] = N(plan.Checkpoints.Count + (plan.Anchor is null ? 0 : 1)),
            ["withheld"] = N(walk.Withheld),
            ["result"] = verification.Intact ? "Intact" : "Broken",
            ["issues"] = N(verification.IssueCount),
            ["signaturesUnchecked"] = N(verification.SignaturesUnchecked),
        }) with
        {
            ResourceType = "AuditChain",
        };
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        var manifest = Manifest(caller, workspaceId, from, to, plan, auditEvent.EventId, verification) with { WithheldEvents = walk.Withheld };
        return new PreparedAuditExport(plan, manifest, keys.Collected, includeRestricted);
    }

    /// <summary>Writes the export file (JSON Lines, LF) in its fixed record order, streaming the events.</summary>
    public async Task WriteAuditExportAsync(Guid workspaceId, PreparedAuditExport export, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(output);
        await WriteLineAsync(output, AuditExportFormat.ManifestLine(export.Manifest), cancellationToken).ConfigureAwait(false);
        foreach (var (keyId, pem) in export.PublicKeys)
        {
            await WriteLineAsync(output, AuditExportFormat.PublicKeyLine(keyId, pem), cancellationToken).ConfigureAwait(false);
        }

        foreach (var checkpoint in export.Plan.Anchor is { } anchor ? [anchor, .. export.Plan.Checkpoints] : export.Plan.Checkpoints)
        {
            await WriteLineAsync(output, AuditExportFormat.CheckpointLine(checkpoint), cancellationToken).ConfigureAwait(false);
        }

        foreach (var gap in export.Plan.Gaps)
        {
            await WriteLineAsync(output, AuditExportFormat.GapLine(gap), cancellationToken).ConfigureAwait(false);
        }

        if (export.Plan.Events > 0)
        {
            await foreach (var row in store.ReadChainAsync(workspaceId, export.Plan.FromSequence, export.Plan.ToSequence, cancellationToken)
                               .ConfigureAwait(false))
            {
                var withheld = !export.IncludeRestricted && row.Envelope.RestrictedDetails is not null;
                var envelope = row.Envelope with
                {
                    Sequence = row.Sequence ?? 0,
                    PrevHash = row.PrevHash ?? AuditChainFormat.Genesis,
                    RestrictedDetails = withheld ? null : row.Envelope.RestrictedDetails,
                };
                await WriteLineAsync(output, AuditExportFormat.EventLine(envelope, row.EventHash, withheld), cancellationToken).ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int CountSignatureIssues(AuditChainWalk walk) =>
        walk.Issues.Count(i => i.Kind == AuditChainIssueKind.CheckpointSignatureInvalid);

    private static async Task WriteLineAsync(TextWriter output, string line, CancellationToken cancellationToken)
    {
        await output.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private AuditExportManifest Manifest(
        SecurityPrincipal caller, Guid workspaceId, DateTimeOffset? from, DateTimeOffset? to, AuditExportPlan plan, Guid? eventId,
        AuditExportServerVerification verification) => new()
        {
            WorkspaceId = workspaceId,
            GeneratedAt = time.GetUtcNow(),
            GeneratedBy = caller.UserId.ToString("D"),
            GeneratedByName = caller.DisplayName,
            AuditEventId = eventId,
            RequestedFrom = from,
            RequestedTo = to,
            FromSequence = plan.FromSequence,
            ToSequence = plan.ToSequence,
            AnchorHash = AuditChainFormat.Hex(plan.AnchorHash),
            Events = plan.Events,
            Checkpoints = plan.Checkpoints.Count + (plan.Anchor is null ? 0 : 1),
            Gaps = plan.Gaps.Count,
            UnsealedEvents = plan.UnsealedEvents,
            EventsAfterLastCheckpoint = plan.EventsAfterLastCheckpoint,
            Verification = verification,
        };

    private AuditEvent Event(SecurityPrincipal caller, Guid workspaceId, string category, string action, IReadOnlyDictionary<string, string?> details)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = caller.UserId.ToString(),
            ActorDisplay = caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : caller.DisplayName,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            ResourceType = "Workspace",
            ResourceId = workspaceId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };
    }

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The installation's checkpoint public keys, as far as this host can read them (the API may hold none: then the
    /// signatures are left to <c>audit verify-export</c> with exported keys). Keys found are embedded in the export.
    /// </summary>
    private sealed class CollectingKeys(ISigningKeyProvider provider) : IAuditCheckpointKeys
    {
        private readonly SigningProviderCheckpointKeys _inner = new(provider);
        private readonly Dictionary<string, byte[]?> _keys = new(StringComparer.Ordinal);

        public IReadOnlyList<(string KeyId, string Pem)> Collected =>
            [.. _keys.Where(k => k.Value is not null).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, AuditExportKeys.Pem(k.Value!)))];

        public async Task<byte[]?> GetPublicKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            if (_keys.TryGetValue(keyId, out var known))
            {
                return known;
            }

            byte[]? key;
            try
            {
                key = await _inner.GetPublicKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException
                                           or KeyUnavailableException or FormatException)
            {
                key = null;
            }

            _keys[keyId] = key;
            return key;
        }
    }
}
