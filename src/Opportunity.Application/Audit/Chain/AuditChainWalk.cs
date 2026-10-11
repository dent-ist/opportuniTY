using System.Globalization;
using System.Security.Cryptography;

namespace Opportunity.Application.Audit.Chain;

/// <summary>
/// The streaming verification walk over one chain's sealed events in sequence order (E14-T03), shared by
/// <c>audit verify</c> (the whole chain from the database) and audit exports (E14-T06: a checkpoint-aligned range of the
/// chain, verified when generated and again offline by <c>audit verify-export</c>). It recomputes every event hash,
/// follows the PrevHash links, accounts for every missing position (deleted, or removed by the retention purge and
/// listed in <see cref="Gaps"/>), and checks every checkpoint's head hash, Merkle root, continuity and ES256 signature.
/// </summary>
/// <param name="chainId">The workspace id, or <see cref="AuditChainFormat.SystemChainId"/>.</param>
/// <param name="firstSequence">The first position walked: 1 for a whole chain, or the position after an anchor checkpoint.</param>
/// <param name="anchorHash">The hash of the event before <paramref name="firstSequence"/> (genesis for a whole chain).</param>
public sealed class AuditChainWalk
{
    private readonly List<AuditChainIssue> _issues = [];
    private byte[]? _prev;
    private long _expected;
    private int _checkpoint;
    private AuditMerkleTree _tree = new();
    private bool _complete = true;

