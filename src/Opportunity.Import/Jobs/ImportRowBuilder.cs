using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Turns one parsed DAT record into the <see cref="ImportRow"/> a chunk writes: structural columns on the document,
/// metadata keyed <c>f{FieldId}</c>, the original strings of coerced values in <c>MetadataRaw</c> (ADR-003 R9), the Q-31
/// coding values, and every error and warning of the row. Pure: the store decides create, overlay, skip or error.
/// </summary>
public static partial class ImportRowBuilder
{
    /// <summary>Key prefix of structural values kept in <c>MetadataRaw</c> until their tickets resolve them (E08-T04, E09).</summary>
    public const string StructuralRawPrefix = "s:";

    public static ImportRow Build(
        CompiledMapping mapping, DatRecord record, long rowNo, long? lineNo, Guid importBatchId, IReadOnlySet<int> codingOverlayFieldIds)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(codingOverlayFieldIds);
        var issues = record.Issues
            .Select(i => new ImportRowIssue(
                i.Severity == DatIssueSeverity.Error ? ImportIssueSeverity.Error : ImportIssueSeverity.Warning,
                Code(i.Kind), Truncate(i.Message), i.Column))
            .ToList();
        if (record.IsRejected)
        {
            return new ImportRow { RowNo = rowNo, LineNo = lineNo, ControlNumber = record.ControlNumber, Issues = issues };
        }

