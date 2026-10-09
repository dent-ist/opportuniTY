using System.Globalization;
using System.Text;

using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>
/// The privilege conflict report as CSV (E13-T02, ticket-review note: "plus CSV download"): one row per reported
/// member, RFC 4180, UTF-8 with a byte-order mark; text a spreadsheet would run as a formula gets a leading apostrophe.
/// It holds exactly what the JSON report holds for the same caller (only visible documents, Q-52).
/// </summary>
public static class PrivilegeConflictReportCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static byte[] Build(PrivilegeConflictReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var catalog = report.Catalog;
        var responsiveness = report.ResponsivenessFieldId is { } r ? catalog.Find(r) : null;
        var header = new List<string>
        {
            "Conflict", "Group ID", "Reasons", "Document ID", "Control Number", "Family Sequence", "Duplicate Primary",
            Name(catalog, PrivilegeFields.Status), "Status Set By", "Status Set At (UTC)",
            Name(catalog, PrivilegeFields.Basis), "Basis Set By", "Basis Set At (UTC)",
        };
        if (responsiveness is not null)
        {
            header.AddRange([responsiveness.Name, responsiveness.Name + " Set By", responsiveness.Name + " Set At (UTC)"]);
        }

        if (report.ProductionId is not null)
        {
            header.Add("In Production");
        }

        var text = new StringBuilder();
        Append(text, header);
        foreach (var group in report.Groups)
        {
            foreach (var member in group.Members)
            {
                var row = new List<string?>
                {
                    group.Kind == PrivilegeConflictKind.Family ? "Family" : "Duplicates",
                    group.GroupId.ToString("D"),
                    string.Join("; ", ReasonTexts(group.Reasons)),
                    member.DocumentId.ToString("D"),
                    member.ControlNumber,
                    member.FamilySequence.ToString(CultureInfo.InvariantCulture),
                    member.IsDuplicatePrimary ? "Yes" : "No",
                };
                AddValue(row, report, PrivilegeFields.Status, member.Status);
                AddValue(row, report, PrivilegeFields.Basis, member.Basis);
                if (responsiveness is not null)
                {
                    AddValue(row, report, responsiveness.FieldId, member.Responsiveness);
                }

                if (report.ProductionId is not null)
                {
                    row.Add(member.InProduction ? "Yes" : "No");
                }

                Append(text, row);
            }
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Choice names of <paramref name="choiceIds"/> in choice order.</summary>
    public static IReadOnlyList<string> ChoiceNames(FieldCatalog catalog, int fieldId, IReadOnlyCollection<int> choiceIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(choiceIds);
        return choiceIds.Count == 0
            ? []
            : [.. catalog.ChoicesOf(fieldId).Where(c => choiceIds.Contains(c.ChoiceId)).OrderBy(c => c.SortOrder).ThenBy(c => c.ChoiceId).Select(c => c.Name)];
    }

    public static IEnumerable<string> ReasonTexts(PrivilegeConflictReasons reasons)
    {
        if (reasons.HasFlag(PrivilegeConflictReasons.WithheldMember))
        {
            yield return "Withheld member in a family that is not withheld as a whole";
        }

        if (reasons.HasFlag(PrivilegeConflictReasons.ResponsivenessDiffers))
        {
            yield return "Responsiveness calls differ within the family";
        }

        if (reasons.HasFlag(PrivilegeConflictReasons.PrivilegeCallsDiffer))
        {
            yield return "Privilege calls differ among duplicates";
        }
    }

    public static string FileName(PrivilegeConflictReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return "privilege-conflicts-" + report.GeneratedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
    }

    private static void AddValue(List<string?> row, PrivilegeConflictReport report, int fieldId, PrivilegeCodedValue value)
    {
        row.Add(string.Join("; ", ChoiceNames(report.Catalog, fieldId, value.ChoiceIds)));
        row.Add(value.ChangedBy is { } by ? report.DisplayNames.GetValueOrDefault(by) ?? by.ToString("D") : null);
        row.Add(value.ChangedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static string Name(FieldCatalog catalog, int fieldId) => catalog.Find(fieldId)?.Name ?? FieldKey.For(fieldId);

    private static void Append(StringBuilder text, IEnumerable<string?> cells) =>
        text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    private static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '@' or '\t' or '\r' || (v[0] == '-' && (v.Length == 1 || !char.IsDigit(v[1])))))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
