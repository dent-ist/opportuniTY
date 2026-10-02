namespace Opportunity.Contracts.Api;

/// <summary>
/// The only collection shape in the API (ADR-019 §2.8): cursor pagination, no offsets. <see cref="NextCursor"/> is
/// opaque and bound to (user, workspace); it is <c>null</c> on the last page.
/// </summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, TotalCount Total);

public sealed record TotalCount(long Value, TotalRelation Relation);

public enum TotalRelation
{
    /// <summary>The total is exact.</summary>
    Eq,

    /// <summary>The total is a lower bound (counting stopped early, Q-10).</summary>
    Gte,
}

public static class Pagination
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
}
