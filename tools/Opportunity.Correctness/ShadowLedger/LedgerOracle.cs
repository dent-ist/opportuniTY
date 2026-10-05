using System.Diagnostics;

using Npgsql;

namespace Opportunity.Correctness.ShadowLedger;

public sealed class ShadowLedgerOptions
{
    public required Guid WorkspaceId { get; init; }

    /// <summary>The index or alias holding the workspace's projection, with its shared-index routing.</summary>
    public required LedgerIndexTarget Index { get; init; }

    /// <summary>ADR-004 candidate whose layout is judged (<see cref="ProjectionLayouts"/>); default the interim Candidate A.</summary>
    public string? Candidate { get; init; }

    public LedgerScope Scope { get; init; } = LedgerScope.Touched;

    /// <summary>Changes committed from here on are "touched"; default: the database clock when the ledger starts.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Documents per reconciliation batch (one PostgreSQL snapshot and one <c>_mget</c> each).</summary>
    public int BatchSize { get; init; } = 1_000;

    /// <summary>Documents followed by continuous sampling (reservoir over the captured changes; bounds memory).</summary>
    public int MaxTrackedDocuments { get; init; } = 10_000;

    /// <summary>Documents per sampling pass.</summary>
    public int SampleBatch { get; init; } = 250;

    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int MaxFindings { get; init; } = 100;

    /// <summary>
    /// Cumulative count of OpenSearch version-conflict no-ops (<c>opportunity.search.stale_version_rejections</c>), read
    /// at start and at the verdict; null when the run cannot observe it.
    /// </summary>
    public Func<long>? VersionConflictRejections { get; init; }

    /// <summary>Recorded in the verdict; also seeds the sampling reservoir.</summary>
    public long? Seed { get; init; }

    /// <summary>Documents changed outside coding (soft deletes, overlays) that the reconciliation must also visit.</summary>
    public IReadOnlyCollection<Guid> ExtraDocuments { get; init; } = [];
}

/// <summary>
/// The stale-version-overwrite shadow ledger (E17-T07, baseline §21/§26, ADR-001 §9): the mechanical oracle behind the
/// §26 gate "0 stale-version overwrites".
/// <list type="number">
/// <item><b>Capture.</b> Committed <c>(documentId, DocumentVersion)</c> pairs are tailed from <c>CodingEvent</c>
/// (see <see cref="PostgresLedgerReader"/> for why not logical decoding); a seeded reservoir of touched documents is
/// tracked.</item>
/// <item><b>Continuous sampling</b> while the run is live: each pass reads tracked documents from OpenSearch (real-time
/// <c>_mget</c>) and <em>then</em> their current versions from PostgreSQL, and asserts
/// <c>os.projectionVersion ≤ pg.DocumentVersion</c>, <c>_version == projectionVersion</c> and that no document's indexed
/// version ever decreases (or vanishes while live).</item>
/// <item><b>Full reconciliation</b> after quiescence, streamed in keyset batches with bounded memory: for 100% of the
/// touched documents, <c>projectionVersion == DocumentVersion</c> and every projected coding value equals PostgreSQL;
/// deleted documents are absent.</item>
/// </list>
/// Counters: <c>staleOverwrites</c> (distinct documents whose index ever held an older state than one already indexed,
/// or than PostgreSQL after quiescence, or a version PostgreSQL never committed, or a write that bypassed external
/// versioning, or a resurrected delete), <c>versionRegressions</c> (observations where a document's indexed version went
/// down), <c>missingDocs</c>, <c>valueMismatches</c> and <c>versionConflictRejections</c> (409 no-ops, informational).
/// </summary>
public sealed class LedgerOracle : IAsyncDisposable
{
    private static readonly TimeSpan CaptureOverlap = TimeSpan.FromSeconds(30);

    private readonly PostgresLedgerReader _postgres;
    private readonly OpenSearchProjectionReader _index;
    private readonly IProjectionLayout _layout;
    private readonly ShadowLedgerOptions _options;
    private readonly Random _random;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Tracked> _tracked = [];
    private readonly List<Guid> _trackedOrder = [];
    private readonly Dictionary<Guid, DateTimeOffset> _recentEvents = [];
    private readonly HashSet<Guid> _staleDocuments = [];
    private readonly List<LedgerFinding> _findings = [];
    private readonly DateTimeOffset _since;
    private readonly DateTimeOffset _startedUtc;
    private readonly long _rejectionsAtStart;
    private DateTimeOffset _cursor;
    private long _documentsSeen;
    private long _eventsCaptured;
    private long _passes;
    private long _observations;
    private long _regressions;
    private int _nextSample;
    private CancellationTokenSource? _sampling;
    private Task? _samplingLoop;
    private Exception? _samplingError;

