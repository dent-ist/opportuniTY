namespace Opportunity.Application.Search;

/// <summary>
/// The query bar's per-user, per-workspace history (#186): distinct query texts, newest run first, trimmed to
/// <see cref="MaxEntries"/>. Query text is search text (Q-16), so it lives only in PostgreSQL under RLS, never in the
/// browser. Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface IQueryHistoryStore
{
    /// <summary>Entries kept per user and workspace.</summary>
    public const int MaxEntries = 50;

    /// <summary>The user's entries in the workspace, newest first (at most <see cref="MaxEntries"/>).</summary>
    Task<IReadOnlyList<QueryHistoryRecord>> ListAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a run of <paramref name="queryText"/> (already trimmed and validated): an earlier entry with the same
    /// text moves to the top, and entries beyond <see cref="MaxEntries"/> are removed.
    /// </summary>
    Task RecordAsync(Guid workspaceId, Guid userId, string queryText, DateTimeOffset ranAt, CancellationToken cancellationToken = default);
}

public sealed record QueryHistoryRecord(string QueryText, DateTimeOffset LastRunAt);
