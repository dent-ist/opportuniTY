using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Application.Search;
using Opportunity.Application.Search.SavedSearches;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.Search;

/// <summary>E07-T09 nested saved searches: reference syntax, inlining, cycles and depth (store faked).</summary>
public sealed class SavedSearchExpansionTests
{
    private static readonly Guid Ws = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task References_are_inlined_with_the_span_of_the_reference_and_the_rest_untouched()
    {
        var store = new FakeStore();
        var inner = store.Add("Inner", "apple OR pear");
        var outer = store.Add("Outer", $"savedsearch:{inner} AND fruit");

        var expansion = await Expand(store, $"basket AND savedsearch:{outer:N}");

        expansion.Success.Should().BeTrue();
        expansion.DirectReferences.Should().Equal(outer);
        QueryPrinter.Print(expansion.Ast).Should().Be("basket AND ((apple OR pear) AND fruit)");
        var referenceSpan = new SourceSpan("basket AND ".Length, $"basket AND savedsearch:{outer:N}".Length);
        ((AndNode)expansion.Ast).Children[1].Span.Should().Be(referenceSpan);
        ((AndNode)((AndNode)expansion.Ast).Children[1]).Children[0].Span.Should().Be(referenceSpan, "every inlined node points at the reference");
    }

    [Fact]
    public async Task A_query_without_references_is_returned_as_is_without_reading_the_store()
    {
        var store = new FakeStore();
        var ast = QueryParser.Parse("contract W/5 termination").Ast!;
        var expansion = await new SavedSearchQueries(store, null!, QueryLimits.Default).ExpandAsync(new SavedSearchExpansionRequest(Ws, ast, null), Ct);
        expansion.Ast.Should().BeSameAs(ast);
        store.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Cycles_through_a_chain_and_back_to_the_search_being_saved_are_rejected()
    {
        var store = new FakeStore();
        var a = Guid.CreateVersion7();
        var b = store.Add("B", $"savedsearch:{a}");
        store.Add("A", $"savedsearch:{b}", a);

        (await Expand(store, $"x OR savedsearch:{a}")).Errors.Select(e => e.Code).Should().Equal(SavedSearchErrorCodes.Cycle);
        (await Expand(store, $"savedsearch:{b}", self: a)).Errors.Select(e => e.Code).Should().Equal(SavedSearchErrorCodes.Cycle);
        (await Expand(store, $"savedsearch:{a}", self: a)).Errors.Select(e => e.Code).Should().Equal(SavedSearchErrorCodes.Cycle);
    }

    [Fact]
    public async Task Nesting_deeper_than_the_limit_is_rejected()
    {
        var store = new FakeStore();
        var id = store.Add("Level 0", "leaf");
        for (var i = 1; i < SavedSearchQuerySyntax.MaxDepth; i++)
        {
            id = store.Add($"Level {i}", $"savedsearch:{id}");
        }

        (await Expand(store, $"savedsearch:{id}")).Success.Should().BeTrue("exactly the maximum depth is allowed");
        var deeper = store.Add("Too deep", $"savedsearch:{id}");
        (await Expand(store, $"savedsearch:{deeper}")).Errors.Select(e => e.Code).Should().Equal(SavedSearchErrorCodes.TooDeep);
    }

    [Theory]
    [InlineData("savedsearch:*")]
    [InlineData("savedsearch:[a TO b]")]
    [InlineData("savedsearch:abc*")]
    [InlineData("savedsearch:hot")]
    [InlineData("fileName:(savedsearch:0199a8a0-0000-7000-8000-000000000001)")]
    public void Malformed_or_misplaced_references_are_positioned_errors(string query)
    {
        var parsed = QueryParser.Parse(query);
        if (parsed.Ast is null)
        {
            return; // rejected by the grammar already
        }

        var reference = SavedSearchReferences.Collect(parsed.Ast).Should().ContainSingle().Subject;
        reference.Error!.Code.Should().Be(SavedSearchErrorCodes.InvalidReference);
        reference.Error.Span.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Missing_and_unparsable_nested_searches_name_the_search_that_uses_them()
    {
        var store = new FakeStore();
        var broken = store.Add("Broken", "apple AND (");
        var usesBroken = store.Add("Uses broken", $"savedsearch:{broken}");
        var usesMissing = store.Add("Uses missing", $"savedsearch:{Guid.CreateVersion7()}");

        var invalid = (await Expand(store, $"savedsearch:{usesBroken}")).Errors.Should().ContainSingle().Subject;
        invalid.Code.Should().Be(SavedSearchErrorCodes.Invalid);
        invalid.Message.Should().Contain("'Broken'");
        var missing = (await Expand(store, $"savedsearch:{usesMissing}")).Errors.Should().ContainSingle().Subject;
        missing.Code.Should().Be(SavedSearchErrorCodes.NotFound);
        missing.Message.Should().Contain("'Uses missing'");
        (await Expand(store, $"savedsearch:{Guid.CreateVersion7()}")).Errors.Select(e => e.Code).Should().Equal(SavedSearchErrorCodes.NotFound);
    }

    [Fact]
    public void Binding_errors_inside_a_reference_point_at_the_whole_reference_and_name_the_search()
    {
        var span = new SourceSpan(4, 40);
        var expansion = new SavedSearchExpansion(new MatchAllNode(span), [], []);
        var errors = new[] { new QueryDiagnostic("UNKNOWN_FIELD", "Unknown field 'custodian'.", new SourceSpan(4, 13), []) };
        expansion.Annotate(errors).Should().Equal(errors, "without references nothing changes");

        var withNames = expansion with { ReferenceNames = new Dictionary<SourceSpan, string> { [span] = "Smith" } };
        var annotated = withNames.Annotate(errors).Should().ContainSingle().Subject;
        annotated.Span.Should().Be(span);
        annotated.Message.Should().Be("In saved search 'Smith': Unknown field 'custodian'.");
    }

    [Fact]
    public void The_shared_action_is_part_of_the_closed_audit_taxonomy()
    {
        AuditTaxonomy.IsDefined(AuditTaxonomy.SavedSearch.Category, AuditTaxonomy.SavedSearch.Shared).Should().BeTrue();
        AuditTaxonomy.IsDefined(AuditTaxonomy.SavedSearch.Category, AuditTaxonomy.SavedSearch.Created).Should().BeTrue();
    }

    private static Task<SavedSearchExpansion> Expand(FakeStore store, string query, Guid? self = null) =>
        new SavedSearchQueries(store, null!, QueryLimits.Default)
            .ExpandAsync(new SavedSearchExpansionRequest(Ws, QueryParser.Parse(query).Ast!, null, self), Ct);

    private sealed class FakeStore : ISavedSearchStore
    {
        private readonly Dictionary<Guid, SavedSearchCriteria> _searches = [];

        public int Reads { get; private set; }

        public Guid Add(string name, string query, Guid? id = null)
        {
            var key = id ?? Guid.CreateVersion7();
            _searches[key] = new SavedSearchCriteria(key, name, query, []);
            return key;
        }

        public Task<IReadOnlyDictionary<Guid, SavedSearchCriteria>> GetCriteriaAsync(
            Guid workspaceId, IReadOnlyCollection<Guid> savedSearchIds, SavedSearchViewer? viewer, CancellationToken cancellationToken = default)
        {
            Reads++;
            IReadOnlyDictionary<Guid, SavedSearchCriteria> found = savedSearchIds.Where(_searches.ContainsKey).ToDictionary(id => id, id => _searches[id]);
            return Task.FromResult(found);
        }

        public Task<SavedSearchRecord?> GetAsync(Guid workspaceId, Guid savedSearchId, SavedSearchViewer? viewer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchPage> ListAsync(Guid workspaceId, SavedSearchViewer viewer, SavedSearchListFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> CreateAsync(
            Guid workspaceId, Guid savedSearchId, Guid ownerId, SavedSearchDefinition definition, AuditEvent audit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> UpdateAsync(
            Guid workspaceId, Guid savedSearchId, long? expectedVersion, SavedSearchDefinition definition, AuditEvent audit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SavedSearchWriteResult> DeleteAsync(
            Guid workspaceId, Guid savedSearchId, long? expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> SetSharingAsync(
            Guid workspaceId, Guid savedSearchId, long? expectedVersion, IReadOnlyList<SavedSearchShareTarget> shares, AuditEvent audit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordRunAsync(Guid workspaceId, Guid savedSearchId, SavedSearchLastRun run, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SavedSearchShareResource>> ListShareCandidatesAsync(
            Guid workspaceId, string? nameContains, Guid excludeUserId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SavedSearchFolderRecord>> ListFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchFolderRecord?> GetFolderAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> CreateFolderAsync(
            Guid workspaceId, Guid folderId, string name, Guid? parentFolderId, Guid createdBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> UpdateFolderAsync(
            Guid workspaceId, Guid folderId, long? expectedVersion, string name, Guid? parentFolderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SavedSearchWriteResult> DeleteFolderAsync(Guid workspaceId, Guid folderId, long? expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
