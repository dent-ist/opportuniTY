using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Fields;
using Opportunity.Data.Search;
using Opportunity.Data.Security;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search;
using Opportunity.Security.Authorization;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// #193: the review grid's first page on a real import (DAT → import job → chunk index worker → search service), not
/// on the e2e mock: derived Document Date and File Type, family markers, the default sort and exact, current totals.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ImportedReviewGridTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Rows deliberately out of control-number order. OPP0002/OPP0003 are attachments of OPP0001 (Parent ID).
    private static readonly string[][] Rows =
    [
        ["ControlNumber", "BegAttach", "EndAttach", "ParentID", "DateSent", "DateReceived", "FileName", "FileExtension", "DateCreated", "DateLastModified"],
        ["OPP0003", "OPP0001", "OPP0003", "OPP0001", "", "", "Of_but.xlsx", "xlsx", "2018-07-31T14:29:29-05:00", ""],
        ["OPP0001", "OPP0001", "OPP0003", "", "2018-11-16T09:55:55-05:00", "2018-11-17T00:01:54+09:00", "Crair kaklas are.msg", "msg", "", ""],
        ["OPP0004", "", "", "", "", "2019-01-02T08:00:00Z", "Notice.pdf", "pdf", "", ""],
        ["OPP0002", "OPP0001", "OPP0003", "OPP0001", "", "", "Vun feakacrio.html", "html", "2018-09-24T11:15:59-05:00", "2018-10-06T22:42:02-05:00"],
    ];

    [Fact]
    public async Task An_imported_dat_shows_dates_file_types_family_markers_in_control_number_order_with_an_exact_current_total()
    {
        await using var h = await ChunkIndexHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Import.WorkspaceAsync();
        var batch = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(Rows)));
        (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var ids = (await h.Import.DocumentsAsync(ws)).ToDictionary(d => d.Key, d => d.Value.DocumentId);

        // Families are rebuilt by E09-T01 (#84); until then the relationship is written directly, before indexing.
        await h.Import.Db.ExecuteAsync(
            """
            UPDATE opportunity.document
            SET family_id = @parent, parent_document_id = @parent, family_status = 1,
                family_sequence = CASE control_number WHEN 'OPP0002' THEN 1 ELSE 2 END
            WHERE workspace_id = @ws AND control_number IN ('OPP0002', 'OPP0003')
            """,
            ("ws", ws), ("parent", ids["OPP0001"]));
        await h.DeliverAllAsync(ws, batch.JobId);
        (await h.CountAsync(ws)).Should().Be(4);
        (await h.TickWatermarkAsync(ws)).IsCurrent.Should().BeTrue();

        await using var search = SearchServices(h);
        var reviewer = await MemberAsync(h, ws);

        var first = await SearchAsync(search, ws, reviewer, new SearchRequest("", PageSize: 2));
        first.Items.Select(i => i.ControlNumber).Should().Equal("OPP0001", "OPP0002");
        first.Total.Should().Be(new TotalCount(4, TotalRelation.Eq));
        first.Freshness.Current.Should().BeTrue();
        first.Freshness.ServedGeneration.Should().NotBeNull();
        first.Freshness.State.Should().Be(SearchFreshnessState.Current);
        first.Freshness.PendingChanges.Should().Be(0);

        var parent = first.Items[0];
        parent.DocumentDate.Should().Be(DateTimeOffset.Parse("2018-11-16T14:55:55Z", System.Globalization.CultureInfo.InvariantCulture));
        parent.FileType.Should().Be("Email");
        parent.IsFamilyParent.Should().BeTrue();
        parent.ParentDocumentId.Should().BeNull();

        var html = first.Items[1];
        html.DocumentDate.Should().Be(DateTimeOffset.Parse("2018-10-07T03:42:02Z", System.Globalization.CultureInfo.InvariantCulture), "DateLastModified precedes DateCreated (ADR-009 R25)");
        html.FileType.Should().Be("Web Page");
        html.ParentDocumentId.Should().Be(ids["OPP0001"].ToString("D"));
        html.IsFamilyParent.Should().BeFalse();

        var second = await PageAsync(search, ws, reviewer, first.SearchId!, new SearchPageRequest(Cursor: first.NextCursor));
        second.Items.Select(i => i.ControlNumber).Should().Equal("OPP0003", "OPP0004");
        second.Freshness.Current.Should().BeTrue();
        second.Items[0].FileType.Should().Be("Spreadsheet");
        second.Items[0].DocumentDate.Should().Be(DateTimeOffset.Parse("2018-07-31T19:29:29Z", System.Globalization.CultureInfo.InvariantCulture));
        second.Items[0].ParentDocumentId.Should().Be(ids["OPP0001"].ToString("D"));
        second.Items[1].FileType.Should().Be("PDF");
        second.Items[1].DocumentDate.Should().Be(DateTimeOffset.Parse("2019-01-02T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        second.Items[1].ParentDocumentId.Should().BeNull();
        second.Items[1].IsFamilyParent.Should().BeFalse("a standalone document is not a parent");

        // An explicit sort wins over the default.
        var byDate = await SearchAsync(search, ws, reviewer,
            new SearchRequest("", [new SearchSortKey("documentDate", SearchSortDirection.Desc)], PageSize: 10));
        byDate.Items.Select(i => i.ControlNumber).Should().Equal("OPP0004", "OPP0001", "OPP0002", "OPP0003");

        // A change committed after the reader opened, not yet indexed: neither that search nor a new one is current.
        var more = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat([["ControlNumber", "FileExtension"], ["OPP0005", "txt"]])),
            name: "more.dat");
        await h.Import.RunAsync(more);
        var back = await PageAsync(search, ws, reviewer, first.SearchId!, new SearchPageRequest(Cursor: second.PreviousCursor));
        back.Freshness.Current.Should().BeFalse();
        var stale = await SearchAsync(search, ws, reviewer, new SearchRequest("", PageSize: 10));
        stale.Freshness.Current.Should().BeFalse();
        stale.Freshness.State.Should().Be(SearchFreshnessState.Updating);
        stale.Freshness.PendingChanges.Should().BeGreaterThan(0);
        stale.Items.Should().HaveCount(4);
    }

    private static ServiceProvider SearchServices(ChunkIndexHarness h)
    {
        var db = h.Import.Db;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
        services.AddSingleton(h.OpenSearchOptions);
        services.AddSingleton<ISecurityStateReader>(new PostgresSecurityStateReader(db.AppDataSource));
        services.AddSingleton<IIndexPlacementStore>(new IndexPlacementStore(db.AppDataSource));
        services.AddSingleton<ISearchSessionStore>(new SearchSessionStore(db.AppDataSource));
        services.AddSingleton<ISearchFreshnessReader>(new SearchWatermarkStore(db.AppDataSource));
        services.AddSingleton<IFieldCatalogRepository>(new FieldCatalogRepository(db.AppDataSource));
        services.AddOpportunityAuthorization();
        services.AddOpenSearchSearchService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<Guid> MemberAsync(ChunkIndexHarness h, Guid ws)
    {
        var user = Guid.CreateVersion7();
        await h.Import.Db.ExecuteAsync(
            """
            INSERT INTO opportunity.app_user (user_id, issuer, subject, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, 'https://idp.test', @id::text, '{}', now(), now())
            """,
            ("id", user));
        await h.Import.Db.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, @role, @user)",
            ("ws", ws), ("id", Guid.CreateVersion7()), ("role", WorkspaceRole.Reviewer.Key()), ("user", user));
        return user;
    }

    private static async Task<SearchResultPage> SearchAsync(ServiceProvider services, Guid ws, Guid user, SearchRequest request)
    {
        await using var scope = services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISearchService>()
            .SearchAsync(SearchHarness.Caller(ws, user), request with { Highlight = false }, Ct);
        outcome.Page.Should().NotBeNull(outcome.Status.ToString());
        return outcome.Page!;
    }

    private static async Task<SearchResultPage> PageAsync(ServiceProvider services, Guid ws, Guid user, string searchId, SearchPageRequest page)
    {
        await using var scope = services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISearchService>().GetPageAsync(SearchHarness.Caller(ws, user), searchId, page, Ct);
        outcome.Page.Should().NotBeNull(outcome.Status.ToString());
        return outcome.Page!;
    }
}
