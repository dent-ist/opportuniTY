using AwesomeAssertions;

using Opportunity.Core.Jobs;

namespace Opportunity.UnitTests.Jobs;

/// <summary>
/// The full transition matrices of ADR-010 §2: every (status, trigger) pair is either the documented edge or rejected.
/// The expected edges are written out independently of the production tables.
/// </summary>
public class JobStateMachineTests
{
    private static readonly HashSet<(JobStatus, JobTrigger, JobStatus)> ExpectedJobEdges =
    [
        (JobStatus.Created, JobTrigger.BeginPreparing, JobStatus.Preparing),
        (JobStatus.Created, JobTrigger.Cancel, JobStatus.Cancelled),
        (JobStatus.Preparing, JobTrigger.Start, JobStatus.Running),
        (JobStatus.Preparing, JobTrigger.Fail, JobStatus.Failed),
        (JobStatus.Preparing, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Running, JobTrigger.Pause, JobStatus.Paused),
        (JobStatus.Paused, JobTrigger.Resume, JobStatus.Running),
        (JobStatus.Running, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Paused, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Running, JobTrigger.Complete, JobStatus.Completed),
        (JobStatus.Running, JobTrigger.CompleteWithErrors, JobStatus.CompletedWithErrors),
        (JobStatus.Running, JobTrigger.Fail, JobStatus.Failed),
        (JobStatus.Cancelling, JobTrigger.FinishCancelling, JobStatus.Cancelled),
        (JobStatus.CompletedWithErrors, JobTrigger.ReplayFailedChunks, JobStatus.Running),
    ];

