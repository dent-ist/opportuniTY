using System.Globalization;
using System.Text;

namespace Opportunity.Application.Workspaces;

/// <summary>
/// The acknowledgment roster as CSV (E20-T03 "exportable roster"): one row per acceptance (every version a person
/// accepted, with its text hash and time) and one Pending row for each direct member who accepted no version. RFC 4180,
/// UTF-8 with a byte-order mark; text a spreadsheet would run as a formula gets a leading apostrophe.
/// </summary>
public static class AcknowledgmentRosterCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static (byte[] Content, int Rows) Build(IReadOnlyList<AcknowledgmentRosterEntry> roster, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var text = new StringBuilder();
        Append(text, ["User ID", "Name", "Email", "Direct Member", "Status", "Version", "Current Version", "Text SHA-256", "Accepted At (UTC)"]);
        var rows = 0;
        foreach (var entry in roster)
        {
            var person = new[] { entry.UserId.ToString("D"), entry.DisplayName, entry.Email, entry.DirectMember ? "Yes" : "No" };
            if (entry.Acceptances.Count == 0)
            {
                Append(text, [.. person, "Pending", null, currentVersion == 0 ? null : Number(currentVersion), null, null]);
                rows++;
                continue;
            }

            foreach (var acceptance in entry.Acceptances)
            {
                Append(text,
                [
                    .. person,
                    acceptance.Version == currentVersion ? "Accepted" : "Superseded",
                    Number(acceptance.Version),
                    Number(currentVersion),
                    acceptance.TextSha256,
                    acceptance.AcceptedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ]);
                rows++;
            }
        }

        return ([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())], rows);
    }

    public static string FileName(DateTimeOffset generatedAt) =>
        "acknowledgment-roster-" + generatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

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
