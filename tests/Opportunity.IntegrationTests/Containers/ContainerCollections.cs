using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.ObjectStore;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.Postgres;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Containers;

// One collection per dependency: each container starts once, tests within a collection share it (isolated per
// test by database / index prefix / vhost / bucket), and the four collections run in parallel.

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}

/// <summary>Also starts a PostgreSQL container: the search service suites need authoritative security state (E07-T05).</summary>
[CollectionDefinition(Name)]
public sealed class OpenSearchCollectionDefinition : ICollectionFixture<OpenSearchFixture>, ICollectionFixture<MigrationPostgresFixture>
{
    public const string Name = "OpenSearch";
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollectionDefinition : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}

[CollectionDefinition(Name)]
public sealed class ObjectStoreCollectionDefinition : ICollectionFixture<ObjectStoreFixture>
{
    public const string Name = "ObjectStore";
}
