using Opportunity.Core.Security;
using Opportunity.Data.Security;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Authorization;

/// <summary>
/// A migrated database with helpers that arrange V0008 security state (as the superuser) for PDP tests. The reader
/// runs as an <c>opportunity_app</c> login, so RLS applies to it as in production.
/// </summary>
internal sealed class AuthorizationDatabase : IAsyncDisposable
{
    private AuthorizationDatabase(CoreSchemaDatabase core)
    {
        Core = core;
        Reader = new PostgresSecurityStateReader(core.AppDataSource);
    }

    public CoreSchemaDatabase Core { get; }

    public PostgresSecurityStateReader Reader { get; }

    public static async Task<AuthorizationDatabase> CreateAsync(MigrationPostgresFixture postgres) =>
        new(await CoreSchemaDatabase.CreateAsync(postgres));

    public async Task<Guid> CreateUserAsync(Guid? userId = null, string[]? groups = null)
    {
        var id = userId ?? Guid.CreateVersion7();
        await Core.ExecuteAsync(
            """
            INSERT INTO opportunity.app_user (user_id, issuer, subject, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, 'https://idp.test', @id::text, @groups, now(), now())
            ON CONFLICT DO NOTHING
            """,
            ("id", id), ("groups", groups ?? []));
        return id;
    }

    public Task AssignAsync(Guid workspaceId, WorkspaceRole role, Guid userId) =>
        Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, @role, @user)",
            ("ws", workspaceId), ("id", Guid.CreateVersion7()), ("role", role.Key()), ("user", userId));

    public Task AssignGroupAsync(Guid workspaceId, WorkspaceRole role, string group) =>
        Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, group_name) VALUES (@ws, @id, @role, @group)",
            ("ws", workspaceId), ("id", Guid.CreateVersion7()), ("role", role.Key()), ("group", group));

    public async Task<Guid> DocumentAsync(Guid workspaceId, params string[] classes)
    {
        var document = await Core.InsertDocumentAsync(workspaceId, "DOC" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant());
        foreach (var classKey in classes)
        {
            await Core.ExecuteAsync(
                "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
                ("ws", workspaceId), ("doc", document.DocumentId), ("class", classKey));
        }

        return document.DocumentId;
    }

    public async Task<Guid> WallAsync(Guid workspaceId, IEnumerable<Guid> users, IEnumerable<string> groups, IEnumerable<Guid> documents)
    {
        var wall = Guid.CreateVersion7();
        await Core.ExecuteAsync(
            "INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, @name)",
            ("ws", workspaceId), ("wall", wall), ("name", "Wall " + wall.ToString("N")));
        foreach (var user in users)
        {
            await Core.ExecuteAsync(
                "INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @id, @user)",
                ("ws", workspaceId), ("wall", wall), ("id", Guid.CreateVersion7()), ("user", user));
        }

        foreach (var group in groups)
        {
            await Core.ExecuteAsync(
                "INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, group_name) VALUES (@ws, @wall, @id, @group)",
                ("ws", workspaceId), ("wall", wall), ("id", Guid.CreateVersion7()), ("group", group));
        }

        foreach (var document in documents)
        {
            await Core.ExecuteAsync(
                "INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall)",
                ("ws", workspaceId), ("doc", document), ("wall", wall));
        }

        return wall;
    }

    /// <summary>An activation that started <paramref name="startedAgo"/> ago and lasts <paramref name="duration"/>.</summary>
    public Task ActivateBreakGlassAsync(Guid workspaceId, Guid userId, TimeSpan duration, TimeSpan? startedAgo = null) =>
        Core.ExecuteAsync(
            """
            INSERT INTO opportunity.break_glass_activation (workspace_id, activation_id, user_id, reason, activated_at, expires_at)
            VALUES (@ws, @id, @user, 'Incident 42: privilege log dispute', now() - @ago, now() - @ago + @duration)
            """,
            ("ws", workspaceId), ("id", Guid.CreateVersion7()), ("user", userId), ("ago", startedAgo ?? TimeSpan.Zero), ("duration", duration));

    public ValueTask DisposeAsync() => Core.DisposeAsync();
}
