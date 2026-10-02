namespace Opportunity.Application.Bootstrap;

/// <summary>
/// An idempotent provisioning step run by the one-shot migrator after the database migrations, e.g. OpenSearch index
/// templates/aliases, RabbitMQ exchanges/queues/DLX, object-storage buckets. Each infrastructure module owns and
/// registers its own step. Steps must succeed when the resources already exist.
/// </summary>
public interface IInfrastructureBootstrapStep
{
    /// <summary>Short name used in logs.</summary>
    string Name { get; }

    /// <summary>Lower runs first; database migrations always run before any step.</summary>
    int Order { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
