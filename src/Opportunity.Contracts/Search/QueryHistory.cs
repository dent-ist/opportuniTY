namespace Opportunity.Contracts.Search;

/// <summary>Body of <c>POST /api/v1/workspaces/{workspaceId}/query-history</c>: a query the caller just ran.</summary>
public sealed record QueryHistoryRequest(string? Query);

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/query-history</c>: the caller's recent queries in the workspace, newest first,
/// distinct, at most 50. Only the caller's own history is ever returned.
/// </summary>
public sealed record QueryHistoryResource(IReadOnlyList<QueryHistoryEntryResource> Items);

/// <param name="Query">The query text as run (trimmed).</param>
/// <param name="RanAt">Server time of the latest run of this text.</param>
public sealed record QueryHistoryEntryResource(string Query, DateTimeOffset RanAt);