        var mapped = mapping.Map(rowNo, record.Values);
        var documentId = Guid.CreateVersion7();
        var document = new Document
        {
            DocumentId = documentId,
            FamilyId = documentId,
            ControlNumber = mapped.ControlNumber ?? string.Empty,
            ControlNumberNorm = mapped.ControlNumberNorm ?? string.Empty,
        };
        var metadata = new JsonObject();
        var raw = new JsonObject();
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        var coding = new List<ImportCodingValue>();
        foreach (var cell in mapped.Cells)
        {
            var column = cell.Column.Column;
            foreach (var warning in cell.Warnings)
            {
                issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "value-adjusted", Truncate($"{cell.Target.Label}: {warning}"), column));
            }

            if (cell.Error is { } error)
            {
                issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, error.Code, Truncate($"{cell.Target.Label}: {error.Message}"), column));
                continue;
            }

            if (cell.Status == CoercionStatus.MissingChoices)
            {
                issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, "unknown-choice",
                    Truncate($"{cell.Target.Label}: no choice named {string.Join(", ", cell.MissingChoices.Select(n => "'" + n + "'"))}."), column));
                continue;
            }

            if (cell.Value is not { } value || cell.Target.IsControlNumber)
            {
                continue;
            }

            if (cell.Target.Structural is { } structural)
            {
                // The upload string stays available to the tickets that resolve it (family E09-T01, paths E08-T04);
                // duplicate/thread identifiers are applied to the document below.
                raw[StructuralRawPrefix + structural] = new JsonObject { ["raw"] = value.GetValue<string>(), ["batch"] = importBatchId.ToString() };
                continue;
            }

            if (cell.Target.FieldId is not { } fieldId)
            {
                continue;
            }

            var definition = cell.Target.Definition;
            switch (definition.Storage)
            {
                case FieldStorage.Column:
                    if (!TrySetColumn(document, fieldId, value, supplied))
                    {
                        issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "not-loaded", $"{cell.Target.Label} cannot be loaded from a load file; ignored.", column));
                        continue;
                    }

                    if (definition.ColumnName is { } columnName)
                    {
                        supplied.Add(columnName);
                    }

                    break;
                case FieldStorage.Coding:
                    if (!codingOverlayFieldIds.Contains(fieldId))
                    {
                        issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, "coding-field-not-enabled",
                            $"{cell.Target.Label} is a coding or privilege field that this import did not enable (Q-31).", column));
                        continue;
                    }

                    coding.Add(new ImportCodingValue(fieldId, value.DeepClone()));
                    break;
                default:
                    metadata[FieldKey.For(fieldId)] = value.DeepClone();
                    break;
            }

            if (cell.KeepRaw && cell.Raw is not null && definition.Storage != FieldStorage.Coding)
            {
                var entry = new JsonObject { ["raw"] = cell.Raw };
                if (cell.Format is { } format)
                {
                    entry["fmt"] = format;
                }

                if (definition.Type == FieldType.Date && definition.DatePrecision != DatePrecision.Date)
                {
                    entry["tz"] = cell.Column.Settings.SourceTimeZone.Id;
                }

                entry["batch"] = importBatchId.ToString();
                raw[FieldKey.For(fieldId)] = entry;
            }
        }

        ApplyUpstreamRelationships(mapped, document, supplied);
        var failed = issues.Any(i => i.Severity == ImportIssueSeverity.Error) || mapped.ControlNumberNorm is null;
        if (!failed)
        {
            document.Metadata = metadata.ToJsonString();
            document.MetadataRaw = raw.Count == 0 ? null : raw.ToJsonString();
        }

        return new ImportRow
        {
            RowNo = rowNo,
            LineNo = lineNo,
            ControlNumber = mapped.ControlNumber ?? record.ControlNumber,
            ControlNumberNorm = mapped.ControlNumberNorm,
            Document = failed ? null : document,
            SuppliedColumns = supplied,
            Coding = coding,
            Issues = issues,
        };
    }

    /// <summary>
    /// Upstream duplicate and email-thread identifiers (E09-T02). Integration point of #85: replace the body with
    /// <c>UpstreamRelationships.Apply(document, UpstreamRelationshipExtractor.Extract(mapping, mapped),
    /// UpstreamRelationshipExtractor.Options(mapping))</c> and add the relationship columns it set to
    /// <paramref name="supplied"/> (overlay writes only supplied columns). Until then only a hex dedupe hash is kept.
    /// </summary>
    private static void ApplyUpstreamRelationships(MappedRow mapped, Document document, HashSet<string> supplied)
    {
        var hash = mapped.Cells.FirstOrDefault(c => c.Target.Structural == StructuralTarget.DedupeHash && c.Error is null)?.Value?.GetValue<string>();
        if (hash is not null && UpstreamHash().IsMatch(hash.ToLowerInvariant()))
        {
            document.UpstreamDedupeHash = hash.ToLowerInvariant();
            supplied.Add("upstream_dedupe_hash");
        }
    }

    /// <summary>Sets the structural column of a system field (ADR-003 §1); false for computed or unknown fields.</summary>
    private static bool TrySetColumn(Document document, int fieldId, JsonNode value, HashSet<string> supplied)
    {
        switch (fieldId)
        {
            case SystemFields.BegBates: document.BegBates = value.GetValue<string>(); return true;
            case SystemFields.EndBates: document.EndBates = value.GetValue<string>(); return true;
            case SystemFields.BegAttach: document.BegAttach = value.GetValue<string>(); return true;
            case SystemFields.EndAttach: document.EndAttach = value.GetValue<string>(); return true;
            case SystemFields.FileName: document.FileName = value.GetValue<string>(); return true;
            case SystemFields.FileExtension: document.FileExtension = value.GetValue<string>(); return true;
            case SystemFields.FileType: document.FileType = value.GetValue<string>(); return true;
            case SystemFields.MimeType: document.MimeType = value.GetValue<string>(); return true;
            case SystemFields.FileSize: document.FileSize = Number(value); return true;
            case SystemFields.PageCount: document.PageCount = checked((int)Number(value)); return true;
            case SystemFields.DateSent: document.DateSent = Instant(value); return true;
            case SystemFields.DateReceived: document.DateReceived = Instant(value); return true;
            case SystemFields.DateCreated: document.DateCreated = Instant(value); return true;
            case SystemFields.DateLastModified: document.DateLastModified = Instant(value); return true;
            case SystemFields.FamilyDate: document.FamilyDate = Instant(value); return true;
            case SystemFields.DocumentDate:
                document.DocumentDate = Instant(value);
                document.DocumentDateSource = DocumentDateSource.Upstream;
                supplied.Add("document_date_source");
                return true;
            case SystemFields.Md5: document.Md5 = Convert.FromHexString(value.GetValue<string>()); return true;
            case SystemFields.Sha1: document.Sha1 = Convert.FromHexString(value.GetValue<string>()); return true;
            case SystemFields.Sha256: document.Sha256 = Convert.FromHexString(value.GetValue<string>()); return true;
            default: return false;
        }
    }

    private static long Number(JsonNode value) => long.Parse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonNode value) =>
        DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary><c>FieldCountMismatch</c> → <c>field-count-mismatch</c>.</summary>
    private static string Code(DatIssueKind kind) => KebabBoundary().Replace(kind.ToString(), "$1-$2").ToLowerInvariant();

    private static string Truncate(string message) =>
        message.Length <= ImportRowIssue.MaxMessageLength ? message : message[..ImportRowIssue.MaxMessageLength];

    [GeneratedRegex("^[0-9a-f]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpstreamHash();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex KebabBoundary();
}
