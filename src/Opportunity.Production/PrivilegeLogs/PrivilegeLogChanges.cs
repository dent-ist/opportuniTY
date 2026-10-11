using Opportunity.Application.PrivilegeLogs;

namespace Opportunity.Production.PrivilegeLogs;

/// <summary>One document's difference between two versions (<see cref="Current"/> or <see cref="Previous"/> is absent when added or removed).</summary>
public sealed record PrivilegeLogEntryDifference(
    PrivilegeLogEntryRow? Current,
    PrivilegeLogEntryRow? Previous,
    IReadOnlyList<(string Column, string Before, string After)> ChangedCells);

/// <summary>What changed between two versions of a log (#182: "regenerate after coding changes with a diff").</summary>
public sealed record PrivilegeLogComparison(
    IReadOnlyList<PrivilegeLogEntryDifference> Differences,
    int Added,
    int Removed,
    int Changed,
    int Unchanged,
    IReadOnlyList<string> ColumnsAdded,
    IReadOnlyList<string> ColumnsRemoved);

/// <summary>
/// Compares two versions' entries keyed by document. Cells are compared by column header (the first column of a header,
/// should a template repeat one); a treatment change counts as a change of the "Treatment" pseudo-column unless a column
/// already shows it changing. Added and changed entries come in the current version's order, then removed ones in the earlier
/// version's order. Pure; both versions are bounded by <see cref="PrivilegeLogService.MaxEntries"/>.
/// </summary>
public static class PrivilegeLogChanges
{
    public const string TreatmentColumn = "Treatment";

    public static PrivilegeLogComparison Compare(
        IReadOnlyList<string> currentColumns, IReadOnlyList<PrivilegeLogEntryRow> current,
        IReadOnlyList<string> previousColumns, IReadOnlyList<PrivilegeLogEntryRow> previous)
    {
        ArgumentNullException.ThrowIfNull(currentColumns);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previousColumns);
        ArgumentNullException.ThrowIfNull(previous);

        var currentIndex = FirstIndexByHeader(currentColumns);
        var previousIndex = FirstIndexByHeader(previousColumns);
        var common = currentIndex.Where(c => previousIndex.ContainsKey(c.Key)).OrderBy(c => c.Value)
            .Select(c => (Column: c.Key, Now: c.Value, Before: previousIndex[c.Key])).ToList();

        var before = new Dictionary<Guid, PrivilegeLogEntryRow>(previous.Count);
        foreach (var row in previous)
        {
            before[row.DocumentId] = row;
        }

        var differences = new List<PrivilegeLogEntryDifference>();
        var seen = new HashSet<Guid>();
        int added = 0, changed = 0, unchanged = 0;
        foreach (var row in current)
        {
            seen.Add(row.DocumentId);
            if (!before.TryGetValue(row.DocumentId, out var old))
            {
                added++;
                differences.Add(new PrivilegeLogEntryDifference(row, null, []));
                continue;
            }

            var cells = new List<(string, string, string)>();
            foreach (var (column, now, was) in common)
            {
                var a = Cell(old.Cells, was);
                var b = Cell(row.Cells, now);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    cells.Add((column, a, b));
                }
            }

            if (old.Treatment != row.Treatment)
            {
                var (wasText, nowText) = (PrivilegeLogRules.TreatmentText(old.Treatment), PrivilegeLogRules.TreatmentText(row.Treatment));
                if (!cells.Any(c => c.Item2 == wasText && c.Item3 == nowText))
                {
                    cells.Add((TreatmentColumn, wasText, nowText));
                }
            }

            if (cells.Count == 0)
            {
                unchanged++;
                continue;
            }

            changed++;
            differences.Add(new PrivilegeLogEntryDifference(row, old, cells));
        }

        var removed = 0;
        foreach (var row in previous)
        {
            if (!seen.Contains(row.DocumentId))
            {
                removed++;
                differences.Add(new PrivilegeLogEntryDifference(null, row, []));
            }
        }

        return new PrivilegeLogComparison(differences, added, removed, changed, unchanged,
            [.. currentColumns.Distinct(StringComparer.Ordinal).Where(c => !previousIndex.ContainsKey(c))],
            [.. previousColumns.Distinct(StringComparer.Ordinal).Where(c => !currentIndex.ContainsKey(c))]);
    }

    private static Dictionary<string, int> FirstIndexByHeader(IReadOnlyList<string> columns)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            index.TryAdd(columns[i], i);
        }

        return index;
    }

    private static string Cell(IReadOnlyList<string> cells, int index) => index < cells.Count ? cells[index] : string.Empty;
}
