using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.Fields;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E07-T02 / ADR-007: the generation-2 projection mapping holds every §23 field with its agreed type, is strict in every
/// container but the two overflows, keeps its slot budgets in step with <see cref="FieldRules"/>, stays inside the
/// 2,000-field limit, and backs every capability the API advertises.
/// </summary>
public sealed class ProjectionMappingTests
{
    private static readonly JsonObject Root = ProjectionMappings.Embedded.Load(2)["mappings"]!.AsObject();
    private static readonly JsonObject Properties = Root["properties"]!.AsObject();
    private static readonly string[] WildcardTypes = ["text", "keyword", "wildcard"];

    public static TheoryData<string, string> AgreedTypes => new()
    {
        // §23 baseline fields.
        { "workspaceId", "keyword" },
        { "documentId", "keyword" },
        { "controlNumber", "keyword" },
        { "familyId", "keyword" },
        { "parentDocumentId", "keyword" },
        { "familySequence", "integer" },
        { "duplicateGroupId", "keyword" },
        { "emailThreadId", "keyword" },
        { "securityTags", "keyword" },
        { "fileName", "text" },
        { "fileType", "keyword" },
        { "mimeType", "keyword" },
        { "documentDate", "date" },
        { "metadata", "object" },
        { "text", "text" },
        { "projectionVersion", "long" },

        // ADR-007 §3 additions.
        { "controlNumberSort", "keyword" },

        // Exact-case sort sub-field kept from generation 1 for the search service (E07-T05) until it sorts on
        // controlNumberSort (natural order, ADR-009 R5).
        { "controlNumber.sort", "keyword" },
        { "begBates", "keyword" },
        { "begBatesSort", "keyword" },
        { "endBates", "keyword" },
        { "endBatesSort", "keyword" },
        { "begAttach", "keyword" },
        { "endAttach", "keyword" },
        { "familyStatus", "keyword" },
        { "isDuplicatePrimary", "boolean" },
        { "fileName.kw", "keyword" },
        { "fileName.wc", "wildcard" },
        { "fileExtension", "keyword" },
        { "fileSize", "long" },
        { "pageCount", "integer" },
        { "familyDate", "date" },
        { "dateSent", "date" },
        { "dateReceived", "date" },
        { "dateCreated", "date" },
        { "dateLastModified", "date" },
        { "md5", "keyword" },
        { "sha1", "keyword" },
        { "sha256", "keyword" },
        { "metadataOverflow", "flat_object" },
        { "coding", "object" },
        { "codingOverflow", "flat_object" },
        { "textTruncated", "boolean" },
        { "textMissing", "boolean" },
        { "nativeMissing", "boolean" },
        { "imagesIncomplete", "boolean" },
        { "textLength", "long" },
    };

    [Theory]
    [MemberData(nameof(AgreedTypes))]
    public void Every_baseline_field_is_mapped_with_its_agreed_type(string path, string type)
    {
        (Resolve(path)?["type"]?.GetValue<string>()).Should().Be(type, path);
    }

