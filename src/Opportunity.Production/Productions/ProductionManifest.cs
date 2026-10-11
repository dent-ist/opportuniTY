using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Opportunity.Application.Coding;
using Opportunity.Application.Productions;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Productions;

namespace Opportunity.Production.Productions;

/// <summary>
/// Software that decides a production's output (E12-T02: "renderer/tool versions"), recorded in its manifest. A re-run
/// on different versions is expected to differ and must be reported as such. The volume writer (E12-T05) is recorded
/// here; the page imager (render pipeline and endorser) that wrote a volume's images is recorded in that volume's own
/// manifest, by the rendering worker that ran it (renderer stays null here).
/// </summary>
public sealed record ProductionSoftware(string Opportunity, string Runtime, string BatesAllocator, string? Renderer, string? VolumeWriter)
{
    public static ProductionSoftware Current { get; } = new(
        typeof(ProductionSoftware).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ProductionSoftware).Assembly.GetName().Version?.ToString() ?? "unknown",
        RuntimeInformation.FrameworkDescription,
        BatesAssignmentHasher.Prefix,
        Renderer: null,
        VolumeWriter: Volumes.ProductionVolumeSettings.WriterVersion);
}

/// <summary>
/// The manifest of a finalized production (Q-08, E12-T02): every specification value (the canonical specification
/// itself and its SHA-256), the frozen set (id, count, root hash), the Bates range and the SHA-256 of the assignment of
/// every document, the frozen-state versions (coding high-water mark; the redaction set version once redactions exist,
/// E11-T04) and the software versions. Written once, canonically (fixed member order, UTF-8, no whitespace), stored
/// with its SHA-256; volume files and their per-file SHA-256 are added by volume generation (E12-T05).
/// </summary>
public static class ProductionManifest
{
    public const int SchemaVersion = 1;

    public static (string Json, byte[] Sha256) Build(
        ProductionRecord production, SnapshotRecord snapshot, CodingHighWater? coding, ProductionSoftware software, Guid finalizedBy,
        DateTimeOffset finalizedAt, string? privilegeConflictOverrideReason = null, ProductionQcResult? qc = null)
    {
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(software);
        if (production.BatesFirst is not { } first || production.BatesLast is not { } last || production.AssignmentsSha256 is not { } assignments)
        {
            throw new InvalidOperationException("A manifest is written for an allocated production.");
        }

        var format = new BatesFormat(production.BatesPrefix, production.BatesPadding, production.BatesSuffix, BatesNumberingLevel.Page);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        using (var specification = JsonDocument.Parse(production.SpecificationJson))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", SchemaVersion);
            json.WriteStartObject("production");
            json.WriteString("productionId", production.ProductionId.ToString("D"));
            json.WriteString("lineageId", production.LineageId.ToString("D"));
            json.WriteNumber("version", production.Version);
            json.WriteString("name", production.Name);
            json.WriteEndObject();

            json.WriteStartObject("snapshot");
            json.WriteString("snapshotId", snapshot.SnapshotId.ToString("D"));
            json.WriteNumber("documentCount", snapshot.DocumentCount ?? 0);
            json.WriteString("rootSha256", snapshot.RootSha256 is { } root ? Convert.ToHexStringLower(root) : null);
            json.WriteEndObject();

            json.WritePropertyName("specification");
            specification.WriteTo(json);
            json.WriteString("specificationSha256", Convert.ToHexStringLower(production.SpecificationSha256));

            json.WriteStartObject("bates");
            json.WriteString("first", format.Format(first));
            json.WriteString("last", format.Format(last));
            json.WriteNumber("firstNumber", first);
            json.WriteNumber("lastNumber", last);
            json.WriteNumber("numbers", production.BatesUnits ?? 0);
            json.WriteNumber("documents", production.BatesDocuments ?? 0);
            json.WriteString("assignmentsSha256", Convert.ToHexStringLower(assignments));
            json.WriteEndObject();

            json.WriteStartObject("frozenState");
            if (coding is null)
            {
                json.WriteNull("codingHighWater");
            }
            else
            {
                json.WriteStartObject("codingHighWater");
                json.WriteString("occurredAt", coding.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("eventId", coding.EventId.ToString("D"));
                json.WriteEndObject();
            }

            // Placeholder until redactions exist (E11-T04): the per-document redaction versions are frozen with them.
            json.WriteNull("redactionSetVersion");
            json.WriteEndObject();

            json.WriteStartObject("software");
            json.WriteString("opportunity", software.Opportunity);
            json.WriteString("runtime", software.Runtime);
            json.WriteString("batesAllocator", software.BatesAllocator);
            json.WriteString("renderer", software.Renderer);
            json.WriteString("volumeWriter", software.VolumeWriter);
            json.WriteEndObject();

            // E13-T02 AC 2: present only when the production was finalized over unresolved privilege conflicts.
            if (privilegeConflictOverrideReason is not null)
            {
                json.WriteStartObject("privilegeConflictOverride");
                json.WriteString("reason", privilegeConflictOverrideReason);
                json.WriteString("by", finalizedBy.ToString("D"));
                json.WriteEndObject();
            }

            // E12-T07: the QC run the production was finalized with: its report hash, overrides with their reasons and warnings.
            if (qc is not null)
            {
                json.WriteStartObject("qc");
                json.WriteString("qcRunId", qc.QcRunId.ToString("D"));
                json.WriteString("reportSha256", Convert.ToHexStringLower(qc.ReportSha256));
                json.WriteString("outcome", qc.Passed ? "passed" : "blocked");
                json.WriteBoolean("warningsAcknowledged", qc.WarningsAcknowledged);
                json.WriteStartArray("overrides");
                foreach (var check in qc.Overridden)
                {
                    json.WriteStartObject();
                    json.WriteString("check", check.Definition.Key);
                    json.WriteNumber("documents", check.Documents);
                    json.WriteString("reason", check.OverrideReason);
                    json.WriteString("by", finalizedBy.ToString("D"));
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("warnings");
                foreach (var check in qc.Warnings)
                {
                    json.WriteStartObject();
                    json.WriteString("check", check.Definition.Key);
                    json.WriteNumber("documents", check.Documents);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteString("finalizedAt", finalizedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("finalizedBy", finalizedBy.ToString("D"));
            json.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        return (System.Text.Encoding.UTF8.GetString(bytes), SHA256.HashData(bytes));
    }

    /// <summary>The values a verification compares, read back from a stored manifest.</summary>
    public static ManifestValues Read(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var bates = root.GetProperty("bates");
        var snapshot = root.GetProperty("snapshot");
        return new ManifestValues(
            root.GetProperty("specificationSha256").GetString(),
            root.GetProperty("specification").GetRawText(),
            snapshot.GetProperty("snapshotId").GetString(),
            snapshot.GetProperty("rootSha256").GetString(),
            snapshot.GetProperty("documentCount").GetInt64(),
            bates.GetProperty("firstNumber").GetInt64(),
            bates.GetProperty("lastNumber").GetInt64(),
            bates.GetProperty("documents").GetInt64(),
            bates.GetProperty("assignmentsSha256").GetString());
    }
}

public sealed record ManifestValues(
    string? SpecificationSha256,
    string SpecificationJson,
    string? SnapshotId,
    string? SnapshotRootSha256,
    long SnapshotDocumentCount,
    long FirstNumber,
    long LastNumber,
    long Documents,
    string? AssignmentsSha256);
