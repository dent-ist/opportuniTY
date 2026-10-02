using System.Text;

using AwesomeAssertions;

using Opportunity.Testing.RabbitMq;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.IntegrationTests.Containers;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class RabbitMqFixtureTests(RabbitMqFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publishes_and_consumes_a_message_in_an_isolated_vhost()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var connection = await Factory(vhost.AmqpUri).CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);

        await channel.QueueDeclareAsync("work", durable: false, exclusive: false, autoDelete: false, cancellationToken: Ct);
        await channel.BasicPublishAsync(string.Empty, "work", Encoding.UTF8.GetBytes("hello"), Ct);

        var received = await GetAsync(channel, "work");
        Encoding.UTF8.GetString(received!.Body.Span).Should().Be("hello");
    }

    [Fact]
    public async Task Virtual_hosts_do_not_share_queues()
    {
        await using var first = await fixture.CreateVirtualHostAsync(Ct);
        await using var second = await fixture.CreateVirtualHostAsync(Ct);

        await using (var a = await Factory(first.AmqpUri).CreateConnectionAsync(Ct))
        await using (var channel = await a.CreateChannelAsync(cancellationToken: Ct))
        {
            await channel.QueueDeclareAsync("only-in-first", durable: false, exclusive: false, autoDelete: false, cancellationToken: Ct);
        }

        await using var b = await Factory(second.AmqpUri).CreateConnectionAsync(Ct);
        await using var other = await b.CreateChannelAsync(cancellationToken: Ct);
        var passive = () => other.QueueDeclarePassiveAsync("only-in-first", Ct);
        await passive.Should().ThrowAsync<OperationInterruptedException>();
    }

    [Fact]
    public async Task Latency_toxic_slows_round_trips_and_lifting_it_restores_speed()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        await using var connection = await Factory(vhost.AmqpUriVia(proxy)).CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);

        await using (await proxy.AddLatencyAsync(Timing.InjectedLatency, cancellationToken: Ct))
        {
            (await Timing.MeasureAsync(() => channel.QueueDeclareAsync("slow", false, false, true, cancellationToken: Ct)))
                .Should().BeGreaterThanOrEqualTo(Timing.MinimumObservedLatency);
        }

        (await Timing.MeasureAsync(() => channel.QueueDeclareAsync("fast", false, false, true, cancellationToken: Ct)))
            .Should().BeLessThan(Timing.MinimumObservedLatency);
    }

    [Fact]
    public async Task Cut_link_closes_the_connection_and_restoring_it_allows_reconnect()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        var factory = Factory(vhost.AmqpUriVia(proxy));
        await using (var connection = await factory.CreateConnectionAsync(Ct))
        {
            var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ConnectionShutdownAsync += (_, _) =>
            {
                shutdown.TrySetResult();
                return Task.CompletedTask;
            };

            await proxy.CutAsync(Ct);
            await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            connection.IsOpen.Should().BeFalse();
        }

        var reconnect = () => factory.CreateConnectionAsync(Ct);
        await reconnect.Should().ThrowAsync<BrokerUnreachableException>();

        await proxy.RestoreAsync(Ct);
        await using var recovered = await factory.CreateConnectionAsync(Ct);
        recovered.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Timeout_toxic_makes_connection_attempts_time_out()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        var factory = Factory(vhost.AmqpUriVia(proxy));

        await using (await proxy.AddTimeoutAsync(TimeSpan.Zero, cancellationToken: Ct))
        {
            var connect = () => factory.CreateConnectionAsync(Ct);
            await connect.Should().ThrowAsync<BrokerUnreachableException>();
        }

        await using var recovered = await factory.CreateConnectionAsync(Ct);
        recovered.IsOpen.Should().BeTrue();
    }

    private static ConnectionFactory Factory(Uri uri) => new()
    {
        Uri = uri,
        AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
        HandshakeContinuationTimeout = TimeSpan.FromSeconds(2),
        ContinuationTimeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<BasicGetResult?> GetAsync(IChannel channel, string queue)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true, Ct);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100, Ct);
        }

        return null;
    }
}
