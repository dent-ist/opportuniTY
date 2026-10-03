using AwesomeAssertions;

using Opportunity.Core.Jobs;

namespace Opportunity.UnitTests.Jobs;

/// <summary>
/// Randomized (seeded, hand-rolled property) tests: random interleavings of claims, commits, failures, lease expiry,
/// sweeps, pause, cancel and replay by several workers, driven only through the domain rules (state machines, retry
/// policy, settlement). After every step the invariants of ADR-010 must hold. A failing run reports its seed.
/// </summary>
public class JobStateMachinePropertyTests
{
    public static TheoryData<int> Seeds => new(Enumerable.Range(1, 200));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Invariants_hold_under_random_interleavings(int seed)
    {
        var random = new Random(seed);
        var model = new JobModel(chunkCount: random.Next(1, 12), maxAttempts: random.Next(1, 5));
        for (var step = 0; step < 300 && !JobStateMachine.IsTerminal(model.Status); step++)
        {
            var before = model.Status;
            model.Step(random);
            model.AssertInvariants($"seed {seed}, step {step}");
            if (JobStateMachine.IsTerminal(before))
            {
                model.Status.Should().Be(before, "terminal job states are final");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_job_whose_workers_keep_trying_always_finishes(int seed)
    {
        var random = new Random(seed);
        var model = new JobModel(chunkCount: random.Next(1, 12), maxAttempts: random.Next(1, 5));
        for (var step = 0; step < 5_000 && !JobStateMachine.IsFinished(model.Status); step++)
        {
            model.ResumeIfPaused();
            model.StepWithoutOperatorActions(random);
        }

        model.Status.Should().BeOneOf([JobStatus.Completed, JobStatus.CompletedWithErrors], $"seed {seed}");
        model.Chunks.Should().OnlyContain(c => JobChunkStateMachine.IsSettled(c.Status));
    }

    private sealed class ChunkModel
    {
        public JobChunkStatus Status { get; set; } = JobChunkStatus.Pending;

        public int Attempts { get; set; }

        public long Token { get; set; }

        public bool LeaseExpired { get; set; }

        public int Commits { get; set; }
    }

    private sealed record Lease(int Chunk, long Token);

    private sealed class JobModel
    {
        private readonly int _maxAttempts;
        private readonly List<Lease> _held = [];
        private int _consecutiveFailures;
        private ChunkErrorClass? _lastClass;

        public JobModel(int chunkCount, int maxAttempts)
        {
            _maxAttempts = maxAttempts;
            Chunks = [.. Enumerable.Range(0, chunkCount).Select(_ => new ChunkModel())];
            Status = JobStateMachine.Transition(JobStateMachine.Transition(JobStatus.Created, JobTrigger.BeginPreparing), JobTrigger.Start);
            Counters = new JobCounters { ChunksTotal = chunkCount };
        }

        public List<ChunkModel> Chunks { get; }

        public JobStatus Status { get; private set; }

        public JobCounters Counters { get; private set; }

        private bool CancelsChunks => Status is JobStatus.Cancelling or JobStatus.Failed or JobStatus.Cancelled;

        public void Step(Random random)
        {
            switch (random.Next(10))
            {
                case 0: Operator(random); break;
                default: StepWithoutOperatorActions(random); break;
            }
        }

        /// <summary>The operator resumes a job the circuit breaker paused.</summary>
        public void ResumeIfPaused()
        {
            if (Status == JobStatus.Paused)
            {
                Status = JobStateMachine.Transition(Status, JobTrigger.Resume);
                _consecutiveFailures = 0;
            }
        }

        public void StepWithoutOperatorActions(Random random)
        {
            switch (random.Next(8))
            {
                case 0: Dispatch(random.Next(Chunks.Count)); break;
                case 1 or 2: Claim(random.Next(Chunks.Count)); break;
                case 3 or 4: Commit(random); break;
                case 5: Fail(random); break;
                case 6: ExpireLease(random); break;
                default: Sweep(); break;
            }
        }

        public void AssertInvariants(string because)
        {
            Counters.ChunksCommitted.Should().Be(Chunks.Count(c => c.Status == JobChunkStatus.Committed), because);
            Counters.ChunksFailed.Should().Be(Chunks.Count(c => c.Status == JobChunkStatus.Failed), because);
            Counters.ChunksCancelled.Should().Be(Chunks.Count(c => c.Status == JobChunkStatus.Cancelled), because);
            Chunks.Should().OnlyContain(c => c.Commits <= 1, because + ": a chunk takes effect at most once");
            Chunks.Should().OnlyContain(c => c.Attempts >= 0 && c.Attempts <= _maxAttempts, because);
            Chunks.Should().OnlyContain(c => (c.Status == JobChunkStatus.Committed) == (c.Commits == 1), because);
            switch (Status)
            {
                case JobStatus.Completed:
                    Chunks.Should().OnlyContain(c => c.Status == JobChunkStatus.Committed, because);
                    break;
                case JobStatus.CompletedWithErrors:
                    Chunks.Should().OnlyContain(c => JobChunkStateMachine.IsSettled(c.Status), because);
                    Chunks.Should().Contain(c => c.Status == JobChunkStatus.Failed, because);
                    break;
                case JobStatus.Cancelled:
                    Chunks.Should().OnlyContain(c => JobChunkStateMachine.IsSettled(c.Status), because);
                    break;
                case JobStatus.Cancelling:
                    Chunks.Should().OnlyContain(c => JobChunkStateMachine.IsSettled(c.Status) || c.Status == JobChunkStatus.Running, because);
                    break;
            }
        }

        private void Operator(Random random)
        {
            var trigger = random.Next(4) switch
            {
                0 => JobTrigger.Pause,
                1 => JobTrigger.Resume,
                2 => JobTrigger.Cancel,
                _ => JobTrigger.ReplayFailedChunks,
            };

            if (trigger == JobTrigger.ReplayFailedChunks)
            {
                if (Status is not (JobStatus.Running or JobStatus.Paused or JobStatus.CompletedWithErrors))
                {
                    return;
                }

                var failed = Chunks.Where(c => c.Status == JobChunkStatus.Failed).ToList();
                if (failed.Count == 0)
                {
                    return;
                }

                foreach (var chunk in failed)
                {
                    Move(chunk, JobChunkTrigger.Replay);
                    chunk.Attempts = 0;
                }

                if (Status == JobStatus.CompletedWithErrors)
                {
                    Status = JobStateMachine.Transition(Status, trigger);
                }

                _consecutiveFailures = 0;
                Settle();
                return;
            }

            if (!JobStateMachine.TryTransition(Status, trigger, out var to))
            {
                return;
            }

            Status = to;
            if (trigger == JobTrigger.Resume)
            {
                _consecutiveFailures = 0;
            }

            if (Status == JobStatus.Cancelling)
            {
                foreach (var chunk in Chunks.Where(c => c.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait))
                {
                    Move(chunk, JobChunkTrigger.Cancel);
                }
            }

            Settle();
        }

        private void Dispatch(int index)
        {
            if (Chunks[index].Status is (JobChunkStatus.Pending or JobChunkStatus.RetryWait) && Status == JobStatus.Running)
            {
                Move(Chunks[index], JobChunkTrigger.Dispatch);
            }
        }

        // Fence F1 plus the claim statement of ADR-010 §3.1.
        private void Claim(int index)
        {
            var chunk = Chunks[index];
            if (JobChunkStateMachine.IsSettled(chunk.Status) || (chunk.Status == JobChunkStatus.Running && !chunk.LeaseExpired))
            {
                return;
            }

            if (CancelsChunks)
            {
                Move(chunk, JobChunkTrigger.Cancel);
                Settle();
                return;
            }

            if (Status != JobStatus.Running)
            {
                if (chunk.Status == JobChunkStatus.Dispatched)
                {
                    Move(chunk, JobChunkTrigger.Yield);
                }

                return;
            }

            if (chunk.Attempts >= _maxAttempts)
            {
                Move(chunk, JobChunkTrigger.ExhaustAttempts);
                RecordFailures(1, ChunkErrorClass.AttemptsExhausted);
                return;
            }

            Move(chunk, chunk.Status == JobChunkStatus.Running ? JobChunkTrigger.Reclaim : JobChunkTrigger.Claim);
            chunk.Attempts++;
            chunk.Token++;
            chunk.LeaseExpired = false;
            _held.Add(new Lease(index, chunk.Token));
        }

        // Fence F3: only the current token of a Running chunk commits, and only while the job is Running.
        private void Commit(Random random)
        {
            if (TakeLease(random) is not { } lease)
            {
                return;
            }

            var chunk = Chunks[lease.Chunk];
            if (chunk.Status != JobChunkStatus.Running || chunk.Token != lease.Token)
            {
                return;
            }

            if (Status == JobStatus.Running)
            {
                Move(chunk, JobChunkTrigger.Commit);
                chunk.Commits++;
                Counters = Counters with { ChunksCommitted = Counters.ChunksCommitted + 1 };
                _consecutiveFailures = 0;
                Settle();
            }
            else
            {
                Release(chunk);
            }
        }

        private void Fail(Random random)
        {
            if (TakeLease(random) is not { } lease)
            {
                return;
            }

            var chunk = Chunks[lease.Chunk];
            if (chunk.Status != JobChunkStatus.Running || chunk.Token != lease.Token)
            {
                return;
            }

            var errorClass = random.Next(2) == 0 ? ChunkErrorClass.Transient : ChunkErrorClass.Permanent;
            var trigger = ChunkRetryPolicy.OnError(errorClass, chunk.Attempts, _maxAttempts);
            if (trigger == JobChunkTrigger.RetryLater && CancelsChunks)
            {
                Move(chunk, JobChunkTrigger.Cancel);
                Settle();
                return;
            }

            Move(chunk, trigger);
            if (trigger == JobChunkTrigger.FailPermanently)
            {
                RecordFailures(1, errorClass);
            }
        }

        private void Release(ChunkModel chunk)
        {
            if (CancelsChunks)
            {
                Move(chunk, JobChunkTrigger.Cancel);
                Settle();
            }
            else
            {
                Move(chunk, JobChunkTrigger.Yield);
                chunk.Attempts--;
            }
        }

        private void ExpireLease(Random random)
        {
            var running = Chunks.Where(c => c.Status == JobChunkStatus.Running && !c.LeaseExpired).ToList();
            if (running.Count > 0)
            {
                running[random.Next(running.Count)].LeaseExpired = true;
            }
        }

        // The lease sweeper of ADR-010 §3.3.
        private void Sweep()
        {
            var failed = 0;
            foreach (var chunk in Chunks.Where(c => c.Status == JobChunkStatus.Running && c.LeaseExpired))
            {
                if (CancelsChunks)
                {
                    Move(chunk, JobChunkTrigger.Cancel);
                }
                else if (chunk.Attempts >= _maxAttempts)
                {
                    Move(chunk, JobChunkTrigger.ExhaustAttempts);
                    failed++;
                }
                else
                {
                    Move(chunk, JobChunkTrigger.ExpireLease);
                }

                chunk.LeaseExpired = false;
            }

            if (failed > 0)
            {
                RecordFailures(failed, ChunkErrorClass.AttemptsExhausted);
            }
            else
            {
                Settle();
            }
        }

        private Lease? TakeLease(Random random)
        {
            if (_held.Count == 0)
            {
                return null;
            }

            var lease = _held[random.Next(_held.Count)];
            _held.Remove(lease);
            return lease;
        }

        private void RecordFailures(int count, ChunkErrorClass errorClass)
        {
            _consecutiveFailures = (_lastClass == errorClass ? _consecutiveFailures : 0) + count;
            _lastClass = errorClass;
            Settle();
        }

        private void Settle()
        {
            Counters = Counters with
            {
                ChunksCommitted = Chunks.Count(c => c.Status == JobChunkStatus.Committed),
                ChunksFailed = Chunks.Count(c => c.Status == JobChunkStatus.Failed),
                ChunksCancelled = Chunks.Count(c => c.Status == JobChunkStatus.Cancelled),
            };
            if (JobSettlement.Next(Status, Counters, _consecutiveFailures) is { } trigger)
            {
                Status = JobStateMachine.Transition(Status, trigger);
            }
        }

        private static void Move(ChunkModel chunk, JobChunkTrigger trigger) =>
            chunk.Status = JobChunkStateMachine.Transition(chunk.Status, trigger);
    }
}
