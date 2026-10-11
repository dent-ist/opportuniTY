using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Opportunity.Application.Authorization;
using Opportunity.Application.Compliance;
using Opportunity.Application.Defensibility;
using Opportunity.Application.Storage;

namespace Opportunity.Production.Defensibility;

/// <param name="From">Start of the period of the ethical-wall proof and the audit trail (inclusive); defaults to 30 days before the as-of point.</param>
/// <param name="DocumentIds">Documents whose chain of custody the pack holds (at most <see cref="DefensibilityPackService.MaxDocuments"/>).</param>
/// <param name="IncludeAuditTrail">The caller may export the audit trail (<c>Audit.Export</c>).</param>
/// <param name="IncludeSearchText">The caller may read search text in the audit trail (<c>Audit.ReadSearchText</c>).</param>
public sealed record DefensibilityPackRequest(
    DefensibilityPoint Point,
    DateTimeOffset? From,
    Guid? SnapshotId,
    IReadOnlyList<Guid> DocumentIds,
    bool IncludeAuditTrail,
    bool IncludeSearchText);

public enum DefensibilityPackStatus
{
    Ok,
    InvalidPeriod,
    SnapshotNotFound,
    DocumentNotFound,
}

/// <summary>Everything a pack holds, generated (and audited) before the first byte is written.</summary>
public sealed record PreparedDefensibilityPack(
    DefensibilityStatement Statement,
    CompliancePeriod Period,
    ReviewSummaryReport Review,
    ProductionHistoryReport Productions,
    SearchTermHitsReport SearchTermHits,
    IReadOnlyList<ChainOfCustodyReport> Custody,
    WallProofReport WallProof,
    AccessReviewReport AccessReview,
    PreparedAuditExport? AuditTrail);

public sealed record DefensibilityPackOutcome(DefensibilityPackStatus Status, PreparedDefensibilityPack? Pack = null);

/// <summary>
/// The defensibility report pack (E14-T05): one ZIP with the four defensibility reports as of one point, the evidence they
/// rest on as it was recorded (each produced production's finalization manifest and QC report, verbatim), the
/// ethical-wall proof and access review (E14-T06) and, for callers with <c>Audit.Export</c>, the verifiable audit-chain
/// export of the period (checked offline with <c>audit verify-export</c>). <c>manifest.json</c>, written last, lists every
/// file with its size and SHA-256, every report's fingerprint and the pack's audit event.
/// </summary>
public sealed class DefensibilityPackService(DefensibilityReportService reports, ComplianceReportService compliance, TimeProvider time)
{
    public const int MaxDocuments = 25;

    public const string ContentType = "application/zip";

    public const string ManifestKind = "opportunity.defensibilityPack";

    public static string FileName(DefensibilityStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return "defensibility-pack-asof-" + DefensibilityCsv.Stamp(statement.AsOf) + ".zip";
    }

