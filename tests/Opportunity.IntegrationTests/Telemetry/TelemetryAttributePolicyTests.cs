using AwesomeAssertions;
using Opportunity.Application.Telemetry;
using Opportunity.Hosting.Telemetry;

namespace Opportunity.IntegrationTests.Telemetry;

public sealed class TelemetryAttributePolicyTests
{
    [Theory]
    [InlineData("url.query")]
    [InlineData("db.query.text")]
    [InlineData("db.statement")]
    [InlineData("db.npgsql.data_source")]
    [InlineData("http.request.header.authorization")]
    [InlineData("http.request.header.cookie")]
    [InlineData("http.response.header.set-cookie")]
    [InlineData("client.address")]
    [InlineData("user_agent.original")]
    [InlineData("network.peer.address")]
    [InlineData("exception.message")]
    [InlineData("search.query")]
    public void Sensitive_or_unknown_attributes_are_dropped(string key)
    {
        TelemetryAttributePolicy.IsAllowed(key).Should().BeFalse();
    }

    [Theory]
    [InlineData("http.route")]
    [InlineData("http.response.status_code")]
    [InlineData("url.path")]
    [InlineData("db.system.name")]
    [InlineData("db.operation.name")]
    [InlineData("messaging.destination.name")]
    [InlineData(TelemetryAttributes.WorkspaceId)]
    [InlineData(TelemetryAttributes.CorrelationId)]
    [InlineData(TelemetryAttributes.WorkerType)]
    public void Identifier_and_shape_attributes_are_kept(string key)
    {
        TelemetryAttributePolicy.IsAllowed(key).Should().BeTrue();
    }

    [Fact]
    public void Sql_text_is_kept_only_when_explicitly_enabled()
    {
        TelemetryAttributePolicy.IsAllowed("db.query.text", recordSqlStatements: true).Should().BeTrue();
        TelemetryAttributePolicy.IsAllowed("db.npgsql.data_source", recordSqlStatements: true).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://search.internal:9200/idx/_search?q=secret#frag", "https://search.internal:9200/idx/_search")]
    [InlineData("https://user:pass@store.internal/bucket/key?X-Amz-Signature=abc", "https://store.internal/bucket/key")]
    [InlineData("/api/v1/items?q=secret", "/api/v1/items")]
    [InlineData("http://otel-collector:4318/v1/traces", "http://otel-collector:4318/v1/traces")]
    public void Urls_lose_query_fragment_and_credentials(string url, string expected)
    {
        TelemetryAttributePolicy.StripQuery(url).Should().Be(expected);
    }

    [Fact]
    public void Every_catalog_attribute_survives_the_export_policy()
    {
        OpportunityMetricCatalog.All.SelectMany(m => m.Attributes).Distinct()
            .Should().OnlyContain(key => TelemetryAttributePolicy.IsAllowed(key, false));
    }

    [Theory]
    [InlineData("Opportunity.Api", "opportunity-api")]
    [InlineData("Opportunity.Worker.Indexing", "opportunity-worker-indexing")]
    [InlineData("", "opportunity")]
    public void Service_name_derives_from_the_application_name(string application, string expected)
    {
        OpportunityTelemetryRegistration.ServiceNameFor(application).Should().Be(expected);
    }
}
