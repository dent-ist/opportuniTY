using AwesomeAssertions;

using Opportunity.Application.Documents.Dedupe;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Documents;

/// <summary>E09-T04: computed group ids, the policy's job parameters and the custodian field rule.</summary>
public class DedupePolicyTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a8a0-0000-7000-8000-00000000aaaa");
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Group_ids_are_deterministic_and_differ_by_hash_kind_scope_and_custodian()
    {
        var ids = new[]
        {
            RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.Sha256Native, Hash),
            RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.Md5, Hash),
            RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.Sha1, Hash),
            RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.Sha256Native, Hash, "Smith"),
            RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.Sha256Native, Hash, "Jones"),
            RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.Md5, Hash, "Smith"),
        };
        ids.Should().OnlyHaveUniqueItems();
        RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.Sha256Native, Hash, "Smith").Should().Be(ids[3]);

        var upstream = () => RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.UpstreamGroup, "DG-1", "Smith");
        upstream.Should().Throw<ArgumentException>();
        var colon = () => RelationshipIds.CustodialDuplicateGroup(Workspace, DuplicateHashKind.Md5, "ab:cd", "Smith");
        colon.Should().Throw<ArgumentException>("the hash must end at the first colon");
    }

    [Theory]
    [InlineData(true, DedupeHashSource.Auto, DuplicateGroupScope.Global, null)]
    [InlineData(true, DedupeHashSource.Sha1, DuplicateGroupScope.Custodial, 1001)]
    [InlineData(false, DedupeHashSource.UpstreamHash, DuplicateGroupScope.Global, 7)]
    public void A_policy_round_trips_through_its_job_parameters(bool enabled, DedupeHashSource source, DuplicateGroupScope scope, int? field)
    {
        var policy = new DedupePolicy(enabled, source, scope, field);
        DedupePolicy.Parse(policy.ToJson()).Should().Be(policy);
    }

    [Fact]
    public void Other_job_parameters_are_not_a_policy()
    {
        var bulk = () => DedupePolicy.Parse(new System.Text.Json.Nodes.JsonObject { ["operations"] = new System.Text.Json.Nodes.JsonArray() });
        bulk.Should().Throw<FormatException>();
        var json = new DedupePolicy(true).ToJson();
        json["hashSource"] = "Crc32";
        var unknown = () => DedupePolicy.Parse(json);
        unknown.Should().Throw<FormatException>();
    }

    [Fact]
    public void The_custodian_field_is_the_named_or_the_workspace_custodian_field_and_must_hold_one_value()
    {
        static FieldDefinition Field(int id, string name, FieldType type, FieldStorage storage = FieldStorage.Metadata, bool multi = false) => new()
        {
            WorkspaceId = Workspace,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = storage,
            IsMultiValue = multi,
        };

        var catalog = new FieldCatalog(
            [.. SystemFields.Create(Workspace), Field(1001, "Custodian", FieldType.Keyword), Field(1002, "Custodians", FieldType.Keyword, multi: true),
             Field(1003, "Owner", FieldType.Text)],
            []);
        DedupeService.CustodianField(catalog, null).Field!.FieldId.Should().Be(1001);
        DedupeService.CustodianField(catalog, 1003).Field!.FieldId.Should().Be(1003);
        DedupeService.CustodianField(catalog, 1002).Error.Should().Contain("single-value");
        DedupeService.CustodianField(catalog, SystemFields.ControlNumber).Error.Should().NotBeNull("a column field is not a custodian");
        DedupeService.CustodianField(catalog, 4242).Error.Should().Contain("does not exist");
        DedupeService.CustodianField(new FieldCatalog(SystemFields.Create(Workspace), []), null).Error.Should().Contain("no Custodian field");
    }
}
