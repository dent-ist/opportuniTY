using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Documents;
using Opportunity.Application.Fields;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Search;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Documents;
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
/// #179 (Q-55): an import's Folder Path becomes a read-only folder tree; the Folders tree lists it one level at a time
/// with counts of the documents the caller may see (walled documents neither counted nor revealing a folder), and a
/// search within a folder lists its documents, with or without subfolders.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class DocumentFolderTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[][] Rows =
    [
        ["ControlNumber", "FileName", "FolderPath"],
        ["FLD0001", "one.msg", @"\Mail\Inbox"],
        ["FLD0002", "two.msg", @"\Mail\Inbox\Archive"],
        ["FLD0003", "three.msg", "/mail/INBOX/"],
        ["FLD0004", "four.msg", @"\Mail\Sent"],
        ["FLD0005", "five.msg", @"\Restricted Custodian"],
        ["FLD0006", "six.msg", ""],
        ["FLD0007", "seven.msg", @"\Mail\Sent\Private"],
    ];

    [Fact]
    public async Task Imported_folder_paths_form_a_lazy_tree_whose_counts_and_searches_exclude_walled_documents()
    {
        await using var h = await ChunkIndexHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Import.WorkspaceAsync();
        var batch = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(Rows)));
        (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var ids = (await h.Import.DocumentsAsync(ws)).ToDictionary(d => d.Key, d => d.Value.DocumentId);

        // One folder per distinct path (case-insensitive, first spelling kept) and every folder above it.
        (await h.Import.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_folder WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(6);
        (await h.Import.Db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND folder_id IS NULL", ("ws", ws))).Should().Be(1);

        var reviewer = await MemberAsync(h, ws, WorkspaceRole.Reviewer);
        var admin = await MemberAsync(h, ws, WorkspaceRole.WorkspaceAdmin);
        await WallAsync(h, ws, reviewer, ids["FLD0005"], ids["FLD0007"]);
        await h.DeliverAllAsync(ws, batch.JobId);
        (await h.CountAsync(ws)).Should().Be(7);
        (await h.TickWatermarkAsync(ws)).IsCurrent.Should().BeTrue();

        await using var services = Services(h);

        // Top level: the walled custodian's folder is not listed for the reviewer (Q-52), but is for the admin.
        var top = await ListAsync(services, ws, reviewer, null);
        top.Parent.Should().BeNull();
        top.CountsApproximate.Should().BeFalse();
        top.Items.Select(f => (f.Name, f.DocumentCount, f.TotalCount, f.HasSubfolders)).Should().Equal(("Mail", 0L, 4L, true));
        (await ListAsync(services, ws, admin, null)).Items.Select(f => (f.Name, f.TotalCount)).Should().Equal(("Mail", 5L), ("Restricted Custodian", 1L));

        // One level at a time, in name order; the walled subfolder neither counts nor makes Sent expandable.
        var mail = top.Items[0];
        var children = await ListAsync(services, ws, reviewer, mail.FolderId);
        children.Parent!.FolderId.Should().Be(mail.FolderId);
        children.Items.Select(f => (f.Name, f.Path, f.DocumentCount, f.TotalCount, f.HasSubfolders)).Should().Equal(
            ("Inbox", @"Mail\Inbox", 2L, 3L, true),
            ("Sent", @"Mail\Sent", 1L, 1L, false));
        var adminSent = (await ListAsync(services, ws, admin, mail.FolderId)).Items.Single(f => f.Name == "Sent");
        adminSent.Should().Match<DocumentFolderResource>(f => f.TotalCount == 2 && f.HasSubfolders);

        // Paging a level.
        var first = await ListAsync(services, ws, reviewer, mail.FolderId, limit: 1);
        first.Items.Select(f => f.Name).Should().Equal("Inbox");
        first.NextCursor.Should().NotBeNull();
        var second = await ListAsync(services, ws, reviewer, mail.FolderId, first.NextCursor, limit: 1);
        second.Items.Select(f => f.Name).Should().Equal("Sent");

        // A hidden folder answers like a missing one; a visible one comes with the folders above it.
        var privateFolder = (await ListAsync(services, ws, admin, adminSent.FolderId)).Items.Single();
        (await GetAsync(services, ws, reviewer, privateFolder.FolderId)).Status.Should().Be(DocumentFolderCountStatus.NotFound);
        (await GetAsync(services, ws, reviewer, Guid.CreateVersion7())).Status.Should().Be(DocumentFolderCountStatus.NotFound);
        var found = await GetAsync(services, ws, admin, privateFolder.FolderId);
        found.Folder!.Ancestors!.Select(a => a.Name).Should().Equal("Mail", "Sent");

        // Searching within a folder: with subfolders (the default) or only the folder itself; the query narrows it.
        var inbox = children.Items[0].FolderId;
        (await SearchAsync(services, ws, reviewer, new SearchRequest("", Folder: new SearchFolderFilter(inbox))))
            .Should().BeEquivalentTo(["FLD0001", "FLD0002", "FLD0003"]);
        (await SearchAsync(services, ws, reviewer, new SearchRequest("", Folder: new SearchFolderFilter(inbox, IncludeSubfolders: false))))
            .Should().BeEquivalentTo(["FLD0001", "FLD0003"]);
        (await SearchAsync(services, ws, reviewer, new SearchRequest("fileName:two.msg", Folder: new SearchFolderFilter(inbox))))
            .Should().Equal("FLD0002");
        (await SearchAsync(services, ws, reviewer, new SearchRequest("", Folder: new SearchFolderFilter(mail.FolderId))))
            .Should().HaveCount(4).And.NotContain("FLD0007");

        // Later pages keep the folder.
        await using (var scope = services.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var page = (await search.SearchAsync(SearchHarness.Caller(ws, reviewer),
                new SearchRequest("", PageSize: 2, Highlight: false, Folder: new SearchFolderFilter(inbox)), Ct)).Page!;
            page.Items.Select(i => i.ControlNumber).Should().Equal("FLD0001", "FLD0002");
            var next = (await search.GetPageAsync(SearchHarness.Caller(ws, reviewer), page.SearchId!, new SearchPageRequest(Cursor: page.NextCursor), Ct)).Page!;
            next.Items.Select(i => i.ControlNumber).Should().Equal("FLD0003");
        }

        // A folder is not combined with a saved search, a report term or a review batch.
        await using (var scope = services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<ISearchService>().SearchAsync(SearchHarness.Caller(ws, reviewer),
                new SearchRequest(null, SavedSearchId: Guid.CreateVersion7(), Folder: new SearchFolderFilter(inbox)), Ct);
            outcome.Status.Should().Be(SearchStatus.InvalidRequest);
        }

        // An overlay moves a document to another folder (re-indexed with the document).
        var overlay = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["ControlNumber", "FolderPath"], ["FLD0006", @"\Mail\Sent"])), mode: ImportMode.Overlay, name: "overlay.dat");
        (await h.Import.RunAsync(overlay)).Status.Should().Be(JobStatus.Completed);
        await h.DeliverAllAsync(ws, overlay.JobId);
        await h.TickWatermarkAsync(ws);
        (await SearchAsync(services, ws, reviewer, new SearchRequest("", Folder: new SearchFolderFilter(children.Items[1].FolderId, false))))
            .Should().BeEquivalentTo(["FLD0004", "FLD0006"]);
    }

    private static ServiceProvider Services(ChunkIndexHarness h)
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
        services.AddSingleton<IDocumentFolderStore>(new DocumentFolderRepository(db.AppDataSource));
        services.AddScoped<DocumentFolderService>();
        services.AddOpportunityAuthorization();
        services.AddOpenSearchSearchService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<Guid> MemberAsync(ChunkIndexHarness h, Guid ws, WorkspaceRole role)
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
            ("ws", ws), ("id", Guid.CreateVersion7()), ("role", role.Key()), ("user", user));
        return user;
    }

    /// <summary>An ethical wall hiding <paramref name="documents"/> from <paramref name="user"/>, as wall administration writes it.</summary>
    private static async Task WallAsync(ChunkIndexHarness h, Guid ws, Guid user, params Guid[] documents)
    {
        var wall = Guid.CreateVersion7();
        await h.Import.Db.ExecuteAsync(
            "INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, 'Folder wall')", ("ws", ws), ("wall", wall));
        await h.Import.Db.ExecuteAsync(
            "INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @id, @user)",
            ("ws", ws), ("wall", wall), ("id", Guid.CreateVersion7()), ("user", user));
        foreach (var document in documents)
        {
            await h.Import.Db.ExecuteAsync(
                """
                INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, document_id) VALUES (@ws, @wall, @id, 1, @doc);
                INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall);
                """,
                ("ws", ws), ("doc", document), ("wall", wall), ("id", Guid.CreateVersion7()));
        }
    }

    private static async Task<DocumentFolderPage> ListAsync(
        ServiceProvider services, Guid ws, Guid user, Guid? parent, string? cursor = null, int? limit = null)
    {
        await using var scope = services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<DocumentFolderService>()
            .ListAsync(SearchHarness.Caller(ws, user), parent, cursor, limit, Ct);
        outcome.Page.Should().NotBeNull(outcome.Status.ToString());
        return outcome.Page!;
    }

    private static async Task<DocumentFolderOutcome> GetAsync(ServiceProvider services, Guid ws, Guid user, Guid folder)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DocumentFolderService>().GetAsync(SearchHarness.Caller(ws, user), folder, Ct);
    }

    private static async Task<List<string>> SearchAsync(ServiceProvider services, Guid ws, Guid user, SearchRequest request)
    {
        await using var scope = services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISearchService>()
            .SearchAsync(SearchHarness.Caller(ws, user), request with { Highlight = false, PageSize = 100 }, Ct);
        outcome.Page.Should().NotBeNull(outcome.Status.ToString());
        return [.. outcome.Page!.Items.Select(i => i.ControlNumber)];
    }
}
