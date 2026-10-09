using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

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
        var walk = new ChainWalk(chainId);

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
        await VerifySignaturesAsync(walk, keys, cancellationToken).ConfigureAwait(false);

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
                walk.Visit(row);
                if (firstSealedAt is { } first && row.Envelope.RecordedAt < first)
                {
                    beforeSealing++;
                }
            }
        }

        var known = Math.Max(headSequence, walk.Checkpoints.Count > 0 ? walk.Checkpoints[^1].Sequence : 0);
        walk.Finish(known);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AuditChainVerification
        {
            ChainId = chainId,
            Events = walk.Events,
            LastSequence = walk.LastSequence,
            LastEventHash = walk.LastHash,
            PurgedEvents = walk.Purged,
            UnsealedEvents = unsealed,
            EventsRecordedBeforeSealing = beforeSealing,
            FirstSealedAt = firstSealedAt,
            Checkpoints = walk.Checkpoints.Count,
            LastCheckpointSequence = walk.Checkpoints.Count > 0 ? walk.Checkpoints[^1].Sequence : 0,
            Issues = walk.Issues,
            IssueCount = walk.IssueCount,
        };
    }

    /// <summary>Each partition the chain lost to the purge must match the gap digest of its signed <c>Audit.Purged</c> event.</summary>
    private static async Task VerifyPurgeRecordsAsync(WorkspaceTransaction tx, ChainWalk walk, CancellationToken cancellationToken)
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

    private static async Task VerifySignaturesAsync(ChainWalk walk, IAuditCheckpointKeys keys, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        long previous = 0;
        foreach (var checkpoint in walk.Checkpoints)
        {
            if (checkpoint.Data.FromSequence != previous)
            {
                walk.Issue(AuditChainIssueKind.CheckpointMissing, checkpoint.Sequence,
                    $"checkpoint at {checkpoint.Sequence} starts after {checkpoint.Data.FromSequence}, but the previous checkpoint ends at {previous}");
            }

            previous = checkpoint.Sequence;
            if (!cache.TryGetValue(checkpoint.KeyId, out var key))
            {
                key = await keys.GetPublicKeyAsync(checkpoint.KeyId, cancellationToken).ConfigureAwait(false);
                cache[checkpoint.KeyId] = key;
            }

            if (key is null)
            {
                walk.Issue(AuditChainIssueKind.CheckpointKeyUnavailable, checkpoint.Sequence, $"no public key for {checkpoint.KeyId}");
                continue;
            }

            bool valid;
            try
            {
                valid = AuditCheckpointSignatures.Verify(checkpoint, key);
            }
            catch (CryptographicException)
            {
                valid = false;
            }

            if (!valid)
            {
                walk.Issue(AuditChainIssueKind.CheckpointSignatureInvalid, checkpoint.Sequence,
                    $"the {checkpoint.Algorithm} signature of checkpoint {checkpoint.Sequence} does not verify with {checkpoint.KeyId}");
            }
        }
    }

    /// <summary>The streaming walk over one chain's sealed rows in sequence order.</summary>
    private sealed class ChainWalk(Guid chainId)
    {
        private readonly List<AuditChainIssue> _issues = [];
        private byte[]? _prev = AuditChainFormat.Genesis;
        private long _expected = 1;
        private int _checkpoint;
        private AuditMerkleTree _tree = new();
        private bool _complete = true;

        public List<AuditChainGap> Gaps { get; } = [];

        public List<AuditCheckpoint> Checkpoints { get; } = [];

        public IReadOnlyList<AuditChainIssue> Issues => _issues;

        public long IssueCount { get; private set; }

        public long Events { get; private set; }

        public long LastSequence { get; private set; }

        public byte[]? LastHash { get; private set; }

        public long Purged { get; private set; }

        public void Issue(AuditChainIssueKind kind, long? sequence, string message)
        {
            IssueCount++;
            if (_issues.Count < AuditChainVerification.MaxIssues)
            {
                _issues.Add(new AuditChainIssue(kind, sequence, message));
            }
        }

        public void Visit(StoredChainRow row)
        {
            var sequence = row.Sequence!.Value;
            if (sequence < _expected)
            {
                Issue(AuditChainIssueKind.DuplicateSequence, sequence, $"event {row.Envelope.EventId} repeats chain position {sequence}");
                return;
            }

            if (sequence > _expected)
            {
                Missing(_expected, sequence - 1, tail: false);
            }

            if (row.PrevHash is null || row.EventHash is null)
            {
                Issue(AuditChainIssueKind.EventModified, sequence, $"event {row.Envelope.EventId} has a sequence but no hashes");
                _prev = null;
                _expected = sequence + 1;
                return;
            }
            else
            {
                // After an unexplained gap the predecessor is unknown; the gap itself is already reported.
                if (_prev is not null && !row.PrevHash.AsSpan().SequenceEqual(_prev))
                {
                    Issue(AuditChainIssueKind.ChainLinkBroken, sequence,
                        $"PrevHash of {sequence} is not the hash of {sequence - 1}: events were reordered, replaced or rewritten");
                }

                var recomputed = AuditChainFormat.EventHash(row.Envelope with { Sequence = sequence, PrevHash = row.PrevHash });
                if (!recomputed.AsSpan().SequenceEqual(row.EventHash))
                {
                    Issue(AuditChainIssueKind.EventModified, sequence,
                        $"event {row.Envelope.EventId} at {sequence} does not match its hash: its content or position was changed");
                }

                _prev = row.EventHash;
            }

            Present(sequence, row.EventHash);
            Events++;
            LastSequence = sequence;
            LastHash = row.EventHash;
            _expected = sequence + 1;
        }

        /// <summary>Accounts for positions after the last row that a checkpoint or the head says exist.</summary>
        public void Finish(long knownSequence)
        {
            if (knownSequence >= _expected)
            {
                Missing(_expected, knownSequence, tail: true);
            }
        }

        private void Present(long sequence, byte[] hash)
        {
            if (_checkpoint >= Checkpoints.Count)
            {
                return;
            }

            var checkpoint = Checkpoints[_checkpoint];
            if (sequence > checkpoint.Data.FromSequence)
            {
                _tree.Add(hash);
            }

            if (sequence == checkpoint.Sequence)
            {
                if (!hash.AsSpan().SequenceEqual(checkpoint.Data.EventHash))
                {
                    Issue(AuditChainIssueKind.CheckpointMismatch, sequence,
                        $"checkpoint {sequence} signed a different head hash: the chain up to {sequence} was rewritten");
                }
                else if (_complete && !_tree.Root().AsSpan().SequenceEqual(checkpoint.Data.MerkleRoot))
                {
                    Issue(AuditChainIssueKind.CheckpointMismatch, sequence,
                        $"the Merkle root of events {checkpoint.Data.FromSequence + 1}..{sequence} differs from the signed checkpoint");
                }

                NextCheckpoint();
            }
        }

        private void Missing(long first, long last, bool tail)
        {
            var covered = Covered(first, last);
            if (covered)
            {
                Purged += last - first + 1;
                _prev = Gaps.First(g => g.LastSequence == last).LastEventHash;
            }
            else
            {
                Issue(tail ? AuditChainIssueKind.TailMissing : AuditChainIssueKind.EventsMissing, first,
                    first == last
                        ? $"event {first} is missing and no purge record covers it: it was deleted"
                        : $"events {first}..{last} are missing and no purge record covers them: they were deleted");
                _prev = null;
            }

            // Checkpoints that end inside the missing run: their head can only be compared with a purge record.
            while (_checkpoint < Checkpoints.Count && Checkpoints[_checkpoint].Sequence <= last)
            {
                var checkpoint = Checkpoints[_checkpoint];
                if (checkpoint.Sequence >= first && covered
                    && Gaps.FirstOrDefault(g => g.LastSequence == checkpoint.Sequence) is { } gap
                    && !gap.LastEventHash.AsSpan().SequenceEqual(checkpoint.Data.EventHash))
                {
                    Issue(AuditChainIssueKind.CheckpointMismatch, checkpoint.Sequence,
                        $"checkpoint {checkpoint.Sequence} signed a different head hash than the purge recorded");
                }

                NextCheckpoint();
            }

            if (_checkpoint < Checkpoints.Count && last > Checkpoints[_checkpoint].Data.FromSequence)
            {
                _complete = false;
            }
        }

        private void NextCheckpoint()
        {
            _checkpoint++;
            _tree = new AuditMerkleTree();
            _complete = true;
        }

        private bool Covered(long first, long last)
        {
            var next = first;
            foreach (var gap in Gaps)
            {
                if (gap.FirstSequence <= next && gap.LastSequence >= next)
                {
                    next = gap.LastSequence + 1;
                }

                if (next > last)
                {
                    return Gaps.Any(g => g.LastSequence == last);
                }
            }

            return false;
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{AuditChainFormat.DescribeChain(chainId)} @ {LastSequence}");
    }
}
