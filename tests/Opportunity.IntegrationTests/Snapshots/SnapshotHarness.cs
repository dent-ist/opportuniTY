using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Snapshots;

/// <summary>
/// The snapshot service over the real search service (OpenSearch + the PostgreSQL PDP) and the PostgreSQL store, all as
/// the RLS-bound app login. Each service call runs in its own DI scope, like one request.
/// </summary>
internal sealed class SnapshotHarness : IAsyncDisposable
{
    private SnapshotHarness(SearchHarness search, SnapshotOptions options)
    {
        Search = search;
        Options = options;
        Store = new Data.Snapshots.DocumentSetSnapshotStore(search.Db.Core.AppDataSource);
    }

    public SearchHarness Search { get; }

    public SnapshotOptions Options { get; }

    public IDocumentSetSnapshotStore Store { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SnapshotHarness> CreateAsync(
        OpenSearchFixture openSearch, MigrationPostgresFixture postgres, Action<SnapshotOptions>? configure = null)
    {
        var options = new SnapshotOptions { SelectionPageSize = 500, AuthorizationBatchSize = 700 };
        configure?.Invoke(options);
        options.Validate();
        return new SnapshotHarness(await SearchHarness.CreateAsync(openSearch, postgres), options);
    }

    public async Task<T> ServiceAsync<T>(Func<DocumentSetSnapshotService, Task<T>> call)
    {
        await using var scope = Search.CreateScope();
        var service = new DocumentSetSnapshotService(
            Store,
            scope.ServiceProvider.GetRequiredService<ISearchService>(),
            scope.ServiceProvider.GetRequiredService<IAuthorizationService>(),
            Search.Audit,
            Options,
            TimeProvider.System);
        return await call(service);
    }

    public Task<SnapshotCreateOutcome> CreateAsync(Guid ws, Guid user, SnapshotCreateRequest request) =>
        ServiceAsync(s => s.CreateAsync(SearchHarness.Caller(ws, user), request, Ct));

    public async Task<SnapshotRecord> ReadyAsync(Guid ws, Guid user, SnapshotCreateRequest request)
    {
        var outcome = await CreateAsync(ws, user, request);
        if (outcome.Status != SnapshotCreateStatus.Ready)
        {
            throw new InvalidOperationException($"Expected a Ready snapshot, got {outcome.Status}.");
        }

        return outcome.Snapshot!;
    }

    public Task<IReadOnlyList<SnapshotMember>> MembersAsync(Guid ws, SnapshotRecord snapshot) =>
        snapshot.DocumentCount is 0 or null
            ? Task.FromResult<IReadOnlyList<SnapshotMember>>([])
            : Store.ReadMembersAsync(ws, snapshot.SnapshotId, 1, snapshot.DocumentCount.Value, Ct);

    /// <summary>A PostgreSQL document (optionally a family child) with restriction classes, projected with its text.</summary>
    public async Task<Guid> DocumentAsync(
        Guid ws, string controlNumber, string text, string[]? classes = null, Guid? parent = null, int sequence = 0, bool projectSecurity = true)
    {
        var doc = await Search.Db.Core.InsertDocumentAsync(ws, controlNumber, d =>
        {
            if (parent is { } p)
            {
                d.FamilyId = p;
                d.ParentDocumentId = p;
                d.FamilySequence = sequence;
            }
        });
        foreach (var classKey in classes ?? [])
        {
            await Search.Db.Core.ExecuteAsync(
                "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
                ("ws", ws), ("doc", doc.DocumentId), ("class", classKey));
        }

        await Search.DocumentAsync(ws, controlNumber, text, classes, projectSecurity: projectSecurity, documentId: doc.DocumentId);
        return doc.DocumentId;
    }

    /// <summary>
    /// <paramref name="count"/> documents inserted in one statement and bulk-indexed with <paramref name="text"/>; returns
    /// their IDs in control-number order.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> BulkDocumentsAsync(Guid ws, int count, string prefix, string text)
    {
        await Search.Db.Core.ExecuteAsync(
            """
            WITH d AS (SELECT n, gen_random_uuid() AS id FROM generate_series(1, @n) AS n)
            INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id)
            SELECT @ws, d.id, @prefix || lpad(d.n::text, 7, '0'), @prefix || lpad(d.n::text, 7, '0'), d.id FROM d
            """,
            ("ws", ws), ("n", count), ("prefix", prefix));
        await Search.Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
            SELECT workspace_id, document_id FROM opportunity.document WHERE workspace_id = @ws
            ON CONFLICT DO NOTHING
            """,
            ("ws", ws));
        var ids = (await Search.Db.Core.ColumnAsync(
                $"SELECT document_id::text || '|' || control_number FROM opportunity.document WHERE workspace_id = '{ws}' AND control_number LIKE '{prefix}%' ORDER BY control_number"))
            .Select(v => v.Split('|')).ToList();

        var placement = await Search.Indexes.ResolveAsync(ws, IndexPurpose.Write, Ct);
        foreach (var target in placement.WriteTargets)
        {
            foreach (var batch in ids.Chunk(2_000))
            {
                var body = new StringBuilder();
                foreach (var row in batch)
                {
                    var action = new JsonObject { ["index"] = new JsonObject { ["_index"] = target.Index, ["_id"] = row[0] } };
                    if (target.Routing is { } routing)
                    {
                        action["index"]!["routing"] = routing;
                    }

                    body.Append(action.ToJsonString()).Append('\n');
                    body.Append(new JsonObject
                    {
                        ["workspaceId"] = ws.ToString("D"),
                        ["documentId"] = row[0],
                        ["controlNumber"] = row[1],
                        ["securityTags"] = new JsonArray(),
                        ["text"] = text,
                    }.ToJsonString()).Append('\n');
                }

                using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson");
                using var response = await Search.OpenSearchHttp.PostAsync("_bulk?refresh=true", content, Ct);
                response.EnsureSuccessStatusCode();
                var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
                if (result["errors"]!.GetValue<bool>())
                {
                    throw new InvalidOperationException("Bulk indexing failed: " + result.ToJsonString()[..500]);
                }
            }
        }

        return [.. ids.Select(r => Guid.Parse(r[0]))];
    }

    public Task<Guid> MemberAsync(Guid ws, WorkspaceRole role) => Search.MemberAsync(ws, role);

    public ValueTask DisposeAsync() => Search.DisposeAsync();
}
