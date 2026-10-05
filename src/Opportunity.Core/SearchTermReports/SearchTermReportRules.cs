using System.Text;

namespace Opportunity.Core.SearchTermReports;

/// <summary>Where a search term report counts: the workspace, a saved search's results or an existing snapshot.</summary>
public enum SearchTermReportScopeKind
{
    Workspace,
    SavedSearch,
    Snapshot,
}

/// <summary>The state of a report's current run.</summary>
public enum SearchTermReportStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>One named term as entered.</summary>
public sealed record SearchTermInput(string Name, string Expression);

/// <summary>Limits and the CSV term-list format of search term reports (E07-T10).</summary>
public static class SearchTermReportRules
{
    public const int MaxNameLength = 200;
    public const int MaxTerms = 500;
    public const int MaxExpressionLength = 10_000;

    /// <summary>The largest pasted CSV term list, in characters.</summary>
    public const int MaxCsvLength = 1_000_000;

    /// <summary>
    /// Parses a pasted term list in the <c>Name,Expression</c> CSV form (RFC 4180 quoting; a header row
    /// <c>Name,Expression</c> is skipped; a line with one value uses it as both name and expression; blank lines are
    /// ignored; any further columns are an error). Returns null with <paramref name="error"/> when the text is malformed.
    /// </summary>
    public static IReadOnlyList<SearchTermInput>? ParseCsv(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        error = null;
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    line += c == '\n' ? 1 : 0;
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    quoted = true;
                    fieldStarted = true;
                    break;
                case '"':
                    error = $"Line {line}: a quote inside an unquoted value; quote the whole value and double the quote.";
                    return null;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    rows.Add(row);
                    row = [];
                    field.Clear();
                    fieldStarted = false;
                    line++;
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (quoted)
        {
            error = $"Line {line}: a quoted value is not closed.";
            return null;
        }

        row.Add(field.ToString());
        rows.Add(row);

        var terms = new List<SearchTermInput>();
        for (var r = 0; r < rows.Count; r++)
        {
            var values = rows[r];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (values.Count > 2)
            {
                error = $"Row {r + 1}: expected Name,Expression (quote values that contain commas).";
                return null;
            }

            var name = values[0].Trim();
            var expression = values.Count == 2 ? values[1].Trim() : name;
            if (terms.Count == 0 && string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)
                && string.Equals(expression, "Expression", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            terms.Add(new SearchTermInput(name, expression));
        }

        return terms;
    }

    /// <summary>A display name: 1–<see cref="MaxNameLength"/> characters, trimmed, no control characters.</summary>
    public static bool IsValidName(string? name) =>
        name is not null && name.Trim().Length is >= 1 and <= MaxNameLength && !name.Any(char.IsControl);
}
