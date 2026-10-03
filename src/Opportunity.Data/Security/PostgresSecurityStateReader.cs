using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Security;

/// <summary>
/// Reads the PDP's state (V0012) in one batched round trip inside the workspace's RLS context (ADR-015 D5.5: no
/// cross-request cache). Deleted documents are reported as absent, so they are NotFound for everyone.
/// </summary>
public sealed class PostgresSecurityStateReader(NpgsqlDataSource dataSource) : ISecurityStateReader
{
    private const string WorkspaceSql = """
        SELECT w.status,
               (SELECT max(b.expires_at) FROM opportunity.break_glass_activation b
                WHERE b.workspace_id = w.workspace_id AND b.user_id = @user AND b.ended_at IS NULL AND b.expires_at > now())
        FROM opportunity.workspace w
        WHERE w.workspace_id = @ws
        """;

    private const string RolesSql = """
        SELECT DISTINCT a.role FROM opportunity.workspace_role_assignment a
        WHERE a.workspace_id = @ws AND (a.user_id = @user OR a.group_name = ANY(@groups))
        """;

    private const string ClassGrantsSql = """
        SELECT c.class_key,
               ARRAY(SELECT g.role FROM opportunity.restriction_class_grant g
                     WHERE g.workspace_id = c.workspace_id AND g.class_key = c.class_key)
        FROM opportunity.restriction_class c
        WHERE c.workspace_id = @ws
        """;

    private const string WallsSql = """
        SELECT DISTINCT m.wall_id FROM opportunity.ethical_wall_member m
        WHERE m.workspace_id = @ws AND (m.user_id = @user OR m.group_name = ANY(@groups))
        """;

    private const string DocumentsSql = """
        SELECT d.document_id,
               ARRAY(SELECT r.class_key FROM opportunity.document_restriction r
                     WHERE r.workspace_id = d.workspace_id AND r.document_id = d.document_id),
               ARRAY(SELECT dw.wall_id FROM opportunity.document_wall dw
                     WHERE dw.workspace_id = d.workspace_id AND dw.document_id = d.document_id)
        FROM opportunity.document d
        LEFT JOIN opportunity.document_projection_state s
               ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids) AND s.is_deleted IS NOT TRUE
        """;

    public async Task<SecurityStateRead> ReadAsync(
        Guid workspaceId,
        SecurityPrincipal principal,
        bool includePrincipal,
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var ids = documentIds is { Count: > 0 } ? documentIds.Distinct().ToArray() : null;
        var documents = new Dictionary<Guid, DocumentSecurityAttributes>(ids?.Length ?? 0);
        if (!includePrincipal && ids is null)
        {
            return new SecurityStateRead(null, documents);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var batch = tx.Batch();
        var groups = principal.Groups.ToArray();
        if (includePrincipal)
        {
            batch.BatchCommands.Add(Command(WorkspaceSql, workspaceId, principal.UserId, groups));
            batch.BatchCommands.Add(Command(RolesSql, workspaceId, principal.UserId, groups));
            var classGrants = new NpgsqlBatchCommand(ClassGrantsSql);
            classGrants.Parameters.Add(new NpgsqlParameter<Guid>("ws", workspaceId));
            batch.BatchCommands.Add(classGrants);
            batch.BatchCommands.Add(Command(WallsSql, workspaceId, principal.UserId, groups));
        }

        if (ids is not null)
        {
            var command = new NpgsqlBatchCommand(DocumentsSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("ws", workspaceId));
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = ids });
            batch.BatchCommands.Add(command);
        }

        PrincipalSecurityState? state = null;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (includePrincipal)
            {
                state = await ReadPrincipalAsync(reader, cancellationToken).ConfigureAwait(false);
            }

            if (ids is not null)
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    documents[reader.GetGuid(0)] = new DocumentSecurityAttributes(
                        reader.GetFieldValue<string[]>(1), reader.GetFieldValue<Guid[]>(2));
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SecurityStateRead(state, documents);
    }

    private static async Task<PrincipalSecurityState?> ReadPrincipalAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        WorkspaceStatus? status = null;
        DateTimeOffset? breakGlassExpiresAt = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            status = Enum.Parse<WorkspaceStatus>(reader.GetString(0));
            breakGlassExpiresAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var roles = new HashSet<WorkspaceRole>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (RoleCatalog.TryParse(reader.GetString(0), out var role))
            {
                roles.Add(role);
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var classGrants = new Dictionary<string, IReadOnlySet<WorkspaceRole>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var granted = new HashSet<WorkspaceRole>();
            foreach (var key in reader.GetFieldValue<string[]>(1))
            {
                if (RoleCatalog.TryParse(key, out var role) && role != WorkspaceRole.BreakGlass)
                {
                    granted.Add(role);
                }
            }

            classGrants[reader.GetString(0)] = granted;
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var walls = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            walls.Add(reader.GetGuid(0));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        return status is { } s ? new PrincipalSecurityState(s, roles, classGrants, walls, breakGlassExpiresAt) : null;
    }

    private static NpgsqlBatchCommand Command(string sql, Guid workspaceId, Guid userId, string[] groups)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("ws", workspaceId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("user", userId));
        command.Parameters.Add(new NpgsqlParameter<string[]>("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = groups });
        return command;
    }
}