    [Fact]
    public void Analysis_follows_adr_007_section_5()
    {
        Resolve("text")!["analyzer"]!.GetValue<string>().Should().Be("opp_text");
        Resolve("text")!["search_analyzer"]!.GetValue<string>().Should().Be("opp_text_search");
        Resolve("text")!["index_options"]!.GetValue<string>().Should().Be("offsets", "phrases and W/n need positions; highlighting reads offsets (R11)");
        Resolve("text")!["store"]!.GetValue<bool>().Should().BeTrue();
        Root["_source"]!["excludes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("text");
        Resolve("fileName")!["analyzer"]!.GetValue<string>().Should().Be("opp_ident");
        Resolve("controlNumber")!["normalizer"]!.GetValue<string>().Should().Be("opp_kw");
        Resolve("controlNumberSort")!["normalizer"].Should().BeNull("the natural sort key is compared ordinally");

        var analysis = ProjectionMappings.Embedded.Load(2)["settings"]!["analysis"]!;
        Filters(analysis["analyzer"]!["opp_text"]!).Should().Equal("lowercase", "opp_folding_preserve");
        analysis["filter"]!["opp_folding_preserve"]!["preserve_original"]!.GetValue<bool>().Should().BeTrue();
        Filters(analysis["analyzer"]!["opp_text_search"]!).Should().Equal("lowercase", "asciifolding");
        Filters(analysis["analyzer"]!["opp_ident"]!).Should().Equal("lowercase", "asciifolding");
        Filters(analysis["normalizer"]!["opp_kw"]!).Should().Equal("lowercase", "asciifolding");
        analysis["analyzer"]!.AsObject().Select(a => a.Key).Should().NotContain(a => a.Contains("stem", StringComparison.Ordinal));

        static IEnumerable<string> Filters(JsonNode node) => node["filter"]!.AsArray().Select(f => f!.GetValue<string>());
    }

    [Fact]
    public void Every_container_is_strict_except_the_overflows()
    {
        Root["dynamic"]!.GetValue<string>().Should().Be("strict");
        foreach (var (path, node) in Walk(Properties, string.Empty).Where(p => p.Node["properties"] is not null))
        {
            (node["dynamic"]?.GetValue<string>()).Should().Be("strict", path);
        }

        Walk(Properties, string.Empty).Where(p => p.Node["type"]?.GetValue<string>() == "flat_object").Select(p => p.Path)
            .Should().BeEquivalentTo("metadataOverflow", "codingOverflow");
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("coding")]
    public void Slot_budgets_match_the_field_rules(string container)
    {
        var budgets = container == "coding" ? FieldRules.CodingSlotBudgets : FieldRules.SlotBudgets;
        var kinds = Properties[container]!["properties"]!.AsObject();
        kinds.Select(k => k.Key).Should().BeEquivalentTo(budgets.Keys);
        foreach (var (kind, budget) in budgets)
        {
            var slots = kinds[kind]!["properties"]!.AsObject();
            slots.Select(s => s.Key).Should().Equal(Enumerable.Range(1, budget).Select(n => $"s{n:D3}"), kind);
            slots.Select(s => s.Value!.ToJsonString()).Distinct().Should().ContainSingle("every slot of {0} is typed alike", kind);
        }

        FieldRules.CodingSlotBudgets.Keys.Should().BeEquivalentTo(FieldRules.SlotBudgets.Keys, "coding reuses the metadata slot kinds");
    }

    [Theory]
    [InlineData("txt", "text", "opp_text")]
    [InlineData("idt", "text", "opp_ident")]
    [InlineData("kw", "keyword", null)]
    [InlineData("int", "long", null)]
    [InlineData("dec", "double", null)]
    [InlineData("dt", "date", null)]
    [InlineData("bool", "boolean", null)]
    [InlineData("ch", "keyword", null)]
    [InlineData("usr", "keyword", null)]
    public void Slot_kinds_have_the_adr_007_types(string kind, string type, string? analyzer)
    {
        foreach (var container in new[] { "metadata", "coding" })
        {
            var slot = Resolve($"{container}.{kind}.s001")!;
            slot["type"]!.GetValue<string>().Should().Be(type);
            (slot["analyzer"]?.GetValue<string>()).Should().Be(analyzer);
            if (type == "text")
            {
                slot["fields"]!["kw"]!["ignore_above"]!.GetValue<int>().Should().Be(256);
            }
        }

        Resolve("metadata.kw.s001")!["normalizer"]!.GetValue<string>().Should().Be("opp_kw");
        Resolve("metadata.kw.s001")!["ignore_above"]!.GetValue<int>().Should().Be(FieldLimits.MaxKeywordLength);
    }

    [Fact]
    public void The_mapping_stays_well_inside_the_2000_field_limit()
    {
        // OpenSearch counts every object, leaf and multi-field; flat_object adds two internal subfields each.
        var count = CountFields(Properties) + 2 * 2;
        count.Should().BeLessThan(2000).And.BeGreaterThan(1500);
        new Opportunity.Search.OpenSearchOptions().TotalFieldsLimit.Should().Be(2000);
    }

    [Fact]
    public void The_builder_writes_the_current_generation()
    {
        ProjectionMappings.Embedded.CurrentGeneration.Should().Be(2);
        new CandidateAProjectionBuilder().Generation.Should().Be(ProjectionMappings.Embedded.CurrentGeneration);
        ProjectionMappings.Embedded.Generations.Should().Contain(1, "generation 1 stays loadable until its indexes are rebuilt");
    }

    [Fact]
    public void Every_field_path_exists_in_the_mapping()
    {
        foreach (var (id, path) in ProjectionFieldPaths.Structural)
        {
            Resolve(path).Should().NotBeNull("system field {0} maps to {1}", id, path);
        }

        foreach (var storage in new[] { FieldStorage.Metadata, FieldStorage.Coding })
        {
            foreach (var (kind, budget) in FieldRules.SlotBudgetsFor(storage))
            {
                for (var n = 1; n <= budget; n++)
                {
                    var path = ProjectionFieldPaths.For(new FieldDefinition
                    {
                        FieldId = 1000,
                        Storage = storage,
                        IsSearchable = true,
                        SearchSlot = FieldRules.Slot(kind, n),
                    })!;
                    Resolve(path).Should().NotBeNull(path);
                }
            }

            var overflow = ProjectionFieldPaths.For(new FieldDefinition
            {
                FieldId = 1017,
                Storage = storage,
                IsSearchable = true,
                SearchSlot = FieldRules.OverflowSlot,
            })!;
            Resolve(overflow[..overflow.IndexOf('.', StringComparison.Ordinal)])!["type"]!.GetValue<string>().Should().Be("flat_object");
        }
    }

    /// <summary>ADR-007 verification: "a capability-table test generated from the mapping".</summary>
    [Fact]
    public void Every_advertised_capability_is_backed_by_the_mapping()
    {
        foreach (var kind in FieldRules.SlotBudgets.Keys)
        {
            var slot = FieldRules.Slot(kind, 1);
            AssertBacked(slot, FieldRules.CapabilitiesForSlot(slot), Resolve($"metadata.{slot}")!);
            AssertBacked(slot, FieldRules.CapabilitiesForSlot(slot), Resolve($"coding.{slot}")!);
        }

        foreach (var field in Opportunity.Core.Fields.SystemFields.Create(Guid.NewGuid()))
        {
            // Column fields map to structural properties; Metadata-storage system fields (E09-T02) use reserved slots.
            var path = ProjectionFieldPaths.For(field) ?? throw new InvalidOperationException($"system field {field.FieldId} is not mapped");
            AssertBacked(path, field.Capabilities, Resolve(path)!);
            if (field.Capabilities.HasFlag(FieldCapabilities.Rangeable) && field.Type == FieldType.Keyword)
            {
                Resolve(ProjectionFieldPaths.SortKeyFields[path]).Should().NotBeNull("{0} ranges and sorts on its natural sort key", path);
            }
        }
    }

    private static void AssertBacked(string name, FieldCapabilities capabilities, JsonNode mapping)
    {
        var type = mapping["type"]!.GetValue<string>();
        var keywordSub = mapping["fields"]?["kw"]?["type"]?.GetValue<string>() == "keyword";
        string[] docValues = ["keyword", "long", "integer", "double", "date", "boolean"];
        string[] numeric = ["long", "integer", "double", "date"];

        if (capabilities.HasFlag(FieldCapabilities.FullText) || capabilities.HasFlag(FieldCapabilities.Highlightable))
        {
            type.Should().Be("text", name);
        }

        if (capabilities.HasFlag(FieldCapabilities.Sortable))
        {
            (docValues.Contains(type) || keywordSub).Should().BeTrue("{0} is sortable", name);
        }

        if (capabilities.HasFlag(FieldCapabilities.Aggregatable))
        {
            docValues.Should().Contain(type, "{0} is aggregatable", name);
        }

        if (capabilities.HasFlag(FieldCapabilities.Rangeable))
        {
            (numeric.Contains(type) || type == "keyword").Should().BeTrue("{0} is rangeable", name);
        }

        if (capabilities.HasFlag(FieldCapabilities.Wildcard))
        {
            WildcardTypes.Should().Contain(type, "{0} takes trailing wildcards", name);
        }

        if (capabilities.HasFlag(FieldCapabilities.LeadingWildcard))
        {
            (mapping["fields"]?["wc"]?["type"]?.GetValue<string>()).Should().Be("wildcard", "{0} takes leading wildcards", name);
        }
    }

    internal static JsonNode? Resolve(string path)
    {
        JsonNode? node = null;
        var properties = Properties;
        foreach (var part in path.Split('.'))
        {
            node = properties?[part] ?? node?["fields"]?[part];
            if (node is null)
            {
                return null;
            }

            properties = node["properties"] as JsonObject;
        }

        return node;
    }

    private static IEnumerable<(string Path, JsonNode Node)> Walk(JsonObject properties, string prefix)
    {
        foreach (var (name, node) in properties)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            yield return (path, node!);
            if (node!["properties"] is JsonObject inner)
            {
                foreach (var child in Walk(inner, path))
                {
                    yield return child;
                }
            }
        }
    }

    private static int CountFields(JsonObject properties) => properties.Sum(p =>
        1 + (p.Value!["properties"] is JsonObject inner ? CountFields(inner) : 0)
          + (p.Value!["fields"] is JsonObject fields ? fields.Count : 0));
}
