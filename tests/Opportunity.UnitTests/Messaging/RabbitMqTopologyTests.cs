using System.Text.RegularExpressions;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Opportunity.Application.Messaging;
using Opportunity.Messaging;

namespace Opportunity.UnitTests.Messaging;

/// <summary>The declared topology matches ADR-001 §5.3 (lanes), ADR-010 §7 (retry, delivery limit, DLQ) and ADR-015 D9.5.</summary>
public sealed class RabbitMqTopologyTests
{
    private static readonly RabbitMqOptions Options = new() { ConnectionString = "amqp://localhost/" };
    private static readonly RabbitMqTopology Topology = RabbitMqTopology.Build(Options);

    [Fact]
    public void Retry_delays_follow_adr_010_backoff()
    {
        Options.RetryDelays().Should().Equal(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40));
        new RabbitMqOptions { MaxTransportRetries = 9 }.RetryDelays().Should().EndWith(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Indexing_has_four_lane_queues_and_no_message_priorities()
    {
        WorkQueues.All.Where(q => q.WorkerType == "indexing").Select(q => q.Lane).Should().BeEquivalentTo(
            [MessageLane.Security, MessageLane.Interactive, MessageLane.SecurityBulk, MessageLane.Bulk]);
        Topology.Queues.Should().OnlyContain(q => !q.Arguments.ContainsKey("x-max-priority"));
    }

    [Fact]
    public void Every_queue_is_a_durable_quorum_queue()
    {
        Topology.Queues.Should().OnlyContain(q => Equals(q.Arguments["x-queue-type"], "quorum"));
    }

    [Theory]
    [MemberData(nameof(QueueNames))]
    public void Work_queue_has_delivery_limit_dead_letter_queue_and_retry_return(string name)
    {
        var queue = WorkQueues.All.Single(q => q.Name == name);
        var definition = Topology.Queues.Single(q => q.Name == name);

        definition.Arguments["x-delivery-limit"].Should().Be(5L);
        definition.Arguments["x-dead-letter-exchange"].Should().Be($"{queue.Area}.dlx");
        definition.Arguments["x-dead-letter-routing-key"].Should().Be(name);
        Topology.Bindings.Should().Contain(new BindingDefinition(RabbitMqTopology.WorkExchange, name, name));
        Topology.Bindings.Should().Contain(b => b.Exchange == $"{queue.Area}.retry.return" && b.Queue == name
            && Equals(b.Arguments![TransportHeaders.Queue], name));

        var dlq = Topology.Queues.Single(q => q.Name == $"{name}.dlq");
        dlq.Arguments["x-max-length"].Should().Be(100_000L);
        dlq.Arguments["x-message-ttl"].Should().Be((long)TimeSpan.FromDays(14).TotalMilliseconds);
        Topology.Bindings.Should().Contain(new BindingDefinition($"{queue.Area}.dlx", $"{name}.dlq", name));
        Topology.Bindings.Should().Contain(new BindingDefinition($"{queue.Area}.dlx", RabbitMqTopology.DeadLetterRecordQueue, name),
            "the recorder gets its own copy; the DLQ keeps the diagnostic one");
    }

    [Fact]
    public void Dead_letter_recorder_queue_receives_every_dead_lettered_and_parked_message()
    {
        var queue = Topology.Queues.Single(q => q.Name == "opportunity.dead-letter.record");
        queue.Arguments["x-max-length"].Should().Be(100_000L);
        queue.Arguments["x-overflow"].Should().Be("drop-head", "a full recorder queue must never block dead-lettering");
        queue.Arguments.Should().NotContainKey("x-dead-letter-exchange");
        foreach (var area in WorkQueues.All.Select(q => q.Area).Distinct())
        {
            Topology.Bindings.Should().Contain(new BindingDefinition($"{area}.dlx", queue.Name, RabbitMqTopology.ParkingRoutingKey));
        }

        Topology.Bindings.Count(b => b.Queue == queue.Name).Should().Be(
            WorkQueues.All.Count + WorkQueues.All.Select(q => q.Area).Distinct().Count());
    }

    [Fact]
    public void Each_area_has_retry_tiers_back_to_its_own_queues_and_a_parking_queue()
    {
        foreach (var area in WorkQueues.All.Select(q => q.Area).Distinct())
        {
            foreach (var delay in Options.RetryDelays())
            {
                var retry = Topology.Queues.Single(q => q.Name == RabbitMqTopology.RetryQueue(area, delay));
                retry.Arguments["x-message-ttl"].Should().Be((long)delay.TotalMilliseconds);
                retry.Arguments["x-dead-letter-exchange"].Should().Be($"{area}.retry.return");
                Topology.Bindings.Should().Contain(new BindingDefinition($"{area}.retry", retry.Name, RabbitMqTopology.RetryTier(delay)));
            }

            Topology.Queues.Single(q => q.Name == $"{area}.parking").Arguments["x-overflow"].Should().Be("reject-publish");
            Topology.Bindings.Should().Contain(new BindingDefinition($"{area}.dlx", $"{area}.parking", RabbitMqTopology.ParkingRoutingKey));
        }
    }

    [Fact]
    public void Retry_tiers_are_named_by_delay()
    {
        RabbitMqTopology.RetryTier(TimeSpan.FromSeconds(5)).Should().Be("5s");
        RabbitMqTopology.RetryTier(TimeSpan.FromMinutes(5)).Should().Be("5m");
        RabbitMqTopology.RetryTier(TimeSpan.FromMilliseconds(250)).Should().Be("250ms");
    }

    [Fact]
    public void Prefetch_defaults_per_queue_and_can_be_overridden()
    {
        var options = new RabbitMqOptions();
        options.Prefetch["index.bulk"] = 8;

        options.PrefetchFor(WorkQueues.IndexBulk).Should().Be(8);
        options.PrefetchFor(WorkQueues.Import).Should().Be(WorkQueues.Import.Prefetch);
    }

    [Fact]
    public void Area_credentials_and_signing_bind_from_configuration_and_are_validated()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RabbitMq"] = "amqp://dispatcher@broker/",
            ["Messaging:RabbitMq:AreaConnectionStrings:render"] = "amqps://render@broker/",
            ["Messaging:RabbitMq:Signing:Enabled"] = "true",
            ["Messaging:RabbitMq:Signing:KeyId"] = "k2",
            ["Messaging:RabbitMq:Signing:AcceptedKeyIds"] = "k1, k2",
        }).Build();

        var options = RabbitMqOptions.Bind(configuration);
        options.Validate();

        options.CredentialFor("render").Should().Be("render");
        options.CredentialFor("export").Should().BeNull("areas without their own user use the default");
        options.ConnectionStringFor("render").Should().Be("amqps://render@broker/");
        options.ConnectionStringFor(null).Should().Be("amqp://dispatcher@broker/");
        options.Signing.Enabled.Should().BeTrue();
        options.Signing.VerificationKeyIds().Should().Equal("k2", "k1");

        options.AreaConnectionStrings["export"] = "http://not-amqp/";
        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*AreaConnectionStrings:export*");
    }

    [Fact]
    public void Worker_permissions_cover_only_its_own_area()
    {
        var index = RabbitMqPermissions.Worker("index");

        Regex.IsMatch("", index.Configure).Should().BeTrue();
        Regex.IsMatch("index.bulk", index.Configure).Should().BeFalse();
        Regex.IsMatch("index.bulk", index.Read).Should().BeTrue();
        Regex.IsMatch("index.security", index.Read).Should().BeTrue();
        Regex.IsMatch("import.chunks", index.Read).Should().BeFalse();
        Regex.IsMatch("index.bulk.dlq", index.Read).Should().BeFalse("workers never read DLQs");
        Regex.IsMatch("index.parking", index.Read).Should().BeFalse("nor parking queues (E05-T07)");
        Regex.IsMatch("index.bulkx", index.Read).Should().BeFalse();
        Regex.IsMatch("index.retry", index.Write).Should().BeTrue();
        Regex.IsMatch("index.dlx", index.Write).Should().BeTrue();
        Regex.IsMatch(RabbitMqTopology.WorkExchange, index.Write).Should().BeFalse("only the dispatcher publishes work");
        Regex.IsMatch("import.retry", index.Write).Should().BeFalse();
        Regex.IsMatch(RabbitMqTopology.WorkExchange, RabbitMqPermissions.Dispatcher.Write).Should().BeTrue();
        Regex.IsMatch("index.bulk", RabbitMqPermissions.Dispatcher.Read).Should().BeFalse();
        Regex.IsMatch("index.bulk.dlq", RabbitMqPermissions.Dispatcher.Read).Should().BeFalse("the DLQ copies stay for diagnostics");
        Regex.IsMatch(RabbitMqTopology.DeadLetterRecordQueue, RabbitMqPermissions.Dispatcher.Read).Should().BeTrue("the dispatcher runs the recorder");
        Regex.IsMatch(RabbitMqTopology.DeadLetterRecordQueue, index.Read).Should().BeFalse();
    }

    public static TheoryData<string> QueueNames() => new(WorkQueues.All.Select(q => q.Name));
}
