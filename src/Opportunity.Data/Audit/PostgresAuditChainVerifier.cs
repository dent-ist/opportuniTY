using System.Data;
using System.Runtime.CompilerServices;

using Opportunity.Application.Audit.Chain;

namespace Opportunity.Data.Audit;

/// <summary>
/// Verifies audit hash chains straight from the database (<c>audit verify</c>, E14-T03): it recomputes every sealed
/// event's hash, follows the PrevHash links in sequence order, accounts for every missing position (deleted, or removed
/// by the retention purge and recorded in <c>audit.chain_gap</c>), and checks every checkpoint's head hash, Merkle root,
/// continuity and ES256 signature with the given public keys. It trusts nothing it could not recompute: chain heads are
/// used only to notice a deleted tail. One chain is read in one REPEATABLE READ snapshot, streamed in sequence order.
/// </summary>
public sealed class PostgresAuditChainVerifier(AuditChainDataSource chainDataSource)
{
    /// <summary>Every chain that has events, a head or a checkpoint (the system chain is the nil uuid).</summary>
    public async Task<IReadOnlyList<Guid>> ListChainsAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(chainDataSource.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT chain_id FROM audit.chain_head
            UNION SELECT chain_id FROM audit.checkpoint
            UNION SELECT DISTINCT coalesce(workspace_id, '00000000-0000-0000-0000-000000000000'::uuid) FROM audit.audit_event
            ORDER BY 1
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

    /// <summary>The chain's checkpoints in sequence order (for exhibits: <c>audit checkpoints</c>).</summary>
    public async IAsyncEnumerable<AuditCheckpoint> CheckpointsAsync(Guid chainId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(chainDataSource.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {AuditChainSql.CheckpointColumns} FROM audit.checkpoint WHERE chain_id = @chain ORDER BY sequence");
        command.Parameters.AddWithValue("chain", chainId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return AuditChainSql.ReadCheckpoint(reader);
        }
    }

    public async Task<AuditChainVerification> VerifyAsync(Guid chainId, IAuditCheckpointKeys keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(chainDataSource.DataSource, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var walk = new AuditChainWalk(chainId);

        DateTime? firstSealedAt;
        long unsealed;
        await using (var summary = tx.Command(
            $"SELECT min(sealed_at), count(*) FILTER (WHERE sequence IS NULL) FROM audit.audit_event WHERE {AuditChainSql.ChainPredicate(chainId)}"))
        {
            AuditChainSql.AddChain(summary, chainId);
            await using var reader = await summary.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            firstSealedAt = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            unsealed = reader.GetInt64(1);
        }

        long headSequence = 0;
        await using (var head = tx.Command("SELECT last_sequence FROM audit.chain_head WHERE chain_id = @chain"))
        {
            head.Parameters.AddWithValue("chain", chainId);
            if (await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value)
            {
                headSequence = value;
            }
        }

        await using (var gaps = tx.Command(
            "SELECT chain_id, first_sequence, last_sequence, last_event_hash, partition_name FROM audit.chain_gap WHERE chain_id = @chain ORDER BY first_sequence"))
        {
            gaps.Parameters.AddWithValue("chain", chainId);
            await using var reader = await gaps.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                walk.Gaps.Add(new AuditChainGap(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetString(4)));
            }
        }

        await using (var checkpoints = tx.Command($"SELECT {AuditChainSql.CheckpointColumns} FROM audit.checkpoint WHERE chain_id = @chain ORDER BY sequence"))
        {
            checkpoints.Parameters.AddWithValue("chain", chainId);
            await using var reader = await checkpoints.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                walk.Checkpoints.Add(AuditChainSql.ReadCheckpoint(reader));
            }
        }

        await VerifyPurgeRecordsAsync(tx, walk, cancellationToken).ConfigureAwait(false);
        await walk.VerifySignaturesAsync(keys, cancellationToken: cancellationToken).ConfigureAwait(false);

        long beforeSealing = 0;
        await using (var events = tx.Command(
            $"""
            SELECT {AuditChainSql.EnvelopeColumns} FROM audit.audit_event
            WHERE {AuditChainSql.ChainPredicate(chainId)} AND sequence IS NOT NULL
            ORDER BY sequence, event_id
            """))
        {
            AuditChainSql.AddChain(events, chainId);
            await using var reader = await events.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = AuditChainSql.Read(reader);
                walk.Visit(row.Envelope, row.Sequence, row.PrevHash, row.EventHash);
                if (firstSealedAt is { } first && row.Envelope.RecordedAt < first)
                {
                    beforeSealing++;
                }
            }
        }

        var known = Math.Max(headSequence, walk.Checkpoints.Count > 0 ? walk.Checkpoints[^1].Sequence : 0);
        walk.Finish(known);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return walk.Result(unsealed, beforeSealing, firstSealedAt);
    }

    /// <summary>Each partition the chain lost to the purge must match the gap digest of its signed <c>Audit.Purged</c> event.</summary>
    private static async Task VerifyPurgeRecordsAsync(WorkspaceTransaction tx, AuditChainWalk walk, CancellationToken cancellationToken)
    {
        foreach (var partition in walk.Gaps.Select(g => g.Partition).Distinct(StringComparer.Ordinal).ToList())
        {
            var all = new List<AuditChainGap>();
            await using (var gaps = tx.Command(
                "SELECT chain_id, first_sequence, last_sequence, last_event_hash, partition_name FROM audit.chain_gap WHERE partition_name = @p"))
            {
                gaps.Parameters.AddWithValue("p", partition);
                await using var reader = await gaps.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    all.Add(new AuditChainGap(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetString(4)));
                }
            }

            var digests = new List<string>();
            await using (var purged = tx.Command(
                """
                SELECT details->>'GapDigest' FROM audit.audit_event
                WHERE workspace_id IS NULL AND category = 'Audit' AND action = 'Purged' AND resource_type = 'AuditPartition' AND resource_id = @p
                """))
            {
                purged.Parameters.AddWithValue("p", partition);
                await using var reader = await purged.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    digests.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                }
            }

            var expected = AuditChainFormat.GapDigest(all);
            if (!digests.Contains(expected, StringComparer.Ordinal))
            {
                walk.Issue(AuditChainIssueKind.PurgeRecordUnverified, null,
                    $"the purge records of {partition} do not match the GapDigest of an Audit.Purged event on the system chain");
                walk.Gaps.RemoveAll(g => g.Partition == partition);
            }
        }
    }
}