    public AuditChainWalk(Guid chainId, long firstSequence = 1, byte[]? anchorHash = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSequence, 1);
        ChainId = chainId;
        FirstSequence = firstSequence;
        _expected = firstSequence;
        _prev = anchorHash ?? AuditChainFormat.Genesis;
    }

    public Guid ChainId { get; }

    public long FirstSequence { get; }

    /// <summary>Purge records of the chain, ordered by first sequence.</summary>
    public List<AuditChainGap> Gaps { get; } = [];

    /// <summary>The checkpoints that end at or after <see cref="FirstSequence"/>, in sequence order.</summary>
    public List<AuditCheckpoint> Checkpoints { get; } = [];

    public IReadOnlyList<AuditChainIssue> Issues => _issues;

    public long IssueCount { get; private set; }

    public long Events { get; private set; }

    public long LastSequence { get; private set; }

    public byte[]? LastHash { get; private set; }

    public long Purged { get; private set; }

    /// <summary>Events whose content was withheld from an export (their hash is checked only through the links and Merkle roots).</summary>
    public long Withheld { get; private set; }

    /// <summary>Signatures that could not be checked because no public key was available and that was not treated as an issue.</summary>
    public int UncheckedSignatures { get; private set; }

    public void Issue(AuditChainIssueKind kind, long? sequence, string message)
    {
        IssueCount++;
        if (_issues.Count < AuditChainVerification.MaxIssues)
        {
            _issues.Add(new AuditChainIssue(kind, sequence, message));
        }
    }

    /// <summary>
    /// Checks checkpoint continuity from <see cref="FirstSequence"/> and every signature. A missing key is an issue
    /// (<see cref="AuditChainIssueKind.CheckpointKeyUnavailable"/>) unless <paramref name="missingKeyIsIssue"/> is false,
    /// in which case it is only counted in <see cref="UncheckedSignatures"/>.
    /// </summary>
    public async Task VerifySignaturesAsync(IAuditCheckpointKeys keys, bool missingKeyIsIssue = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var cache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var previous = FirstSequence - 1;
        foreach (var checkpoint in Checkpoints)
        {
            if (checkpoint.Data.FromSequence != previous)
            {
                Issue(AuditChainIssueKind.CheckpointMissing, checkpoint.Sequence,
                    $"checkpoint at {checkpoint.Sequence} starts after {checkpoint.Data.FromSequence}, but the previous checkpoint ends at {previous}");
            }

            previous = checkpoint.Sequence;
            await VerifySignatureAsync(checkpoint, keys, cache, missingKeyIsIssue, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks one checkpoint's signature (an export's anchor checkpoint, which ends before the walked range).</summary>
    public Task<bool> VerifySignatureAsync(AuditCheckpoint checkpoint, IAuditCheckpointKeys keys, bool missingKeyIsIssue = true,
        CancellationToken cancellationToken = default) =>
        VerifySignatureAsync(checkpoint, keys, new Dictionary<string, byte[]?>(StringComparer.Ordinal), missingKeyIsIssue, cancellationToken);

    /// <param name="contentWithheld">
    /// The envelope is incomplete (an export left out content the caller may not read): the stored hash is taken as given
    /// and checked only by the next event's link and the checkpoints' Merkle roots.
    /// </param>
    public void Visit(AuditChainEnvelope envelope, long? sequenceOrNull, byte[]? prevHash, byte[]? eventHash, bool contentWithheld = false)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var sequence = sequenceOrNull ?? throw new ArgumentException("Only sealed events are walked.", nameof(sequenceOrNull));
        if (sequence < _expected)
        {
            Issue(AuditChainIssueKind.DuplicateSequence, sequence, $"event {envelope.EventId} repeats chain position {sequence}");
            return;
        }

        if (sequence > _expected)
        {
            Missing(_expected, sequence - 1, tail: false);
        }

        if (prevHash is null || eventHash is null)
        {
            Issue(AuditChainIssueKind.EventModified, sequence, $"event {envelope.EventId} has a sequence but no hashes");
            _prev = null;
            _expected = sequence + 1;
            return;
        }

        // After an unexplained gap the predecessor is unknown; the gap itself is already reported.
        if (_prev is not null && !prevHash.AsSpan().SequenceEqual(_prev))
        {
            Issue(AuditChainIssueKind.ChainLinkBroken, sequence,
                $"PrevHash of {sequence} is not the hash of {sequence - 1}: events were reordered, replaced or rewritten");
        }

        if (contentWithheld)
        {
            Withheld++;
        }
        else
        {
            var recomputed = AuditChainFormat.EventHash(envelope with { Sequence = sequence, PrevHash = prevHash });
            if (!recomputed.AsSpan().SequenceEqual(eventHash))
            {
                Issue(AuditChainIssueKind.EventModified, sequence,
                    $"event {envelope.EventId} at {sequence} does not match its hash: its content or position was changed");
            }
        }

        _prev = eventHash;
        Present(sequence, eventHash);
        Events++;
        LastSequence = sequence;
        LastHash = eventHash;
        _expected = sequence + 1;
    }

    /// <summary>Accounts for positions after the last event that a checkpoint or the chain head says exist.</summary>
    public void Finish(long knownSequence)
    {
        if (knownSequence >= _expected)
        {
            Missing(_expected, knownSequence, tail: true);
        }
    }

    /// <summary>The report of the walk.</summary>
    public AuditChainVerification Result(long unsealed = 0, long beforeSealing = 0, DateTime? firstSealedAt = null) => new()
    {
        ChainId = ChainId,
        Events = Events,
        LastSequence = LastSequence,
        LastEventHash = LastHash,
        PurgedEvents = Purged,
        UnsealedEvents = unsealed,
        EventsRecordedBeforeSealing = beforeSealing,
        FirstSealedAt = firstSealedAt,
        Checkpoints = Checkpoints.Count,
        LastCheckpointSequence = Checkpoints.Count > 0 ? Checkpoints[^1].Sequence : 0,
        Issues = Issues,
        IssueCount = IssueCount,
    };

    private async Task<bool> VerifySignatureAsync(
        AuditCheckpoint checkpoint, IAuditCheckpointKeys keys, Dictionary<string, byte[]?> cache, bool missingKeyIsIssue,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(checkpoint.KeyId, out var key))
        {
            key = await keys.GetPublicKeyAsync(checkpoint.KeyId, cancellationToken).ConfigureAwait(false);
            cache[checkpoint.KeyId] = key;
        }

        if (key is null)
        {
            if (missingKeyIsIssue)
            {
                Issue(AuditChainIssueKind.CheckpointKeyUnavailable, checkpoint.Sequence, $"no public key for {checkpoint.KeyId}");
            }
            else
            {
                UncheckedSignatures++;
            }

            return false;
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
            Issue(AuditChainIssueKind.CheckpointSignatureInvalid, checkpoint.Sequence,
                $"the {checkpoint.Algorithm} signature of checkpoint {checkpoint.Sequence} does not verify with {checkpoint.KeyId}");
        }

        return valid;
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

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{AuditChainFormat.DescribeChain(ChainId)} @ {LastSequence}");
}
