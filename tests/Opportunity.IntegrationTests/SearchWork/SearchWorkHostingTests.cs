using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Data.Audit;
using Opportunity.Hosting.Workers;
using Opportunity.Jobs;
using Opportunity.Jobs.Dispatch;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E06-T03: search work partitions are only created ahead while the housekeeping loop runs, so the dispatcher worker
/// must host it (next to the audit/coding partition maintenance); inserts fail once the 31-day horizon passes otherwise.
/// </summary>
public sealed class SearchWorkHostingTests
{
    [Fact]
    public void The_dispatcher_worker_hosts_search_work_housekeeping_and_no_other_worker_does()
    {
        foreach (var type in WorkerTypes.All)
        {
            var services = new ServiceCollection();
            WorkerModuleCatalog.Modules[type].Register(services, new ConfigurationBuilder().Build());
            var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

            if (type == WorkerTypes.Dispatcher)
            {
                hosted.Should().Contain(typeof(SearchWorkHousekeepingService)).And.Contain(typeof(PartitionMaintenanceService))
                    .And.Contain(typeof(OutboxDispatcherService)).And.Contain(typeof(JobLeaseSweeperService));
                services.Should().Contain(d => d.ServiceType == typeof(ISearchWorkMaintenance))
                    .And.Contain(d => d.ServiceType == typeof(ISearchWorkWakeUpListener))
                    .And.Contain(d => d.ServiceType == typeof(IJobChunkDispatchRepository));
            }
            else
            {
                hosted.Should().NotContain(typeof(SearchWorkHousekeepingService), $"only the dispatcher runs housekeeping ({type})")
                    .And.NotContain(typeof(OutboxDispatcherService), $"only the dispatcher publishes ({type})");
            }
        }
    }

    [Fact]
    public void The_dispatcher_registers_the_rabbitmq_publisher_only_when_a_broker_is_configured()
    {
        var without = new ServiceCollection();
        WorkerModuleCatalog.Modules[WorkerTypes.Dispatcher].Register(without, new ConfigurationBuilder().Build());
        without.Should().NotContain(d => d.ServiceType == typeof(IMessagePublisher));

        var with = new ServiceCollection();
        WorkerModuleCatalog.Modules[WorkerTypes.Dispatcher].Register(with, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@localhost:5672/",
                ["Dispatcher:BatchSize"] = "50",
            })
            .Build());
        with.Should().Contain(d => d.ServiceType == typeof(IMessagePublisher));
        using var provider = with.BuildServiceProvider();
        provider.GetRequiredService<OutboxDispatcherOptions>().BatchSize.Should().Be(50, "Dispatcher:* binds the options");
    }

    [Fact]
    public async Task The_dispatcher_and_lease_sweeper_without_a_database_disable_themselves_instead_of_crashing_the_host()
    {
        var services = new ServiceCollection().AddLogging();
        WorkerModuleCatalog.Modules[WorkerTypes.Dispatcher].Register(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var loops = services.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t == typeof(OutboxDispatcherService) || t == typeof(JobLeaseSweeperService))
            .Select(t => (BackgroundService)ActivatorUtilities.CreateInstance(provider, t!))
            .ToList();
        loops.Should().HaveCount(2);

        foreach (var loop in loops)
        {
            await loop.StartAsync(TestContext.Current.CancellationToken);
            await loop.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await loop.StopAsync(TestContext.Current.CancellationToken);
            loop.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Housekeeping_without_a_database_disables_itself_instead_of_crashing_the_host()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSearchWorkHousekeeping(new SearchWorkHousekeepingOptions { Interval = TimeSpan.FromMilliseconds(50) });
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetServices<IHostedService>().OfType<SearchWorkHousekeepingService>().Single();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
    }
}
