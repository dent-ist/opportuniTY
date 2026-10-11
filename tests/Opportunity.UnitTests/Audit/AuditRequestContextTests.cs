using AwesomeAssertions;

using Opportunity.Application.Audit;

namespace Opportunity.UnitTests.Audit;

/// <summary>E14-T02: the request context fills the envelope gaps of the request actor's events, and nothing else.</summary>
public sealed class AuditRequestContextTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private static readonly byte[] Hash = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private static AuditEvent Event(string actorId = "user-1") => new()
    {
        WorkspaceId = Workspace,
        OccurredAt = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero),
        Category = AuditTaxonomy.Privilege.Category,
        Action = AuditTaxonomy.Privilege.ConflictReportExported,
        ActorType = AuditActorType.User,
        ActorId = actorId,
        ActorDisplay = "Alice",
        Outcome = AuditOutcome.Success,
    };

    private static AuditRequestContext Request() => new()
    {
        ActorId = "user-1",
        ClientIp = "192.0.2.7",
        UserAgent = "probe/1.0",
        SessionIdHash = Hash,
        CorrelationId = "corr-1",
    };

    [Fact]
    public void Outside_a_request_events_are_unchanged()
    {
        AuditRequestContext.Current.Should().BeNull();
        var e = Event();
        AuditRequestContext.Complete(e).Should().BeSameAs(e);
    }

    [Fact]
    public void The_request_actors_events_get_client_session_correlation_and_their_workspace_as_object()
    {
        using (AuditRequestContext.Enter(Request()))
        {
            var completed = AuditRequestContext.Complete(Event());

            completed.ClientIp.Should().Be("192.0.2.7");
            completed.UserAgent.Should().Be("probe/1.0");
            completed.SessionIdHash!.Value.ToArray().Should().Equal(Hash);
            completed.CorrelationId.Should().Be("corr-1");
            completed.ResourceType.Should().Be(AuditRequestContext.WorkspaceResourceType);
            completed.ResourceId.Should().Be(Workspace.ToString());
            AuditEventRules.Validate(completed).Should().BeEmpty();
        }

        AuditRequestContext.Current.Should().BeNull("the scope restores the previous context");
    }

    [Fact]
    public void Values_the_writer_set_win()
    {
        using (AuditRequestContext.Enter(Request()))
        {
            var completed = AuditRequestContext.Complete(Event() with
            {
                ClientIp = "198.51.100.1",
                CorrelationId = "own",
                ResourceType = "Document",
                ResourceId = "d-1",
            });

            completed.ClientIp.Should().Be("198.51.100.1");
            completed.CorrelationId.Should().Be("own");
            completed.ResourceType.Should().Be("Document");
            completed.ResourceId.Should().Be("d-1");
            completed.SessionIdHash.Should().NotBeNull();
        }
    }

    [Fact]
    public void Events_of_another_actor_keep_what_their_writer_set()
    {
        using (AuditRequestContext.Enter(Request()))
        {
            var other = Event("dispatcher");
            AuditRequestContext.Complete(other).Should().BeSameAs(other, "a background loop that inherited the request must not borrow its client or session");
        }
    }
}
