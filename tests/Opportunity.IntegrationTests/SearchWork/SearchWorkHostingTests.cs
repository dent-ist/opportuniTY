using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.SearchWork;
using Opportunity.Data.Audit;
using Opportunity.Hosting.Workers;
using Opportunity.Jobs;

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
                hosted.Should().Contain(typeof(SearchWorkHousekeepingService)).And.Contain(typeof(PartitionMaintenanceService));
                services.Should().Contain(d => d.ServiceType == typeof(ISearchWorkMaintenance));
            }
            else
            {
                hosted.Should().NotContain(typeof(SearchWorkHousekeepingService), $"only the dispatcher runs housekeeping ({type})");
            }
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
