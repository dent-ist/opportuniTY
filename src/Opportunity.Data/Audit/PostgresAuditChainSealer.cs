using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Keys;

namespace Opportunity.Data.Audit;

/// <summary>
/// The data source of a login in <c>opportunity_audit_sealer</c> (ADR-013 §1.2): it reads every chain and may set
/// only the reserved chain columns, once. Never the application login, whose audit access is confined to one chain.
/// Disposing it disposes the data source.
/// </summary>
public sealed class AuditChainDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable, IDisposable
{
    /// <summary><c>ConnectionStrings:AuditSealer</c>, the sealer login's connection string.</summary>
    public const string ConnectionStringName = "AuditSealer";

    public NpgsqlDataSource DataSource { get; } = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    public void Dispose() => DataSource.Dispose();
}

public sealed class AuditChainOptions
{
    /// <summary>ADR-013 §3.2: the sealer runs every second.</summary>
    public TimeSpan SealInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>ADR-013 §3.4: a checkpoint every 10 minutes when the chain advanced.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Events sealed per transaction; a chain with a backlog is sealed in several.</summary>
    public int BatchSize { get; init; } = 1000;
}

/// <summary>The chain was found broken while taking a checkpoint; nothing is signed over it.</summary>
public sealed class AuditChainBrokenException(Guid chainId, string message)
    : InvalidOperationException($"Audit chain {AuditChainFormat.DescribeChain(chainId)} is broken: {message}")
{
    public Guid ChainId { get; } = chainId;
}