    public async Task<DefensibilityPackOutcome> PrepareAsync(
        SecurityPrincipal caller, Guid workspaceId, DefensibilityPackRequest request, IObjectStore? objects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (compliance.Period(request.From, request.Point.AsOf) is not { } period)
        {
            return new DefensibilityPackOutcome(DefensibilityPackStatus.InvalidPeriod);
        }

        var context = new PackContext(Guid.CreateVersion7(), time.GetUtcNow());
        var review = await reports.ReviewSummaryAsync(caller, workspaceId, request.Point, request.SnapshotId, DefensibilityFormat.Pack, context, cancellationToken)
            .ConfigureAwait(false);
        if (review.Report is null)
        {
            return new DefensibilityPackOutcome(DefensibilityPackStatus.SnapshotNotFound);
        }

        var custody = new List<ChainOfCustodyReport>();
        foreach (var documentId in request.DocumentIds.Distinct())
        {
            var outcome = await reports.ChainOfCustodyAsync(caller, workspaceId, documentId, request.Point, objects, DefensibilityFormat.Pack, context,
                cancellationToken).ConfigureAwait(false);
            if (outcome.Report is null)
            {
                return new DefensibilityPackOutcome(DefensibilityPackStatus.DocumentNotFound);
            }

            custody.Add(outcome.Report);
        }

        var productions = await reports.ProductionHistoryAsync(caller, workspaceId, request.Point, DefensibilityFormat.Pack, context, cancellationToken)
            .ConfigureAwait(false);
        var terms = await reports.SearchTermHitsAsync(caller, workspaceId, request.Point, DefensibilityFormat.Pack, context, cancellationToken)
            .ConfigureAwait(false);

        // The compliance reports and the audit-chain export audit themselves as usual (Security.*, Audit.Exported).
        var wallProof = await compliance.WallProofAsync(caller, workspaceId, period, ComplianceReportFormat.Csv, cancellationToken).ConfigureAwait(false);
        var accessReview = await compliance.AccessReviewAsync(caller, workspaceId, ComplianceReportFormat.Csv, cancellationToken).ConfigureAwait(false);
        var auditTrail = request.IncludeAuditTrail
            ? await compliance.PrepareAuditExportAsync(caller, workspaceId, period.From, period.To, request.IncludeSearchText, cancellationToken)
                .ConfigureAwait(false)
            : null;

        List<string> fingerprints =
        [
            review.Report.Statement.Fingerprint, productions.Statement.Fingerprint, terms.Statement.Fingerprint,
            .. custody.Select(c => c.Statement.Fingerprint),
        ];
        var statement = await reports.StatePackAsync(caller, workspaceId, request.Point, request.SnapshotId, context, fingerprints,
            new Dictionary<string, string?>
            {
                ["from"] = period.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["documents"] = custody.Count.ToString(CultureInfo.InvariantCulture),
                ["productions"] = productions.Productions.Count.ToString(CultureInfo.InvariantCulture),
                ["termReports"] = terms.Reports.Count.ToString(CultureInfo.InvariantCulture),
                ["auditTrail"] = auditTrail is null ? "false" : "true",
                ["reviewFingerprint"] = review.Report.Statement.Fingerprint,
                ["productionsFingerprint"] = productions.Statement.Fingerprint,
                ["termsFingerprint"] = terms.Statement.Fingerprint,
            }, cancellationToken).ConfigureAwait(false);
        return new DefensibilityPackOutcome(DefensibilityPackStatus.Ok,
            new PreparedDefensibilityPack(statement, period, review.Report, productions, terms, custody, wallProof, accessReview, auditTrail));
    }

    /// <summary>Writes the ZIP: the reports, the production evidence, the compliance reports, the audit trail, then manifest.json.</summary>
    public async Task WriteAsync(Guid workspaceId, PreparedDefensibilityPack pack, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(output);
        var files = new List<PackFile>();
        var stamp = pack.Statement.AsOf;
        var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken)
            .ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            await AddAsync(archive, "review-summary.csv", "Review population and coding summary by reviewer and date",
                DefensibilityCsv.ReviewSummary(pack.Review), stamp, files, cancellationToken).ConfigureAwait(false);
            await AddAsync(archive, "production-history.csv", "Production history: volumes, Bates ranges, counts, hashes and QC results",
                DefensibilityCsv.ProductionHistory(pack.Productions), stamp, files, cancellationToken).ConfigureAwait(false);
            await AddAsync(archive, "search-term-hits.csv", "Search term hit reports with snapshot, search generation and index lag at execution",
                DefensibilityCsv.SearchTermHits(pack.SearchTermHits), stamp, files, cancellationToken).ConfigureAwait(false);
            foreach (var custody in pack.Custody)
            {
                await AddAsync(archive, "chain-of-custody/" + SafeName(custody.Data.ControlNumber) + "_" + custody.Data.DocumentId.ToString("N")[..8] + ".csv",
                    "Chain of custody of " + custody.Data.ControlNumber, DefensibilityCsv.ChainOfCustody(custody), stamp, files, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var p in pack.Productions.Productions)
            {
                var folder = "productions/" + SafeName(p.Name) + "_v" + p.Version.ToString(CultureInfo.InvariantCulture) + "_" + p.ProductionId.ToString("N")[..8];
                await AddAsync(archive, folder + "/manifest.json", "Finalization manifest of " + p.Name + " (canonical JSON as frozen)",
                    Encoding.UTF8.GetBytes(p.Manifest), stamp, files, cancellationToken).ConfigureAwait(false);
                if (p.Qc is { } qc)
                {
                    await AddAsync(archive, folder + "/qc-report.json", "Finalization QC report of " + p.Name + " (canonical JSON as recorded)",
                        Encoding.UTF8.GetBytes(qc.ReportJson), stamp, files, cancellationToken).ConfigureAwait(false);
                }
            }

            await AddAsync(archive, "compliance/ethical-wall-proof.csv", "Ethical-wall proof for the period", ComplianceCsv.WallProof(pack.WallProof), stamp,
                files, cancellationToken).ConfigureAwait(false);
            await AddAsync(archive, "compliance/access-review.csv", "Workspace access review", ComplianceCsv.AccessReview(pack.AccessReview), stamp, files,
                cancellationToken).ConfigureAwait(false);

            if (pack.AuditTrail is { } trail)
            {
                const string path = "audit/audit-trail.jsonl";
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var hashing = new HashingStream(stream);
                    await using (hashing.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(hashing, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true);
                        await using (writer.ConfigureAwait(false))
                        {
                            await compliance.WriteAuditExportAsync(workspaceId, trail, writer, cancellationToken).ConfigureAwait(false);
                        }

                        files.Add(new PackFile(path, "Verifiable audit-chain export of the period (audit verify-export)", hashing.Length, hashing.Hash()));
                    }
                }
            }

