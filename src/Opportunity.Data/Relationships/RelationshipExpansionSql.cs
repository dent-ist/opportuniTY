using System.Text.RegularExpressions;

using Opportunity.Core.Documents;

namespace Opportunity.Data.Relationships;

/// <summary>
/// The PostgreSQL form of <see cref="RelationshipExpansion"/> (E09-T03, ADR-002 §5.2.1): expands a seed set held in a
/// temporary table, set-based and inside the caller's transaction, from the authoritative relationship columns
/// (<c>family_id</c>, <c>duplicate_group_id</c>, <c>email_thread_id</c>). Used by the snapshot freeze; any other set
/// computation over PostgreSQL (e.g. "with family" counts of a search-term report) can call it with its own seed table.
/// It adds live documents only and never authorizes: the caller authorizes what was added for the person it acts for
/// (Q-11, Q-13, Q-52), exactly as it authorizes the seeds.
/// </summary>
internal static partial class RelationshipExpansionSql
{
    /// <summary>
    /// Creates <paramref name="target"/> (<c>document_id uuid PRIMARY KEY, reason smallint</c>, dropped at commit) with the
    /// documents the expansion adds to the seeds in <paramref name="seedTable"/> (any table with a <c>document_id</c>
    /// column), in the steps of <see cref="RelationshipExpansion.Steps"/>: each step adds only documents that are neither
    /// seeds nor added by an earlier step, so a document keeps its first reason (<see cref="RelationshipKind"/> values,
    /// the snapshot inclusion reasons). Returns how many documents were added.
    /// </summary>
    public static async Task<long> ExpandAsync(
        WorkspaceTransaction tx, string seedTable, string target, RelationshipExpansion expansion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        RequireIdentifier(seedTable);
        RequireIdentifier(target);
        await using (var create = tx.Command(
            $"CREATE TEMP TABLE {target} (document_id uuid PRIMARY KEY, reason smallint NOT NULL) ON COMMIT DROP"))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long added = 0;
        foreach (var step in expansion.Steps)
        {
            var column = step.Kind switch
            {
                RelationshipKind.Family => "family_id",
                RelationshipKind.Duplicate => "duplicate_group_id",
                RelationshipKind.Thread => "email_thread_id",
                _ => throw new InvalidOperationException($"Unknown relationship kind {step.Kind}."),
            };
            var seeds = step.Seed == ExpansionSeed.Base
                ? $"SELECT s.document_id FROM {seedTable} s"
                : $"SELECT t.document_id FROM {target} t WHERE t.reason IN ({(short)RelationshipKind.Duplicate}, {(short)RelationshipKind.Thread})";

            // The keys of the seeds first (a small set), then their members through the (workspace_id, key) index.
            await using var insert = tx.Command(
                $"""
                INSERT INTO {target} (document_id, reason)
                SELECT d.document_id, @reason
                  FROM opportunity.document d
                  JOIN opportunity.document_projection_state ps
                    ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
                 WHERE d.workspace_id = @ws
                   AND d.{column} IN (SELECT DISTINCT k.{column}
                                        FROM opportunity.document k
                                       WHERE k.workspace_id = @ws AND k.{column} IS NOT NULL AND k.document_id IN ({seeds}))
                   AND NOT EXISTS (SELECT 1 FROM {seedTable} x WHERE x.document_id = d.document_id)
                ON CONFLICT (document_id) DO NOTHING
                """);
            insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
            insert.Parameters.AddWithValue("reason", (short)step.Kind);
            added += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var analyze = tx.Command($"ANALYZE {target}"))
        {
            await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    private static void RequireIdentifier(string name)
    {
        if (!Identifier().IsMatch(name))
        {
            throw new ArgumentException("Expected a plain lower-case table name.", nameof(name));
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
