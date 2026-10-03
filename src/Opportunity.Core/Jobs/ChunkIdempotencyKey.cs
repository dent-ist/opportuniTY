using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Opportunity.Core.Jobs;

/// <summary>
/// The idempotency key formula of ADR-010 §5.1. Stable across re-publish; unique per workspace on <c>job_chunk</c> and
/// <c>index_chunk_task</c>. Unrelated to the HTTP <c>Idempotency-Key</c>, which deduplicates job creation.
/// </summary>
public static class ChunkIdempotencyKey
{
    /// <summary>Length of a key: lowercase hex of a SHA-256 digest.</summary>
    public const int Length = 64;

    /// <param name="projectionGeneration">Target generation for <see cref="ChunkOperationKind.ReindexChunk"/>; 0 otherwise.</param>
    public static string ForChunk(
        Guid workspaceId, Guid jobId, int chunkSequence, ChunkOperationKind operationKind, long projectionGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(projectionGeneration);
        if (!Enum.IsDefined(operationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, "Unknown operation kind.");
        }

        return Hash(string.Join('|',
            "v1",
            Id(workspaceId),
            Id(jobId),
            chunkSequence.ToString(CultureInfo.InvariantCulture),
            operationKind.ToString(),
            projectionGeneration.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Key of an interactive SearchOutbox message: <c>SHA-256("v1|" + WorkspaceId + "|outbox|" + OutboxId)</c>.</summary>
    public static string ForOutbox(Guid workspaceId, Guid outboxId) =>
        Hash($"v1|{Id(workspaceId)}|outbox|{Id(outboxId)}");

    /// <summary>The same formula for the <c>bigint</c> OutboxId of <c>search_outbox</c> (ADR-001 §1 R3), in invariant decimal.</summary>
    public static string ForOutbox(Guid workspaceId, long outboxId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outboxId);
        return Hash($"v1|{Id(workspaceId)}|outbox|{outboxId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
