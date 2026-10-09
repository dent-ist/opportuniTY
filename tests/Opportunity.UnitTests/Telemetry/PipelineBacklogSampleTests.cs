using AwesomeAssertions;

using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Jobs.Dispatch;

namespace Opportunity.UnitTests.Telemetry;

/// <summary>E19-T05: the dispatcher's pipeline gauges sum workspaces into a bounded grid with zeros and oldest ages.</summary>
public sealed class PipelineBacklogSampleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Workspaces_are_summed_the_oldest_row_wins_and_every_series_is_reported()
    {
        var a = new PipelineBacklog(
            [new IndexTaskBacklog(IndexChunkTaskStatus.Running, MessageLane.Bulk, 2, Now.AddMinutes(-3))],
            [new JobChunkBacklog(JobType.Import, JobChunkStatus.Pending, 10, Now.AddMinutes(-1))],
            [new ActiveJobCount(JobType.Import, 1)]);
        var b = new PipelineBacklog(
            [new IndexTaskBacklog(IndexChunkTaskStatus.Running, MessageLane.Bulk, 1, Now.AddMinutes(-20)),
             new IndexTaskBacklog(IndexChunkTaskStatus.Failed, MessageLane.SecurityBulk, 4, Now.AddHours(-2))],
            [new JobChunkBacklog(JobType.Import, JobChunkStatus.Pending, 5, Now.AddMinutes(-7))],
            [new ActiveJobCount(JobType.Import, 2), new ActiveJobCount(JobType.Render, 1)]);

        var sample = PipelineBacklogSample.From([a, b, PipelineBacklog.Empty], Now);

        sample.IndexTasks.Should().HaveCount(PipelineBacklogSample.TaskStatuses.Count * PipelineBacklogSample.TaskLanes.Count);
        sample.IndexTasks.Should().ContainSingle(t => t.Status == IndexChunkTaskStatus.Running && t.Lane == MessageLane.Bulk)
            .Which.Should().Be((IndexChunkTaskStatus.Running, MessageLane.Bulk, 3L, TimeSpan.FromMinutes(20)));
        sample.IndexTasks.Should().Contain((IndexChunkTaskStatus.Failed, MessageLane.SecurityBulk, 4L, TimeSpan.FromHours(2)));
        sample.IndexTasks.Should().Contain((IndexChunkTaskStatus.Pending, MessageLane.Bulk, 0L, TimeSpan.Zero), "a drained backlog reports 0");

        sample.JobChunks.Should().HaveCount(Enum.GetValues<JobType>().Length * PipelineBacklogSample.ChunkStatuses.Count);
        sample.JobChunks.Should().Contain((JobType.Import, JobChunkStatus.Pending, 15L, TimeSpan.FromMinutes(7)));
        sample.ActiveJobs.Should().HaveCount(Enum.GetValues<JobType>().Length)
            .And.Contain((JobType.Import, 3L)).And.Contain((JobType.Render, 1L)).And.Contain((JobType.Export, 0L));
    }

    [Fact]
    public void Clock_skew_never_reports_a_negative_age()
    {
        var sample = PipelineBacklogSample.From(
            [new PipelineBacklog([new IndexTaskBacklog(IndexChunkTaskStatus.Pending, MessageLane.Bulk, 1, Now.AddSeconds(5))], [], [])], Now);

        sample.IndexTasks.Should().OnlyContain(t => t.OldestAge >= TimeSpan.Zero);
    }
}