    private static readonly HashSet<(JobChunkStatus, JobChunkTrigger, JobChunkStatus)> ExpectedChunkEdges =
    [
        (JobChunkStatus.Pending, JobChunkTrigger.Dispatch, JobChunkStatus.Dispatched),
        (JobChunkStatus.Pending, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.RetryWait, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.Running, JobChunkTrigger.Reclaim, JobChunkStatus.Running),
        (JobChunkStatus.Running, JobChunkTrigger.Commit, JobChunkStatus.Committed),
        (JobChunkStatus.Running, JobChunkTrigger.RetryLater, JobChunkStatus.RetryWait),
        (JobChunkStatus.Running, JobChunkTrigger.FailPermanently, JobChunkStatus.Failed),
        (JobChunkStatus.Pending, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Dispatched, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.RetryWait, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Running, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Running, JobChunkTrigger.Yield, JobChunkStatus.Pending),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Yield, JobChunkStatus.Pending),
        (JobChunkStatus.Running, JobChunkTrigger.ExpireLease, JobChunkStatus.Pending),
        (JobChunkStatus.Pending, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.RetryWait, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Running, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Failed, JobChunkTrigger.Replay, JobChunkStatus.Pending),
    ];

    public static TheoryData<JobStatus, JobTrigger> AllJobPairs()
    {
        var data = new TheoryData<JobStatus, JobTrigger>();
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            foreach (var trigger in Enum.GetValues<JobTrigger>())
            {
                data.Add(status, trigger);
            }
        }

        return data;
    }

    public static TheoryData<JobChunkStatus, JobChunkTrigger> AllChunkPairs()
    {
        var data = new TheoryData<JobChunkStatus, JobChunkTrigger>();
        foreach (var status in Enum.GetValues<JobChunkStatus>())
        {
            foreach (var trigger in Enum.GetValues<JobChunkTrigger>())
            {
                data.Add(status, trigger);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllJobPairs))]
    public void Job_transition_matrix_allows_exactly_the_documented_edges(JobStatus from, JobTrigger trigger)
    {
        var expected = ExpectedJobEdges.SingleOrDefault(e => e.Item1 == from && e.Item2 == trigger);
        var allowed = JobStateMachine.TryTransition(from, trigger, out var to);

        if (expected == default)
        {
            allowed.Should().BeFalse($"{from} --{trigger}--> is not in ADR-010 §2");
            FluentActions.Invoking(() => JobStateMachine.Transition(from, trigger))
                .Should().Throw<InvalidJobTransitionException>().WithMessage($"*{trigger}*{from}*");
            JobStateMachine.SourcesOf(trigger).Should().NotContain(from);
        }
        else
        {
            allowed.Should().BeTrue();
            to.Should().Be(expected.Item3);
            JobStateMachine.Transition(from, trigger).Should().Be(expected.Item3);
            JobStateMachine.SourcesOf(trigger).Should().Contain(from);
        }
    }

    [Theory]
    [MemberData(nameof(AllChunkPairs))]
    public void Chunk_transition_matrix_allows_exactly_the_documented_edges(JobChunkStatus from, JobChunkTrigger trigger)
    {
        var expected = ExpectedChunkEdges.SingleOrDefault(e => e.Item1 == from && e.Item2 == trigger);
        var allowed = JobChunkStateMachine.TryTransition(from, trigger, out var to);

        if (expected == default)
        {
            allowed.Should().BeFalse($"{from} --{trigger}--> is not in ADR-010 §2");
            FluentActions.Invoking(() => JobChunkStateMachine.Transition(from, trigger))
                .Should().Throw<InvalidJobTransitionException>();
            JobChunkStateMachine.SourcesOf(trigger).Should().NotContain(from);
        }
        else
        {
            allowed.Should().BeTrue();
            to.Should().Be(expected.Item3);
            JobChunkStateMachine.SourcesOf(trigger).Should().Contain(from);
            JobChunkStateMachine.TargetOf(trigger).Should().Be(expected.Item3);
        }
    }

    [Fact]
    public void Matrices_have_no_edges_beyond_the_documented_ones()
    {
        JobStateMachine.Transitions.Should().BeEquivalentTo(ExpectedJobEdges);
        JobChunkStateMachine.Transitions.Should().BeEquivalentTo(ExpectedChunkEdges);
    }

    [Fact]
    public void Terminal_states_have_no_way_out_and_every_other_state_has_one()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            var outgoing = Enum.GetValues<JobTrigger>().Count(t => JobStateMachine.TryTransition(status, t, out _));
            (outgoing == 0).Should().Be(JobStateMachine.IsTerminal(status), status.ToString());
        }

        foreach (var status in Enum.GetValues<JobChunkStatus>())
        {
            var outgoing = Enum.GetValues<JobChunkTrigger>().Count(t => JobChunkStateMachine.TryTransition(status, t, out _));
            (outgoing == 0).Should().Be(JobChunkStateMachine.IsTerminal(status), status.ToString());
        }

        JobStateMachine.IsFinished(JobStatus.CompletedWithErrors).Should().BeTrue();
        JobStateMachine.IsTerminal(JobStatus.CompletedWithErrors).Should().BeFalse("failed chunks can be replayed");
        JobChunkStateMachine.IsSettled(JobChunkStatus.Failed).Should().BeTrue();
        JobChunkStateMachine.IsTerminal(JobChunkStatus.Failed).Should().BeFalse("operator replay");
    }

    [Fact]
    public void Every_state_is_reachable_from_the_initial_state()
    {
        Reachable(JobStatus.Created, s => Enum.GetValues<JobTrigger>()
                .Select(t => JobStateMachine.TryTransition(s, t, out var to) ? to : (JobStatus?)null).OfType<JobStatus>())
            .Should().BeEquivalentTo(Enum.GetValues<JobStatus>());
        Reachable(JobChunkStatus.Pending, s => Enum.GetValues<JobChunkTrigger>()
                .Select(t => JobChunkStateMachine.TryTransition(s, t, out var to) ? to : (JobChunkStatus?)null).OfType<JobChunkStatus>())
            .Should().BeEquivalentTo(Enum.GetValues<JobChunkStatus>());
    }

    [Fact]
    public void Committed_chunks_never_change_and_only_a_lease_holder_can_commit()
    {
        Enum.GetValues<JobChunkTrigger>().Where(t => JobChunkStateMachine.TryTransition(JobChunkStatus.Committed, t, out _))
            .Should().BeEmpty();
        JobChunkStateMachine.SourcesOf(JobChunkTrigger.Commit).Should().Equal(JobChunkStatus.Running);
    }

    [Fact]
    public void Stored_chunk_status_codes_are_fixed()
    {
        Enum.GetValues<JobChunkStatus>().Select(s => (short)s).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        Enum.GetValues<ChunkMembershipKind>().Select(s => (short)s).Should().Equal(1, 2, 3, 4);
        Enum.GetValues<ChunkErrorClass>().Select(s => (short)s).Should().Equal(1, 2, 3);
        Enum.GetValues<JobItemResultKind>().Select(s => (short)s).Should().Equal(1, 2, 3);
    }

    private static HashSet<T> Reachable<T>(T start, Func<T, IEnumerable<T>> next)
    {
        var seen = new HashSet<T> { start };
        var queue = new Queue<T>([start]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var to in next(current).Where(seen.Add))
            {
                queue.Enqueue(to);
            }
        }

        return seen;
    }
}