            var manifest = Manifest(pack, files);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = stamp;
            var manifestStream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                await manifestStream.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Manifest(PreparedDefensibilityPack pack, IReadOnlyList<PackFile> files)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            var s = pack.Statement;
            json.WriteStartObject();
            json.WriteString("kind", ManifestKind);
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("workspaceId", s.WorkspaceId.ToString("D"));
            json.WriteString("asOf", Iso(s.AsOf));
            if (s.SearchGeneration is { } generation)
            {
                json.WriteNumber("searchGeneration", generation);
            }

            if (s.SnapshotId is { } snapshot)
            {
                json.WriteString("reviewSnapshotId", snapshot.ToString("D"));
            }

            json.WriteString("periodFrom", Iso(pack.Period.From));
            json.WriteString("periodTo", Iso(pack.Period.To));
            json.WriteString("generatedAt", Iso(s.GeneratedAt));
            json.WriteString("generatedBy", s.GeneratedBy);
            json.WriteString("auditEventId", s.AuditEventId.ToString("D"));
            json.WriteString("fingerprint", s.Fingerprint);
            json.WriteStartArray("reports");
            Report(json, "ReviewSummary", "review-summary.csv", pack.Review.Statement);
            Report(json, "ProductionHistory", "production-history.csv", pack.Productions.Statement);
            Report(json, "SearchTermHits", "search-term-hits.csv", pack.SearchTermHits.Statement);
            foreach (var c in pack.Custody)
            {
                Report(json, "ChainOfCustody", null, c.Statement, c.Data.DocumentId);
            }

            json.WriteEndArray();
            if (pack.AuditTrail is { } trail)
            {
                json.WriteStartObject("auditTrail");
                json.WriteNumber("fromSequence", trail.Manifest.FromSequence);
                json.WriteNumber("toSequence", trail.Manifest.ToSequence);
                json.WriteNumber("events", trail.Manifest.Events);
                json.WriteBoolean("intact", trail.Manifest.Verification.Intact);
                json.WriteEndObject();
            }

            json.WriteStartArray("files");
            foreach (var f in files)
            {
                json.WriteStartObject();
                json.WriteString("path", f.Path);
                json.WriteString("description", f.Description);
                json.WriteNumber("bytes", f.Bytes);
                json.WriteString("sha256", f.Sha256);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("notes");
            json.WriteStringValue("Each report reads only what was recorded at or before asOf; generating it again with the same asOf gives the same fingerprint.");
            json.WriteStringValue("Chain-of-custody files show the SHA-256 recorded at import next to the SHA-256 of the bytes re-read from storage when the pack was generated.");
            json.WriteStringValue("Production manifests and QC reports are the canonical JSON frozen at finalization; their SHA-256 values appear in production-history.csv.");
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void Report(Utf8JsonWriter json, string report, string? path, DefensibilityStatement statement, Guid? documentId = null)
    {
        json.WriteStartObject();
        json.WriteString("report", report);
        if (path is not null)
        {
            json.WriteString("path", path);
        }

        if (documentId is { } id)
        {
            json.WriteString("documentId", id.ToString("D"));
        }

        json.WriteString("fingerprint", statement.Fingerprint);
        json.WriteEndObject();
    }

    private static async Task AddAsync(
        ZipArchive archive, string path, string description, byte[] content, DateTimeOffset stamp, List<PackFile> files, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = stamp;
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }

        files.Add(new PackFile(path, description, content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content))));
    }

    /// <summary>A file name from free text: letters, digits, dot, dash and underscore only, at most 60 characters.</summary>
    internal static string SafeName(string value)
    {
        var safe = new string([.. value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]).Trim('.', '_');
        return safe.Length == 0 ? "item" : safe.Length > 60 ? safe[..60] : safe;
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed record PackFile(string Path, string Description, long Bytes, string Sha256);

    /// <summary>Passes writes through while hashing and counting them.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        private long Written { get; set; }

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            Written += count;
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            Written += buffer.Length;
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
