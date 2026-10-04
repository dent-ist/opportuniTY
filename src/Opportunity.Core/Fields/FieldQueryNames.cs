using System.Globalization;
using System.Text;

namespace Opportunity.Core.Fields;

/// <summary>
/// Query-language names of a workspace's fields (ADR-008 R11): structural fields use the fixed names of ADR-007 §3;
/// other fields get a default from the display name (lower-case, runs of non-alphanumerics → <c>_</c>), with
/// collisions suffixed <c>_2</c>, <c>_3</c>, … in field-id order. Admin-edited names are a later addition.
/// </summary>
public static class FieldQueryNames
{
    /// <summary>System column fields with a fixed name (ADR-007 §3).</summary>
    public static readonly IReadOnlyDictionary<int, string> Structural = new Dictionary<int, string>
    {
        [SystemFields.ControlNumber] = "controlnumber",
        [SystemFields.BegBates] = "begbates",
        [SystemFields.EndBates] = "endbates",
        [SystemFields.FileName] = "filename",
        [SystemFields.FileExtension] = "extension",
        [SystemFields.FileType] = "filetype",
        [SystemFields.MimeType] = "mimetype",
        [SystemFields.FileSize] = "filesize",
        [SystemFields.PageCount] = "pagecount",
        [SystemFields.DateSent] = "datesent",
        [SystemFields.DateReceived] = "datereceived",
        [SystemFields.DateCreated] = "datecreated",
        [SystemFields.DateLastModified] = "datelastmodified",
        [SystemFields.DocumentDate] = "date",
        [SystemFields.FamilyDate] = "familydate",
        [SystemFields.Md5] = "md5",
        [SystemFields.Sha1] = "sha1",
        [SystemFields.Sha256] = "sha256",
        [SystemFields.TextLength] = "textlength",
        [SystemFields.TextTruncated] = "texttruncated",
        [SystemFields.TextMissing] = "textmissing",
        [SystemFields.NativeMissing] = "nativemissing",
        [SystemFields.ImagesIncomplete] = "imagesincomplete",
    };

    /// <summary>
    /// Names that are taken even though no FieldDefinition carries them: relationship fields addressed directly
    /// (ADR-007 §3), the default field, and the injected-only fields that must never resolve (ADR-008 R11).
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "text", "familyid", "familysequence", "familystatus", "duplicategroup", "duplicateprimary", "threadid",
        "workspaceid", "securitytags", "documentid", "projectionversion",
    };

    /// <summary>Query name per field id for every field of <paramref name="fields"/>.</summary>
    public static IReadOnlyDictionary<int, string> Assign(IEnumerable<FieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var ordered = fields.OrderBy(f => f.FieldId).ToList();
        var taken = new HashSet<string>(Reserved, StringComparer.Ordinal);
        var names = new Dictionary<int, string>();
        foreach (var field in ordered)
        {
            if (field.IsSystem && Structural.TryGetValue(field.FieldId, out var fixedName))
            {
                names[field.FieldId] = fixedName;
                taken.Add(fixedName);
            }
        }

        foreach (var field in ordered.Where(f => !names.ContainsKey(f.FieldId)))
        {
            var baseName = Default(field.Name, field.FieldId);
            var name = baseName;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{baseName}_{n}");
            }

            names[field.FieldId] = name;
        }

        return names;
    }

    /// <summary>The generated default: starts with a letter (a QL field name rule), so <c>2024 Notes</c> → <c>f_2024_notes</c>.</summary>
    public static string Default(string displayName, int fieldId)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var builder = new StringBuilder(displayName.Length);
        var pendingSeparator = false;
        foreach (var c in displayName.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        if (builder.Length == 0)
        {
            return FieldKey.For(fieldId);
        }

        return char.IsLetter(builder[0]) ? builder.ToString() : "f_" + builder;
    }
}
