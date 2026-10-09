namespace Opportunity.Application.Workspaces.Deletion;

/// <summary>
/// The order in which the database purge empties the tenant tables (ADR-014 §4 step 5): a table goes before every
/// table its rows reference, so no batch ever breaks a foreign key. It is derived from the live schema at run time, so
/// a table added later is purged without code changes; a new reference cycle fails the run (and the schema test) until
/// it is handled here.
/// <para>The one cycle in the schema is documents, their page sets and their stored objects (each references another
/// with a deferred key). It is purged as one unit: each batch of documents goes together with its page sets and
/// objects (<c>workspace_purge_batch</c>'s document step), then the remaining page sets and the objects that belong to
/// no document (exports, reports).</para>
/// </summary>
public static class WorkspacePurgePlan
{
    public const string Document = "document";

    /// <summary>Purged with each batch of documents, in this order after <see cref="Document"/>.</summary>
    public static IReadOnlyList<string> DocumentCluster { get; } = [Document, "page_set", "stored_object"];

    /// <summary>The tables in purge order. Throws <see cref="InvalidOperationException"/> on a cycle it does not know.</summary>
    public static IReadOnlyList<string> Order(IReadOnlyCollection<string> tables, IEnumerable<PurgeTableReference> references)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(references);
        var known = tables.ToHashSet(StringComparer.Ordinal);
        var cluster = DocumentCluster.All(known.Contains) ? DocumentCluster.ToHashSet(StringComparer.Ordinal) : [];
        string Node(string table) => cluster.Contains(table) ? Document : table;

        // referencedBy[parent] = children whose rows point at parent's rows (they must be purged first).
        var nodes = known.Select(Node).ToHashSet(StringComparer.Ordinal);
        var referencedBy = nodes.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!known.Contains(reference.Table) || !known.Contains(reference.ReferencedTable))
            {
                continue;
            }

            var child = Node(reference.Table);
            var parent = Node(reference.ReferencedTable);
            if (child != parent)
            {
                referencedBy[parent].Add(child);
            }
        }

        var order = new List<string>(known.Count);
        var remaining = new SortedSet<string>(nodes, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            // A node is ready once no remaining node references it.
            var ready = remaining.Where(n => !referencedBy[n].Overlaps(remaining)).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException(
                    "The tenant tables reference each other in a cycle the workspace purge cannot order: " + string.Join(", ", remaining) + ".");
            }

            foreach (var node in ready)
            {
                remaining.Remove(node);
                if (node == Document && cluster.Count > 0)
                {
                    order.AddRange(DocumentCluster);
                }
                else
                {
                    order.Add(node);
                }
            }
        }

        return order;
    }
}
