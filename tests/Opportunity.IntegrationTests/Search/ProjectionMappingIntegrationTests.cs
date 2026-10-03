using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Projection;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T02 against a real OpenSearch: the generation-2 template's analysis (phrases, wildcards, W/n proximity, case and
/// accent folding), strictness, the 2,000-field limit and external-versioned writes of builder output.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ProjectionMappingIntegrationTests(OpenSearchFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Index(IndexHarness harness, Placement placement) : IAsyncDisposable
    {
        public IndexHarness Harness => harness;

        public Placement Placement => placement;

        public IndexTarget Target => placement.WriteTargets[0];

        public async Task<HttpResponseMessage> WriteAsync(ProjectionWrite write)
        {
            var query = new StringBuilder("?refresh=true");
            if (write.Version is { } v)
            {
                query.Append($"&version={v}&version_type=external");
            }

            if (Target.Routing is { } r)
            {
                query.Append($"&routing={r}");
            }

            var path = $"{Target.Index}/_doc/{write.Id}{query}";
            return write.Kind == ProjectionWriteKind.Index
                ? await harness.Http.PutAsync(path, new StringContent(write.Body!.ToJsonString(), Encoding.UTF8, "application/json"), Ct)
                : await harness.Http.DeleteAsync(path, Ct);
        }

        public async Task<HttpResponseMessage> RawAsync(string id, object body) =>
            await harness.RawIndexAsync(Target.Index, id, body, Target.Routing);

        /// <summary>Ids matching <paramref name="query"/> inside the injected workspace filter.</summary>
        public async Task<string[]> SearchAsync(object query, object? sort = null)
        {
            var routing = placement.Read.Routing is { } r ? $"?routing={r}" : string.Empty;
            var request = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["bool"] = new JsonObject
                    {
                        ["filter"] = new JsonArray(new JsonObject { ["term"] = new JsonObject { ["workspaceId"] = placement.WorkspaceFilterValue } }),
                        ["must"] = new JsonArray(JsonSerializer.SerializeToNode(query)),
                    },
                },
                ["sort"] = JsonSerializer.SerializeToNode(sort ?? new object[] { new { documentId = "asc" } }),
                ["size"] = 100,
            };
            using var response = await harness.Http.PostAsync(
                $"{placement.Read.Index}/_search{routing}", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(text);
            using var body = JsonDocument.Parse(text);
            return [.. body.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("_id").GetString()!)];
        }

        public async Task<string[]> AnalyzeAsync(object request)
        {
            using var response = await harness.Http.PostAsJsonAsync($"{Target.Index}/_analyze", request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(text);
            using var body = JsonDocument.Parse(text);
            return [.. body.RootElement.GetProperty("tokens").EnumerateArray().Select(t => t.GetProperty("token").GetString()!)];
        }

        public ValueTask DisposeAsync() => harness.DisposeAsync();
    }

    private async Task<Index> CreateAsync(bool dedicated = false)
    {
        var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();
        var placement = await harness.Manager.PlaceAsync(ws, new WorkspacePlacementRequest(DedicatedIndex: dedicated), Ct);
        placement.Generation.Should().Be(CandidateAProjectionBuilder.ProjectionGeneration);
        return new Index(harness, placement);
    }

    private static readonly CandidateAProjectionBuilder Builder = new();

    private static FieldCatalog Catalog(Guid ws) => new(
        [
            .. SystemFields.Create(ws),
            Field(ws, 1000, FieldType.Keyword, FieldStorage.Metadata, "kw.s001", multi: true),
            Field(ws, 1001, FieldType.Text, FieldStorage.Metadata, "txt.s001"),
            Field(ws, 1002, FieldType.Text, FieldStorage.Metadata, "idt.s001", analysis: TextAnalysis.Identifier),
            Field(ws, 1003, FieldType.Date, FieldStorage.Metadata, "dt.s001", precision: DatePrecision.Date),
            Field(ws, 1004, FieldType.Keyword, FieldStorage.Metadata, FieldRules.OverflowSlot, multi: true),
            Field(ws, 1020, FieldType.MultiChoice, FieldStorage.Coding, "ch.s001", multi: true),
            Field(ws, 1021, FieldType.Boolean, FieldStorage.Coding, "bool.s001"),
            Field(ws, 1022, FieldType.Decimal, FieldStorage.Coding, FieldRules.OverflowSlot),
        ],
        []);

    private static FieldDefinition Field(
        Guid ws, int id, FieldType type, FieldStorage storage, string slot, bool multi = false,
        TextAnalysis? analysis = null, DatePrecision? precision = null) => new()
        {
            WorkspaceId = ws,
            FieldId = id,
            Name = "F" + id,
            Type = type,
            Storage = storage,
            IsMultiValue = multi,
            TextAnalysis = type == FieldType.Text ? analysis ?? TextAnalysis.Prose : null,
            DatePrecision = precision,
            IsSearchable = true,
            SearchSlot = slot,
            Capabilities = FieldRules.CapabilitiesForSlot(slot),
        };

    private static ProjectionDocument Build(
        Placement placement, string controlNumber, string text, long version = 1, string metadata = "{}",
        Dictionary<int, JsonNode>? coding = null, string? fileName = null)
    {
        var document = Document.Create(placement.WorkspaceId, controlNumber, caseSensitive: false);
        document.FileName = fileName;
        document.Metadata = metadata;
        var source = new ProjectionSource
        {
            WorkspaceId = placement.WorkspaceId,
            DocumentId = document.DocumentId,
            State = ProjectionSourceState.Live,
            DocumentVersion = version,
            Document = document,
            Coding = coding ?? [],
        };
        return Builder.Build(source, Catalog(placement.WorkspaceId), IndexedText.Cap(text, 10_000));
    }

    private static async Task WriteOkAsync(Index index, ProjectionDocument document)
    {
        foreach (var write in document.Writes)
        {
            using var response = await index.WriteAsync(write);
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Analyzers_fold_case_and_accents_and_split_identifiers()
    {
        await using var index = await CreateAsync();

        (await index.AnalyzeAsync(new { analyzer = "opp_text", text = "Café RÉSUMÉ" })).Should().BeEquivalentTo("café", "cafe", "résumé", "resume");
        (await index.AnalyzeAsync(new { analyzer = "opp_text_search", text = "Café RÉSUMÉ" })).Should().Equal("cafe", "resume");
        (await index.AnalyzeAsync(new { analyzer = "opp_ident", text = "john.smith@acme.com" })).Should().Equal("john", "smith", "acme", "com");
        (await index.AnalyzeAsync(new { analyzer = "opp_ident", text = "Q3_Budget-final.xlsx" })).Should().Equal("q3", "budget", "final", "xlsx");
        (await index.AnalyzeAsync(new { normalizer = "opp_kw", text = "ÉCOLE-Ø17" })).Should().Equal("ecole-o17");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_language_primitives_work_on_projected_documents(bool dedicated)
    {
        await using var index = await CreateAsync(dedicated);
        var p = index.Placement;
        var memo = Build(p, "ABC10", "The Quarterly results were discussed at the Café board meeting.",
            metadata: """{"f1000": ["Smith, John"], "f1001": "Pricing strategy", "f1002": "john.smith@acme.com", "f1003": "2025-03-01", "f1004": ["VND-17", "VND-18"]}""",
            coding: new() { [1020] = new JsonArray(3, 5), [1021] = JsonValue.Create(true), [1022] = JsonValue.Create(1.25m) },
            fileName: "Q3_Budget-final.xlsx");
        var other = Build(p, "abc9", "Results of the quarterly cafe review were not discussed.", fileName: "notes.docx");
        await WriteOkAsync(index, memo);
        await WriteOkAsync(index, other);
        var m = memo.DocumentId.ToString("D");
        var o = other.DocumentId.ToString("D");

        // Case and accent folding in both directions.
        (await index.SearchAsync(new { match = new { text = "CAFE" } })).Should().BeEquivalentTo(m, o);
        (await index.SearchAsync(new { match = new { text = "café" } })).Should().BeEquivalentTo(m, o);

        // Phrases need positions.
        (await index.SearchAsync(new { match_phrase = new { text = "quarterly results" } })).Should().Equal(m);

        // Proximity W/n (unordered, within n words) via span_near.
        var near = new
        {
            span_near = new
            {
                clauses = new object[] { new { span_term = new { text = "results" } }, new { span_term = new { text = "discussed" } } },
                slop = 2,
                in_order = false,
            },
        };
        (await index.SearchAsync(near)).Should().ContainSingle().Which.Should().Be(m, "only the memo has 'results' within 2 words of 'discussed'");

        // Trailing wildcard on text, leading/infix wildcard on fileName.wc, normalized keyword wildcard.
        (await index.SearchAsync(new { wildcard = new { text = new { value = "quarter*" } } })).Should().BeEquivalentTo(m, o);
        (await index.SearchAsync(new { wildcard = new Dictionary<string, object> { ["fileName.wc"] = new { value = "*budget*", case_insensitive = true } } }))
            .Should().Equal(m);
        (await index.SearchAsync(new { wildcard = new { controlNumber = new { value = "abc1*" } } })).Should().Equal(m);
        (await index.SearchAsync(new { match = new { fileName = "budget" } })).Should().Equal(m);

        // Metadata and coding slots by type.
        (await index.SearchAsync(new { term = new Dictionary<string, object> { ["metadata.kw.s001"] = "smith, john" } })).Should().Equal(m);
        (await index.SearchAsync(new { match = new Dictionary<string, object> { ["metadata.idt.s001"] = "acme" } })).Should().Equal(m);
        (await index.SearchAsync(new { range = new Dictionary<string, object> { ["metadata.dt.s001"] = new { gte = "2025-03-01", lte = "2025-03-01" } } }))
            .Should().Equal(m);
        (await index.SearchAsync(new { term = new Dictionary<string, object> { ["coding.ch.s001"] = "5" } })).Should().Equal(m);
        (await index.SearchAsync(new { term = new Dictionary<string, object> { ["coding.bool.s001"] = true } })).Should().Equal(m);
        (await index.SearchAsync(new { term = new Dictionary<string, object> { ["metadataOverflow.f1004"] = "VND-18" } })).Should().Equal(m);
        (await index.SearchAsync(new { term = new Dictionary<string, object> { ["codingOverflow.f1022"] = "1.25" } })).Should().Equal(m);

        // Natural sort on the sort key: ABC9 before ABC10.
        (await index.SearchAsync(new { match_all = new { } }, new object[] { new { controlNumberSort = "asc" } })).Should().Equal(o, m);

        // Text is searchable but never returned in _source.
        using var get = await index.Harness.Http.GetAsync(
            $"{index.Target.Index}/_doc/{m}" + (index.Target.Routing is { } r ? $"?routing={r}" : string.Empty), Ct);
        using var source = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct));
        source.RootElement.GetProperty("_source").TryGetProperty("text", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_strict_mapping_rejects_unknown_fields_and_wrongly_typed_values()
    {
        await using var index = await CreateAsync();
        var ws = index.Placement.WorkspaceFilterValue;

        async Task RejectedAsync(string id, object body, string error)
        {
            using var response = await index.RawAsync(id, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(error);
        }

        await RejectedAsync("u1", new { workspaceId = ws, custodian = "Smith" }, "strict_dynamic_mapping_exception");
        await RejectedAsync("u2", new { workspaceId = ws, metadata = new { kw = new { s999 = "x" } } }, "strict_dynamic_mapping_exception");
        await RejectedAsync("u3", new { workspaceId = ws, metadata = new { cf = new { s001 = "x" } } }, "strict_dynamic_mapping_exception");
        await RejectedAsync("u4", new { workspaceId = ws, coding = new { ch = new { s101 = "1" } } }, "strict_dynamic_mapping_exception");
        await RejectedAsync("u5", new { workspaceId = ws, metadata = new { @int = new { s001 = "forty-two" } } }, "mapper_parsing_exception");
        await RejectedAsync("u6", new { workspaceId = ws, metadata = new { dt = new { s001 = "03/01/2025" } } }, "mapper_parsing_exception");

        // Custom fields go to typed containers; overflow keys are free-form inside the flat_object only.
        using var ok = await index.RawAsync("ok", new { workspaceId = ws, metadata = new { @int = new { s001 = 42 } }, metadataOverflow = new { f1234 = "x" } });
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task External_versions_reject_stale_writes_and_tombstones_block_resurrection()
    {
        await using var index = await CreateAsync();
        var v3 = Build(index.Placement, "DOC1", "third", version: 3);
        var id = v3.DocumentId;
        await WriteOkAsync(index, v3);

        var stale = v3.Writes[0] with { Version = 2 };
        using (var response = await index.WriteAsync(stale))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a stale write is a no-op success (ADR-001 §3)");
        }

        using (var delete = await index.WriteAsync(new ProjectionWrite(id.ToString("D"), ProjectionWriteKind.Delete, 4, null)))
        {
            delete.IsSuccessStatusCode.Should().BeTrue();
        }

        using (var late = await index.WriteAsync(v3.Writes[0]))
        {
            late.StatusCode.Should().Be(HttpStatusCode.Conflict, "the tombstone (v4) outlives the delayed v3 write");
        }

        (await index.SearchAsync(new { match_all = new { } })).Should().BeEmpty();
        var settings = await index.Harness.GetJsonAsync($"{index.Target.Index}/_settings");
        settings.EnumerateObject().Single().Value.GetProperty("settings").GetProperty("index").GetProperty("gc_deletes").GetString()
            .Should().Be("10m");
    }

    [Fact]
    public async Task The_mapping_fits_the_2000_field_limit_with_every_slot_filled()
    {
        await using var index = await CreateAsync();
        var settings = await index.Harness.GetJsonAsync($"{index.Target.Index}/_settings");
        settings.EnumerateObject().Single().Value.GetProperty("settings").GetProperty("index").GetProperty("mapping")
            .GetProperty("total_fields").GetProperty("limit").GetString().Should().Be("2000");

        // A document that fills every slot of every kind in both containers indexes without adding fields.
        var body = new JsonObject { ["workspaceId"] = index.Placement.WorkspaceFilterValue, ["documentId"] = "full" };
        foreach (var (container, budgets) in new[] { ("metadata", FieldRules.SlotBudgets), ("coding", FieldRules.CodingSlotBudgets) })
        {
            var kinds = new JsonObject();
            foreach (var (kind, budget) in budgets)
            {
                var slots = new JsonObject();
                for (var n = 1; n <= budget; n++)
                {
                    slots[$"s{n:D3}"] = kind switch
                    {
                        "int" => JsonValue.Create(n),
                        "dec" => JsonValue.Create(n + 0.5),
                        "dt" => JsonValue.Create("2025-03-01T00:00:00Z"),
                        "bool" => JsonValue.Create(true),
                        _ => JsonValue.Create("value " + n),
                    };
                }

                kinds[kind] = slots;
            }

            body[container] = kinds;
        }

        using (var response = await index.RawAsync("full", body))
        {
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        }

        // The same mapping in an index limited to 1,000 fields is refused: the limit is real and the mapping sits
        // between 1,000 and 2,000 fields.
        var mapping = ProjectionMappings.Embedded.Load(2);
        var tooSmall = new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["analysis"] = mapping["settings"]!["analysis"]!.DeepClone(),
                ["index"] = new JsonObject { ["mapping"] = new JsonObject { ["total_fields"] = new JsonObject { ["limit"] = 1000 } } },
            },
            ["mappings"] = mapping["mappings"]!.DeepClone(),
        };
        using var refused = await index.Harness.Http.PutAsync(
            $"{index.Harness.Scope.Prefix}-limit-check", new StringContent(tooSmall.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Limit of total fields [1000]");
    }
}