/// <summary>
/// <see cref="IAuditChainSealer"/> on <c>audit.audit_event</c> (ADR-013 §3.2–§3.4). One active sealer per chain: a
/// transaction-scoped advisory lock lets concurrent sealers (dispatcher replicas, the CLI) skip a busy chain, and the
/// chain head row lock serializes them regardless. Each batch reads committed unsealed events in
/// <c>(recorded_at, event_id)</c> order, hashes them in process and writes sequence, hashes and head in one transaction.
/// Interactive writers never touch the chain, so their latency is unaffected.
/// </summary>
public sealed partial class PostgresAuditChainSealer(
    AuditChainDataSource chainDataSource,
    ISigningKeyProvider signer,
    IAuditEventWriter? audit = null,
    TimeProvider? time = null,
    AuditChainOptions? options = null,
    ILogger<PostgresAuditChainSealer>? logger = null) : IAuditChainSealer
{
    public const string ActorId = "service:audit-sealer";

    private readonly NpgsqlDataSource _dataSource = chainDataSource.DataSource;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AuditChainOptions _options = options ?? new AuditChainOptions();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<AuditSealResult> SealAsync(Guid? chainId = null, CancellationToken cancellationToken = default)
    {
        var chains = chainId is { } one ? [one] : await UnsealedChainsAsync(cancellationToken).ConfigureAwait(false);
        long total = 0;
        var busy = 0;
        var failed = 0;
        foreach (var chain in chains)
        {
            try
            {
                while (true)
                {
                    var (count, locked) = await SealBatchAsync(chain, cancellationToken).ConfigureAwait(false);
                    total += count;
                    if (!locked)
                    {
                        busy++;
                        break;
                    }

                    if (count < _options.BatchSize)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (chainId is null && ex is not OperationCanceledException)
            {
                // One chain that cannot be sealed must not hold back the others; it is retried next round.
                failed++;
                LogSealFailed(_logger, chain, ex);
            }
        }

        return new AuditSealResult(chains.Count, total, busy, failed);
    }

    public async Task<IReadOnlyList<AuditCheckpoint>> CheckpointAsync(
        AuditCheckpointReason reason, Guid? chainId = null, CancellationToken cancellationToken = default)
    {
        await SealAsync(chainId, cancellationToken).ConfigureAwait(false);
        var chains = chainId is { } one ? [one] : await AdvancedChainsAsync(cancellationToken).ConfigureAwait(false);
        var created = new List<AuditCheckpoint>();
        foreach (var chain in chains)
        {
            AuditCheckpoint? checkpoint;
            try
            {
                checkpoint = await CheckpointChainAsync(chain, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (AuditChainBrokenException ex) when (chainId is null)
            {
                // Nothing is signed over a broken chain; the other chains still get their checkpoints.
                LogBroken(_logger, chain, ex);
                continue;
            }

            if (checkpoint is null)
            {
                continue;
            }

            created.Add(checkpoint);
            LogCheckpoint(_logger, chain, checkpoint.Sequence, checkpoint.KeyId, reason);
            if (reason != AuditCheckpointReason.Scheduled && audit is not null)
            {
                // Scheduled checkpoints are not audited: the event would advance the chain and call for the next one.
                await audit.WriteAsync(new AuditEvent
                {
                    WorkspaceId = AuditChainFormat.WorkspaceIdOf(chain),
                    OccurredAt = _time.GetUtcNow(),
                    Category = AuditTaxonomy.Audit.Category,
                    Action = AuditTaxonomy.Audit.CheckpointCreated,
                    ActorType = AuditActorType.Service,
                    ActorId = ActorId,
                    ActorDisplay = "Audit sealer",
                    ResourceType = chain == AuditChainFormat.SystemChainId ? "Installation" : "Workspace",
                    ResourceId = AuditChainFormat.WorkspaceIdOf(chain)?.ToString(),
                    Outcome = AuditOutcome.Success,
                    Details = new Dictionary<string, string?>
                    {
                        ["Reason"] = reason.ToString(),
                        ["Sequence"] = checkpoint.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["EventHash"] = AuditChainFormat.Hex(checkpoint.Data.EventHash),
                        ["MerkleRoot"] = AuditChainFormat.Hex(checkpoint.Data.MerkleRoot),
                        ["KeyId"] = checkpoint.KeyId,
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return created;
    }

    private async Task<List<Guid>> UnsealedChainsAsync(CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(_dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT DISTINCT coalesce(workspace_id, '00000000-0000-0000-0000-000000000000'::uuid) FROM audit.audit_event WHERE sequence IS NULL");
        var chains = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chains.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return chains;
    }

    private async Task<List<Guid>> AdvancedChainsAsync(CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(_dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT h.chain_id FROM audit.chain_head h
            WHERE h.last_sequence > coalesce((SELECT max(c.sequence) FROM audit.checkpoint c WHERE c.chain_id = h.chain_id), 0)
            """);
        var chains = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chains.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return chains;
    }

    /// <returns>The events sealed, and false when another sealer holds the chain.</returns>
    private async Task<(int Count, bool Locked)> SealBatchAsync(Guid chain, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(_dataSource, cancellationToken).ConfigureAwait(false);
        if (!await TryLockChainAsync(tx, chain, cancellationToken).ConfigureAwait(false))
        {
            return (0, false);
        }

        var (lastSequence, lastHash) = await LockHeadAsync(tx, chain, cancellationToken).ConfigureAwait(false);

        var rows = new List<StoredChainRow>(Math.Min(_options.BatchSize, 1024));
        await using (var select = tx.Command(
            $"""
            SELECT {AuditChainSql.EnvelopeColumns} FROM audit.audit_event
            WHERE {AuditChainSql.ChainPredicate(chain)} AND sequence IS NULL
            ORDER BY recorded_at, event_id
            LIMIT @limit
            """))
        {
            AuditChainSql.AddChain(select, chain);
            select.Parameters.AddWithValue("limit", _options.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(AuditChainSql.Read(reader));
            }
        }

        if (rows.Count == 0)
        {
            return (0, true);
        }

        var ids = new Guid[rows.Count];
        var occurred = new DateTime[rows.Count];
        var sequences = new long[rows.Count];
        var prevHashes = new byte[rows.Count][];
        var hashes = new byte[rows.Count][];
        var prev = lastHash;
        for (var i = 0; i < rows.Count; i++)
        {
            var envelope = rows[i].Envelope with { Sequence = lastSequence + i + 1, PrevHash = prev };
            var hash = AuditChainFormat.EventHash(envelope);
            ids[i] = envelope.EventId;
            occurred[i] = envelope.OccurredAt;
            sequences[i] = envelope.Sequence;
            prevHashes[i] = prev;
            hashes[i] = hash;
            prev = hash;
        }

        await using (var update = tx.Command(
            """
            UPDATE audit.audit_event e
            SET sequence = v.seq, prev_hash = v.prev, event_hash = v.hash, sealed_at = clock_timestamp()
            FROM unnest(@ids, @occurred, @seqs, @prevs, @hashes) AS v(id, occurred, seq, prev, hash)
            WHERE e.event_id = v.id AND e.occurred_at = v.occurred AND e.sequence IS NULL
            """))
        {
            update.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            update.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = occurred });
            update.Parameters.Add(new NpgsqlParameter("seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = sequences });
            update.Parameters.Add(new NpgsqlParameter("prevs", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = prevHashes });
            update.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = hashes });
            var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (updated != rows.Count)
            {
                throw new InvalidOperationException(
                    $"Sealing chain {AuditChainFormat.DescribeChain(chain)} updated {updated} of {rows.Count} events; another writer interfered.");
            }
        }

        await using (var head = tx.Command(
            "UPDATE audit.chain_head SET last_sequence = @seq, last_hash = @hash, sealed_through = clock_timestamp() WHERE chain_id = @chain"))
        {
            head.Parameters.AddWithValue("seq", sequences[^1]);
            head.Parameters.AddWithValue("hash", hashes[^1]);
            head.Parameters.AddWithValue("chain", chain);
            await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (rows.Count, true);
    }

    private async Task<AuditCheckpoint?> CheckpointChainAsync(Guid chain, AuditCheckpointReason reason, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(_dataSource, cancellationToken).ConfigureAwait(false);
        var (lastSequence, lastHash) = await LockHeadAsync(tx, chain, cancellationToken).ConfigureAwait(false);
        long from;
        await using (var latest = tx.Command("SELECT coalesce(max(sequence), 0) FROM audit.checkpoint WHERE chain_id = @chain"))
        {
            latest.Parameters.AddWithValue("chain", chain);
            from = (long)(await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (lastSequence <= from)
        {
            return null;
        }

        // Re-read the range: a checkpoint is never signed over a chain that is no longer contiguous.
        var tree = new AuditMerkleTree();
        var expected = from + 1;
        byte[]? tail = null;
        await using (var range = tx.Command(
            $"""
            SELECT sequence, event_hash FROM audit.audit_event
            WHERE {AuditChainSql.ChainPredicate(chain)} AND sequence > @from AND sequence <= @to
            ORDER BY sequence
            """))
        {
            AuditChainSql.AddChain(range, chain);
            range.Parameters.AddWithValue("from", from);
            range.Parameters.AddWithValue("to", lastSequence);
            await using var reader = await range.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sequence = reader.GetInt64(0);
                if (sequence != expected)
                {
                    throw new AuditChainBrokenException(chain, $"expected sequence {expected}, found {sequence}");
                }

                tail = reader.GetFieldValue<byte[]>(1);
                tree.Add(tail);
                expected++;
            }
        }

        if (expected != lastSequence + 1 || tail is null || !tail.AsSpan().SequenceEqual(lastHash))
        {
            throw new AuditChainBrokenException(chain, $"the events up to sequence {lastSequence} do not end in the head hash");
        }

        var data = new AuditCheckpointData(
            chain, from, lastSequence, lastHash, tree.Root(), reason, AuditChainFormat.ToMicroseconds(_time.GetUtcNow().UtcDateTime));
        var signature = await signer.SignAsync(SigningKeyPurposes.AuditCheckpoint, AuditChainFormat.CheckpointPayload(data), cancellationToken)
            .ConfigureAwait(false);
        var checkpoint = new AuditCheckpoint(data, signature.KeyId, signature.Algorithm, signature.Value);

        await using (var insert = tx.Command(
            """
            INSERT INTO audit.checkpoint (chain_id, sequence, from_sequence, event_hash, merkle_root, event_count, reason,
                                          created_at, key_id, algorithm, signature)
            VALUES (@chain, @seq, @from, @hash, @root, @count, @reason, @created, @key, @alg, @sig)
            """))
        {
            var p = insert.Parameters;
            p.AddWithValue("chain", chain);
            p.AddWithValue("seq", data.Sequence);
            p.AddWithValue("from", data.FromSequence);
            p.AddWithValue("hash", data.EventHash);
            p.AddWithValue("root", data.MerkleRoot);
            p.AddWithValue("count", data.EventCount);
            p.AddWithValue("reason", reason.ToString());
            p.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = data.CreatedAt });
            p.AddWithValue("key", checkpoint.KeyId);
            p.AddWithValue("alg", checkpoint.Algorithm);
            p.AddWithValue("sig", checkpoint.Signature);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var head = tx.Command("UPDATE audit.chain_head SET last_checkpoint_sequence = @seq WHERE chain_id = @chain"))
        {
            head.Parameters.AddWithValue("seq", data.Sequence);
            head.Parameters.AddWithValue("chain", chain);
            await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static async Task<bool> TryLockChainAsync(WorkspaceTransaction tx, Guid chain, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT pg_try_advisory_xact_lock(hashtextextended('audit.chain:' || @chain::text, 0))");
        command.Parameters.AddWithValue("chain", chain);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Creates the head on first use and locks it for this transaction.</summary>
    private static async Task<(long Sequence, byte[] Hash)> LockHeadAsync(WorkspaceTransaction tx, Guid chain, CancellationToken cancellationToken)
    {
        await using (var ensure = tx.Command("INSERT INTO audit.chain_head (chain_id) VALUES (@chain) ON CONFLICT (chain_id) DO NOTHING"))
        {
            ensure.Parameters.AddWithValue("chain", chain);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = tx.Command("SELECT last_sequence, last_hash FROM audit.chain_head WHERE chain_id = @chain FOR UPDATE");
        command.Parameters.AddWithValue("chain", chain);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sealing audit chain {Chain} (nil = system chain) failed; retrying next round")]
    private static partial void LogSealFailed(ILogger logger, Guid chain, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit chain {Chain} (nil = system chain) is broken; no checkpoint was signed over it. Run `audit verify` (docs/operations/audit-chain.md)")]
    private static partial void LogBroken(ILogger logger, Guid chain, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain {Chain} (nil = system chain) checkpointed at sequence {Sequence} with {KeyId} ({Reason})")]
    private static partial void LogCheckpoint(ILogger logger, Guid chain, long sequence, string keyId, AuditCheckpointReason reason);
}
