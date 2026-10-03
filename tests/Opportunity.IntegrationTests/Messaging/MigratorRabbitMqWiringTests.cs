using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Bootstrap;
using Opportunity.Messaging;
using Opportunity.Migrator;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>The migrator registers the RabbitMQ topology step exactly when <c>ConnectionStrings:RabbitMq</c> is set (Refs #39).</summary>
public sealed class MigratorRabbitMqWiringTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Topology_step_is_registered_only_with_a_rabbitmq_connection_string(bool configured)
    {
        string[] args = configured ? [$"--ConnectionStrings:{RabbitMqOptions.ConnectionStringName}=amqp://user:pass@localhost:5672/"] : [];
        var steps = -1;
        var transport = false;

        // No migrator connection string: the run stops with a configuration error before touching any dependency.
        var exit = await MigratorApp.RunAsync(
            args,
            services =>
            {
                steps = services.Count(d => d.ServiceType == typeof(IInfrastructureBootstrapStep));
                transport = services.Any(d => d.ServiceType == typeof(RabbitMqConnections));
            },
            TestContext.Current.CancellationToken);

        exit.Should().Be(MigratorApp.ExitConfigurationError);
        steps.Should().Be(configured ? 1 : 0);
        transport.Should().Be(configured);
    }
}
