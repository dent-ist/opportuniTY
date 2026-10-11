using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Contracts.Messaging;
using Opportunity.Data.Migrations;
using Opportunity.Release;
using Opportunity.Search.Indexing;
using Opportunity.UnitTests.Security;

namespace Opportunity.UnitTests.Release;

/// <summary>E01-T06: the per-release compatibility matrix is built from the code that ships.</summary>
public sealed class CompatibilityMatrixTests
{
    private static readonly IReadOnlyDictionary<string, string> Versions =
        CompatibilityMatrix.ReadVersionsEnv(Path.Combine(PermissionMatrixTests.RepositoryRoot(), "versions.env"));

    [Fact]
    public void Matrix_reports_the_schema_projection_and_message_versions_of_this_build()
    {
        var matrix = CompatibilityMatrix.FromCode("1.4.2", "0123abc", Versions, "v1.3.7");

        matrix.Release.Should().Be("1.4.2");
        matrix.Commit.Should().Be("0123abc");
        matrix.Database.SchemaVersion.Should().Be(MigrationCatalog.LatestVersion);
        matrix.Database.LatestMigration.Should().Be(MigrationCatalog.Scripts[^1].Name);
        matrix.Database.EngineVersion.Should().Be(Versions["POSTGRES"]);
        matrix.Search.ProjectionGeneration.Should().Be(ProjectionMappings.Embedded.CurrentGeneration);
        matrix.Search.MappingGenerations.Select(g => g.Generation).Should().Equal(ProjectionMappings.Embedded.Generations);
        matrix.Search.MappingGenerations.Should().OnlyContain(g => g.Sha256.Length == 64);
        matrix.Messaging.Contracts.Select(c => c.MessageType).Should().BeEquivalentTo(
            MessageContracts.CreateRegistry().Current.Select(c => c.MessageType));
        matrix.Messaging.Contracts.Should().OnlyContain(c => c.ReadsMajors.Contains(SchemaVersion.Parse(c.Writes).Major));
        matrix.Upgrade.SupportedFrom.Should().Be("1.3.7", "the release it was upgrade-tested from");
    }

    [Theory]
    [InlineData("0.0.1", "none")]
    [InlineData("0.3.0", "0.2.x")]
    [InlineData("2.0.0", "the last 1.x release")]
    public void Without_a_tested_previous_release_the_supported_source_is_the_previous_minor(string release, string expected) =>
        CompatibilityMatrix.FromCode(release, null, Versions).Upgrade.SupportedFrom.Should().Be(expected);

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    public void Release_must_be_a_semver_version(string release)
    {
        var act = () => CompatibilityMatrix.FromCode(release, null, Versions);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Json_and_markdown_render_every_component()
    {
        var matrix = CompatibilityMatrix.FromCode("0.1.0", null, Versions);

        using var json = JsonDocument.Parse(matrix.ToJson());
        json.RootElement.GetProperty("format").GetInt32().Should().Be(CompatibilityMatrix.CurrentFormat);
        json.RootElement.GetProperty("database").GetProperty("schemaVersion").GetInt32().Should().Be(MigrationCatalog.LatestVersion);
        json.RootElement.TryGetProperty("commit", out _).Should().BeFalse("unset values are omitted");

        var markdown = matrix.ToMarkdown();
        markdown.Should().Contain($"| {MigrationCatalog.LatestVersion} (`{MigrationCatalog.Scripts[^1].Name}`)");
        markdown.Should().Contain($"generation {ProjectionMappings.Embedded.CurrentGeneration}");
        foreach (var contract in matrix.Messaging.Contracts)
        {
            markdown.Should().Contain($"`{contract.MessageType}`");
        }
    }
}
