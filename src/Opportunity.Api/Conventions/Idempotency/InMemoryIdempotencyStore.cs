using Opportunity.Application.Idempotency;

namespace Opportunity.Api.Conventions.Idempotency;

/// <summary>
/// INTERIM adapter until the PostgreSQL store lands (E04/E06): process-local, lost on restart and not shared between
/// API replicas, so it only guarantees idempotency for a single-instance deployment. Registered with TryAdd so the
/// durable <see cref="IIdempotencyStore"/> replaces it without touching the API host.
/// </summary>
internal sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private const int SweepEvery = 1024;

    private readonly Dictionary<IdempotencyScope, Entry> _entries = [];
    private readonly Lock _gate = new();
    private int _beginsSinceSweep;

    public ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyScope scope,
        string requestHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (++_beginsSinceSweep >= SweepEvery)
            {
                Sweep(now);
            }

            if (_entries.TryGetValue(scope, out var entry) && entry.ExpiresAt > now)
            {
                IdempotencyBeginResult result = entry.RequestHash != requestHash ? IdempotencyBeginResult.Mismatch
                    : entry.Response is null ? IdempotencyBeginResult.InProgress
                    : IdempotencyBeginResult.Replay(entry.Response);
                return ValueTask.FromResult(result);
            }

            _entries[scope] = new Entry(requestHash, expiresAt, Response: null);
            return ValueTask.FromResult(IdempotencyBeginResult.Acquired);
        }
    }

    public ValueTask CompleteAsync(IdempotencyScope scope, IdempotentResponse response, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var entry))
            {
                _entries[scope] = entry with { Response = response };
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(IdempotencyScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _entries.Remove(scope);
        }

        return ValueTask.CompletedTask;
    }

    private void Sweep(DateTimeOffset now)
    {
        _beginsSinceSweep = 0;
        foreach (var expired in _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
        {
            _entries.Remove(expired);
        }
    }

    private sealed record Entry(string RequestHash, DateTimeOffset ExpiresAt, IdempotentResponse? Response);
}
