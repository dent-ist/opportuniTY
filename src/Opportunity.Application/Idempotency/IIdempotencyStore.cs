namespace Opportunity.Application.Idempotency;

/// <summary>
/// Where an <c>Idempotency-Key</c> is valid (ADR-019 §2.6): the same key may be reused by another user, in another
/// workspace or on another route without colliding.
/// </summary>
public sealed record IdempotencyScope(string Principal, string WorkspaceId, string Route, string Key);

/// <summary>The response recorded for a completed request, replayed verbatim on a retry.</summary>
public sealed record IdempotentResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

public enum IdempotencyBeginOutcome
{
    /// <summary>First use of the key: the caller executes the request, then completes or releases the key.</summary>
    Acquired,

    /// <summary>Same key and same request hash already completed: replay <see cref="IdempotencyBeginResult.Response"/>.</summary>
    Replay,

    /// <summary>Same key with a different request hash: reject (422 <c>idempotency-key-reuse</c>).</summary>
    Mismatch,

    /// <summary>Same key is still executing in another request: reject and let the client retry later.</summary>
    InProgress,
}

public sealed record IdempotencyBeginResult(IdempotencyBeginOutcome Outcome, IdempotentResponse? Response = null)
{
    public static IdempotencyBeginResult Acquired { get; } = new(IdempotencyBeginOutcome.Acquired);
    public static IdempotencyBeginResult Mismatch { get; } = new(IdempotencyBeginOutcome.Mismatch);
    public static IdempotencyBeginResult InProgress { get; } = new(IdempotencyBeginOutcome.InProgress);

    public static IdempotencyBeginResult Replay(IdempotentResponse response) => new(IdempotencyBeginOutcome.Replay, response);
}

/// <summary>
/// Port for idempotency-key records. Implementations must make <see cref="TryBeginAsync"/> atomic per scope and keep
/// completed records until <c>expiresAt</c> (at least 24 h). The durable adapter belongs in <c>Opportunity.Data</c>
/// (E04/E06) so the record commits in the same transaction as the job it created.
/// </summary>
public interface IIdempotencyStore
{
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyScope scope,
        string requestHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(IdempotencyScope scope, IdempotentResponse response, CancellationToken cancellationToken);

    /// <summary>Forgets an acquired key whose request failed, so the client can retry with the same key.</summary>
    ValueTask ReleaseAsync(IdempotencyScope scope, CancellationToken cancellationToken);
}