    private LedgerOracle(
        PostgresLedgerReader postgres, OpenSearchProjectionReader index, IProjectionLayout layout, ShadowLedgerOptions options, DateTimeOffset since)
    {
        _postgres = postgres;
        _index = index;
        _layout = layout;
        _options = options;
        _since = since;
        _cursor = since;
        _startedUtc = DateTimeOffset.UtcNow;
        _random = new Random(unchecked((int)(options.Seed ?? 0x5eed)));
        _rejectionsAtStart = options.VersionConflictRejections?.Invoke() ?? 0;
    }

    public DateTimeOffset Since => _since;

    /// <summary>Starts a ledger for one workspace; nothing is read from OpenSearch until the first sample.</summary>
    public static async Task<LedgerOracle> StartAsync(
        NpgsqlDataSource postgres, HttpClient openSearch, ShadowLedgerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(openSearch);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BatchSize is < 1 or > 10_000 || options.SampleBatch < 1 || options.MaxTrackedDocuments < 1)
        {
            throw new ArgumentException("BatchSize must be 1–10,000; SampleBatch and MaxTrackedDocuments at least 1.", nameof(options));
        }

        var layout = ProjectionLayouts.For(options.Candidate);
        var reader = new PostgresLedgerReader(postgres, options.WorkspaceId);
        var since = options.Since ?? (await reader.NowAsync(cancellationToken).ConfigureAwait(false)).AddSeconds(-1);
        return new LedgerOracle(reader, new OpenSearchProjectionReader(openSearch, options.Index, layout), layout, options, since);
    }

    /// <summary>Samples in the background every <see cref="ShadowLedgerOptions.SampleInterval"/> until the verdict.</summary>
    public void StartSampling()
    {
        if (_samplingLoop is not null)
        {
            return;
        }

        _sampling = new CancellationTokenSource();
        var token = _sampling.Token;
        _samplingLoop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SampleOnceAsync(token).ConfigureAwait(false);
                    await Task.Delay(_options.SampleInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A sample that cannot read (a store is partitioned by the fault under test) is skipped, not fatal.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _samplingError = ex;
                    try
                    {
                        await Task.Delay(_options.SampleInterval * 5, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, CancellationToken.None);
    }

    public async Task StopSamplingAsync()
    {
        if (_sampling is null || _samplingLoop is null)
        {
            return;
        }

        await _sampling.CancelAsync().ConfigureAwait(false);
        await _samplingLoop.ConfigureAwait(false);
        _sampling.Dispose();
        _sampling = null;
        _samplingLoop = null;
    }

    /// <summary>The last error a background sample swallowed (diagnostics only).</summary>
    public Exception? LastSamplingError => _samplingError;

    /// <summary>Tails newly committed changes into the ledger.</summary>
    public async Task CaptureAsync(CancellationToken cancellationToken = default)
    {
        const int Page = 5_000;
        var from = _cursor - CaptureOverlap;
        CommittedChange? after = null;
        while (true)
        {
            var page = await _postgres.ChangesSinceAsync(from, after, Page, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var change in page)
                {
                    if (!_recentEvents.TryAdd(change.EventId, change.OccurredAt))
                    {
                        continue;
                    }

                    _eventsCaptured++;
                    Track(change);
                    if (change.OccurredAt > _cursor)
                    {
                        _cursor = change.OccurredAt;
                    }
                }
            }

            if (page.Count < Page)
            {
                break;
            }

            after = page[^1];
        }

        lock (_gate)
        {
            var horizon = _cursor - CaptureOverlap - CaptureOverlap;
            foreach (var old in _recentEvents.Where(e => e.Value < horizon).Select(e => e.Key).ToList())
            {
                _recentEvents.Remove(old);
            }
        }
    }

    /// <summary>One capture plus one sampling pass over the next tracked documents; returns the documents observed.</summary>
    public async Task<int> SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> batch;
        lock (_gate)
        {
            if (_trackedOrder.Count == 0)
            {
                return 0;
            }

            var take = Math.Min(_options.SampleBatch, _trackedOrder.Count);
            batch = new List<Guid>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(_trackedOrder[(_nextSample + i) % _trackedOrder.Count]);
            }

            _nextSample = (_nextSample + take) % _trackedOrder.Count;
        }

        // Index first, PostgreSQL second: the authoritative version can only have grown in between, so os ≤ pg must hold.
        var indexed = await _index.GetAsync(batch, cancellationToken).ConfigureAwait(false);
        var versions = await _postgres.VersionsAsync(batch, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _passes++;
            foreach (var id in batch)
            {
                _observations++;
                if (!_tracked.TryGetValue(id, out var tracked))
                {
                    continue; // Replaced in the reservoir meanwhile.
                }

                long? postgresVersion = versions.TryGetValue(id, out var pg) ? pg.Version : null;
                if (indexed.TryGetValue(id, out var doc))
                {
                    CheckInvariants(doc, postgresVersion);
                    Observe(tracked, id, doc.ProjectionVersion ?? doc.ExternalVersion, postgresVersion);
                }
                else if (tracked.SeenIndexed && postgresVersion is not null && !pg.Deleted)
                {
                    Regress(id, LedgerFindingKind.VersionRegression, postgresVersion, null, tracked.Last, "the document vanished from the index while live");
                    tracked.SeenIndexed = false;
                }
            }
        }

        return batch.Count;
    }

    /// <summary>
    /// Stops sampling and reconciles every touched document (or the whole workspace) against PostgreSQL, in keyset
    /// batches. Call after quiescence: every work record of the run is Applied or Failed.
    /// </summary>
    public async Task<ShadowLedgerVerdict> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await StopSamplingAsync().ConfigureAwait(false);
        await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var fields = await _postgres.CodingFieldsAsync(cancellationToken).ConfigureAwait(false);
        var projected = fields.Where(f => f.IsProjected).ToList();

        var clock = Stopwatch.StartNew();
        long documents = 0, batches = 0, missing = 0, mismatches = 0;
        var extras = new HashSet<Guid>(_options.ExtraDocuments);
        Guid? after = null;
        while (true)
        {
            var page = await _postgres.DocumentPageAsync(_options.Scope, _since, after, _options.BatchSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1];
            extras.ExceptWith(page);
            var (m, v) = await ReconcileBatchAsync(page, projected, cancellationToken).ConfigureAwait(false);
            missing += m;
            mismatches += v;
            documents += page.Count;
            batches++;
        }

        foreach (var chunk in extras.Order().Chunk(_options.BatchSize))
        {
            var (m, v) = await ReconcileBatchAsync(chunk, projected, cancellationToken).ConfigureAwait(false);
            missing += m;
            mismatches += v;
            documents += chunk.Length;
            batches++;
        }

        clock.Stop();
        lock (_gate)
        {
            var rejections = _options.VersionConflictRejections is { } count ? count() - _rejectionsAtStart : (long?)null;
            var stale = _staleDocuments.Count;
            var passed = stale == 0 && _regressions == 0 && missing == 0 && mismatches == 0;
            return new ShadowLedgerVerdict
            {
                WorkspaceId = _options.WorkspaceId,
                Candidate = _layout.Candidate,
                Seed = _options.Seed,
                StartedUtc = _startedUtc,
                EndedUtc = DateTimeOffset.UtcNow,
                ShadowLedger = new ShadowLedgerCounters
                {
                    Status = passed ? "passed" : "failed",
                    TouchedDocs = documents,
                    StaleOverwrites = stale,
                    VersionRegressions = _regressions,
                    MissingDocs = missing,
                    ValueMismatches = mismatches,
                    VersionConflictRejections = rejections,
                },
                Sampling = new LedgerSampling
                {
                    Passes = _passes,
                    Observations = _observations,
                    DocumentsTracked = _trackedOrder.Count,
                    CommittedEventsCaptured = _eventsCaptured,
                },
                Reconciliation = new LedgerReconciliation
                {
                    Documents = documents,
                    Batches = batches,
                    BatchSize = _options.BatchSize,
                    DurationMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                    DocumentsPerSecond = clock.Elapsed.TotalSeconds > 0 ? Math.Round(documents / clock.Elapsed.TotalSeconds, 1) : 0,
                },
                Findings = [.. _findings],
            };
        }
    }

    public async ValueTask DisposeAsync() => await StopSamplingAsync().ConfigureAwait(false);

    private async Task<(long Missing, long Mismatches)> ReconcileBatchAsync(
        IReadOnlyList<Guid> ids, IReadOnlyList<CodingFieldInfo> fields, CancellationToken cancellationToken)
    {
        var indexed = await _index.GetAsync(ids, cancellationToken).ConfigureAwait(false);
        var states = await _postgres.StatesAsync(ids, cancellationToken).ConfigureAwait(false);
        long missing = 0, mismatches = 0;
        lock (_gate)
        {
            foreach (var state in states)
            {
                indexed.TryGetValue(state.DocumentId, out var doc);
                if (state.Deleted)
                {
                    if (doc is not null)
                    {
                        Stale(state.DocumentId, LedgerFindingKind.Resurrected, state.Version, doc, "deleted in PostgreSQL, present in the index");
                    }

                    continue;
                }

                if (doc is null)
                {
                    missing++;
                    Find(new LedgerFinding(state.DocumentId, LedgerFindingKind.Missing, state.Version, null, null));
                    continue;
                }

                CheckInvariants(doc, state.Version);
                var version = doc.ProjectionVersion ?? doc.ExternalVersion;
                if (_tracked.TryGetValue(state.DocumentId, out var tracked))
                {
                    Observe(tracked, state.DocumentId, version, state.Version);
                }

                if (version < state.Version)
                {
                    Stale(state.DocumentId, LedgerFindingKind.StaleVersion, state.Version, doc, "older than PostgreSQL after quiescence");
                    continue;
                }

                if (version != state.Version)
                {
                    continue; // VersionAhead, already counted by CheckInvariants.
                }

                if (Mismatch(doc, state, fields) is { } detail)
                {
                    mismatches++;
                    Find(new LedgerFinding(state.DocumentId, LedgerFindingKind.ValueMismatch, state.Version, doc.ProjectionVersion, doc.ExternalVersion, detail));
                }
            }
        }

        return (missing, mismatches);
    }

    private string? Mismatch(IndexedDocument doc, PostgresDocumentState state, IReadOnlyList<CodingFieldInfo> fields)
    {
        if (doc.Source["workspaceId"]?.GetValue<string>() is { } ws && Guid.TryParse(ws, out var id) && id != _options.WorkspaceId)
        {
            return $"indexed under workspace {ws}";
        }

        foreach (var field in fields)
        {
            var expected = CodingValues.FromPostgres(field, state.Values.GetValueOrDefault(field.FieldId), state.Choices.GetValueOrDefault(field.FieldId));
            var actual = CodingValues.FromIndex(field, _layout.CodingValue(doc.Source, field));
            if (!CodingValues.Equal(expected, actual))
            {
                return $"field {field.FieldId}: PostgreSQL [{string.Join(", ", expected)}], index [{string.Join(", ", actual)}]";
            }
        }

        return null;
    }

    /// <summary>Invariants that hold at every observation: <c>_version == projectionVersion ≤ pg.DocumentVersion</c>.</summary>
    private void CheckInvariants(IndexedDocument doc, long? postgresVersion)
    {
        if (doc.ProjectionVersion != doc.ExternalVersion)
        {
            Stale(doc.DocumentId, LedgerFindingKind.ExternalVersionMismatch, postgresVersion, doc, "a write bypassed external versioning");
        }

        if (postgresVersion is { } pg && (doc.ProjectionVersion ?? doc.ExternalVersion) > pg)
        {
            Stale(doc.DocumentId, LedgerFindingKind.VersionAhead, postgresVersion, doc, "PostgreSQL never committed this version");
        }
    }

    private void Observe(Tracked tracked, Guid id, long version, long? postgresVersion)
    {
        if (tracked.Last is { } last && version < last)
        {
            Regress(id, LedgerFindingKind.VersionRegression, postgresVersion, version, last, $"indexed version went from {last} down to {version}");
        }
        else if (tracked.Max is { } max && version < max)
        {
            _staleDocuments.Add(id);
        }

        tracked.Last = version;
        tracked.Max = Math.Max(tracked.Max ?? version, version);
        tracked.SeenIndexed = true;
    }

    private void Regress(Guid id, LedgerFindingKind kind, long? postgresVersion, long? indexed, long? previous, string detail)
    {
        _regressions++;
        _staleDocuments.Add(id);
        Find(new LedgerFinding(id, kind, postgresVersion, indexed, previous, detail));
    }

    private void Stale(Guid id, LedgerFindingKind kind, long? postgresVersion, IndexedDocument doc, string detail)
    {
        _staleDocuments.Add(id);
        Find(new LedgerFinding(id, kind, postgresVersion, doc.ProjectionVersion, doc.ExternalVersion, detail));
    }

    private void Find(LedgerFinding finding)
    {
        if (_findings.Count < _options.MaxFindings)
        {
            _findings.Add(finding);
        }
    }

    /// <summary>Reservoir sampling (Algorithm R) keeps the tracked set bounded and seeded.</summary>
    private void Track(CommittedChange change)
    {
        if (_tracked.TryGetValue(change.DocumentId, out var known))
        {
            known.Committed = Math.Max(known.Committed, change.DocumentVersion);
            return;
        }

        _documentsSeen++;
        if (_trackedOrder.Count < _options.MaxTrackedDocuments)
        {
            _tracked[change.DocumentId] = new Tracked { Committed = change.DocumentVersion };
            _trackedOrder.Add(change.DocumentId);
            return;
        }

        var slot = _random.NextInt64(_documentsSeen);
        if (slot < _options.MaxTrackedDocuments)
        {
            _tracked.Remove(_trackedOrder[(int)slot]);
            _trackedOrder[(int)slot] = change.DocumentId;
            _tracked[change.DocumentId] = new Tracked { Committed = change.DocumentVersion };
        }
    }

    private sealed class Tracked
    {
        /// <summary>Highest DocumentVersion seen committed for the document in CodingEvent.</summary>
        public long Committed { get; set; }

        public long? Last { get; set; }

        public long? Max { get; set; }

        public bool SeenIndexed { get; set; }
    }
}
