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
        CompiledMapping mapping, DatRecord record, long rowNo, long? lineNo, Guid workspaceId, Guid importBatchId, IReadOnlySet<int> codingOverlayFieldIds)
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
        var documentId = ImportDocumentIds.For(importBatchId, rowNo);
        var document = new Document
        {
            WorkspaceId = workspaceId,
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
                // The upload string stays available to the tickets that resolve it (paths E08-T04); family sources are
                // also stored normalized (E09-T01) and duplicate/thread identifiers are applied to the document below.
                raw[StructuralRawPrefix + structural] = new JsonObject { ["raw"] = value.GetValue<string>(), ["batch"] = importBatchId.ToString() };
                SetFamilySource(mapping, document, structural, value.GetValue<string>(), cell.Column.Settings.MultiValueDelimiter, supplied, issues, column);
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

                    if (definition.ColumnName is { } columnName && fieldId != SystemFields.FamilyDate)
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

        // Derived File Type (ADR-003 §1) when the load file has none. Not a supplied column: an overlay never replaces
        // a stored file type with a derived one.
        document.FileType ??= FileTypes.Describe(document.FileExtension, document.MimeType, document.FileName);

        NormalizeAttachmentRange(mapping, document, supplied, issues);
        var relationships = ApplyUpstreamRelationships(mapping, mapped, document, supplied);
        issues.AddRange(relationships.Warnings.Select(w => new ImportRowIssue(ImportIssueSeverity.Warning, "relationship-adjusted", Truncate(w))));
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
            DuplicateGroup = failed ? null : relationships.DuplicateGroup,
            EmailThread = failed ? null : relationships.EmailThread,
            Issues = issues,
        };
    }

    /// <summary>
    /// Upstream duplicate group, dedupe/email hash and email thread (E09-T02, ADR-009 R13-R20). The columns it sets
    /// become supplied columns, so an overlay replaces them; the store records the group and thread rows.
    /// </summary>
    private static UpstreamRelationshipResult ApplyUpstreamRelationships(
        CompiledMapping mapping, MappedRow mapped, Document document, HashSet<string> supplied)
    {
        var result = UpstreamRelationships.Apply(document, UpstreamRelationshipExtractor.Extract(mapping, mapped), UpstreamRelationshipExtractor.Options(mapping));
        if (result.DuplicateGroup is not null)
        {
            supplied.Add("duplicate_group_id");
        }

        if (document.UpstreamDedupeHash is not null)
        {
            supplied.Add("upstream_dedupe_hash");
            supplied.Add("upstream_dedupe_hash_kind");
        }

        if (result.EmailThread is not null)
        {
            supplied.Add("email_thread_id");
            supplied.Add("email_thread_source");
        }

        return result;
    }

    /// <summary>
    /// The Mode B/C family sources (ADR-009 §2): ParentID (prefix already applied by the mapping) and AttachmentIDs are
    /// normalized like control numbers; the group identifier is kept trimmed and verbatim.
    /// </summary>
    private static void SetFamilySource(
        CompiledMapping mapping, Document document, StructuralTarget structural, string value, char delimiter, HashSet<string> supplied,
        List<ImportRowIssue> issues, string column)
    {
        switch (structural)
        {
            case StructuralTarget.ParentId when ControlNumber.TryNormalize(value, mapping.ControlNumberCaseSensitive, null, out var parent, out _):
                document.ParentIdNorm = parent;
                supplied.Add("parent_id_norm");
                break;
            case StructuralTarget.GroupId:
                document.GroupIdentifier = value.Trim();
                supplied.Add("group_identifier");
                break;
            case StructuralTarget.AttachmentIds:
                var ids = new List<string>();
                foreach (var id in value.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ControlNumber.TryNormalize(id, mapping.ControlNumberCaseSensitive, mapping.EffectiveProfile.ControlNumberPrefix, out var norm, out _))
                    {
                        ids.Add(norm);
                    }
                    else
                    {
                        issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "family-source-ignored",
                            Truncate($"Attachment IDs: '{id}' is not a valid control number; ignored."), column));
                    }
                }

                if (ids.Count > 0)
                {
                    document.AttachmentIdsNorm = [.. ids.Distinct(StringComparer.Ordinal)];
                    supplied.Add("attachment_ids_norm");
                }

                break;
        }
    }

    /// <summary>Mode A sources: BegAttach/EndAttach name control numbers, so they take the import prefix and normalization.</summary>
    private static void NormalizeAttachmentRange(CompiledMapping mapping, Document document, HashSet<string> supplied, List<ImportRowIssue> issues)
    {
        string? Normalize(string? value, string label)
        {
            if (value is null)
            {
                return null;
            }

            if (ControlNumber.TryNormalize(value, mapping.ControlNumberCaseSensitive, mapping.EffectiveProfile.ControlNumberPrefix, out var norm, out _))
            {
                return norm;
            }

            issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "family-source-ignored",
                Truncate($"{label} '{value}' is not a valid control number; it is kept but not used to build families.")));
            return null;
        }

        if (supplied.Contains("beg_attach"))
        {
            document.BegAttachNorm = Normalize(document.BegAttach, "BegAttach");
            supplied.Add("beg_attach_norm");
        }

        if (supplied.Contains("end_attach"))
        {
            document.EndAttachNorm = Normalize(document.EndAttach, "EndAttach");
            supplied.Add("end_attach_norm");
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
            case SystemFields.FamilyDate:
                // FamilyDate is derived by family resolution (ADR-009 R25); the mapped value is its upstream input.
                document.UpstreamFamilyDate = Instant(value);
                supplied.Add("upstream_family_date");
                return true;
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

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex KebabBoundary();
}
