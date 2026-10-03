using Opportunity.Core.Fields;

namespace Opportunity.Search.Projection;

/// <summary>
/// Physical projection paths of generation 2 (ADR-007 §3, R3, R6; interim Candidate A coding under <c>coding.*</c>).
/// Paths never leave <c>Opportunity.Search</c>: the API and the query language address fields by id and query name;
/// the builder and, later, the query planner translate here.
/// </summary>
internal static class ProjectionFieldPaths
{
    public const string Metadata = "metadata";
    public const string MetadataOverflow = "metadataOverflow";
    public const string Coding = "coding";
    public const string CodingOverflow = "codingOverflow";

    /// <summary>System column fields → structural property (only fields that are searchable as such).</summary>
    public static readonly IReadOnlyDictionary<int, string> Structural = new Dictionary<int, string>
    {
        [SystemFields.ControlNumber] = "controlNumber",
        [SystemFields.BegBates] = "begBates",
        [SystemFields.EndBates] = "endBates",
        [SystemFields.BegAttach] = "begAttach",
        [SystemFields.EndAttach] = "endAttach",
        [SystemFields.FileName] = "fileName",
        [SystemFields.FileExtension] = "fileExtension",
        [SystemFields.FileType] = "fileType",
        [SystemFields.MimeType] = "mimeType",
        [SystemFields.FileSize] = "fileSize",
        [SystemFields.PageCount] = "pageCount",
        [SystemFields.DateSent] = "dateSent",
        [SystemFields.DateReceived] = "dateReceived",
        [SystemFields.DateCreated] = "dateCreated",
        [SystemFields.DateLastModified] = "dateLastModified",
        [SystemFields.DocumentDate] = "documentDate",
        [SystemFields.FamilyDate] = "familyDate",
        [SystemFields.Md5] = "md5",
        [SystemFields.Sha1] = "sha1",
        [SystemFields.Sha256] = "sha256",
        [SystemFields.TextLength] = "textLength",
        [SystemFields.TextTruncated] = "textTruncated",
        [SystemFields.TextMissing] = "textMissing",
        [SystemFields.NativeMissing] = "nativeMissing",
        [SystemFields.ImagesIncomplete] = "imagesIncomplete",
    };

    /// <summary>Natural-sort companions of the structural keyword fields (ADR-009 R5).</summary>
    public static readonly IReadOnlyDictionary<string, string> SortKeyFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["controlNumber"] = "controlNumberSort",
        ["begBates"] = "begBatesSort",
        ["endBates"] = "endBatesSort",
    };

    /// <summary>
    /// Where a field's values live in the projection, or null when the field is not searchable. Overflow fields are a
    /// key of the storage's <c>flat_object</c> container.
    /// </summary>
    public static string? For(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Storage == FieldStorage.Column)
        {
            return Structural.GetValueOrDefault(field.FieldId);
        }

        if (!field.IsSearchable || field.SearchSlot is not { } slot || field.IsDeleted)
        {
            return null;
        }

        var coding = field.Storage == FieldStorage.Coding;
        return slot == FieldRules.OverflowSlot
            ? $"{(coding ? CodingOverflow : MetadataOverflow)}.{field.Key}"
            : $"{(coding ? Coding : Metadata)}.{slot}";
    }
}
