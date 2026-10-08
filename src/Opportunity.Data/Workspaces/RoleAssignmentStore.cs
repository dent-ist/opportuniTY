using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Workspaces;

/// <summary>
/// PostgreSQL <see cref="IRoleAssignmentStore"/> (V0012, V0046). The workspace row is the lock and the version holder of
/// the assignment set: a replace takes it <c>FOR UPDATE</c>, so two administrators can never both remove "the other"
/// Workspace Admin, and the last-administrator check runs on the rows the transaction is about to commit.
/// </summary>
public sealed class RoleAssignmentStore(NpgsqlDataSource dataSource) : IRoleAssignmentStore
{
    /// <summary>Bound on one read of the matrix (one row per assignment); far above any real workspace.</summary>
    public const int MaxAssignments = 20_000;

    public async Task<RoleAssignmentSet?> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var set = await ReadSetAsync(tx, lockWorkspace: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return set;
    }

    public async Task<RoleAssignmentWrite> ReplaceAsync(
        Guid workspaceId, RolePrincipal principal, IReadOnlySet<WorkspaceRole> roles, long expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await ReadSetAsync(tx, lockWorkspace: true, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new(RoleAssignmentWriteStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(RoleAssignmentWriteStatus.VersionConflict);
        }

        var held = current.Assignments.Where(a => principal.Matches(a.UserId, a.GroupName)).ToList();
        var removed = held.Where(a => !roles.Contains(a.Role)).OrderBy(a => a.Role).ToList();
        var added = roles.Where(r => held.All(a => a.Role != r)).Order().ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(RoleAssignmentWriteStatus.Ok, current);
        }

        if (added.Count > 0 && principal.UserId is { } newUser)
        {
            await using var known = tx.Command("SELECT EXISTS (SELECT FROM opportunity.app_user WHERE user_id = @user)");
            known.Parameters.AddWithValue("user", newUser);
            if (await known.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return new(RoleAssignmentWriteStatus.UnknownUser);
            }
        }

        if (removed.Count > 0)
        {
            await using var delete = tx.Command(
                "DELETE FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND assignment_id = ANY (@ids)");
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("ids", removed.Select(a => a.AssignmentId).ToArray());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var addedIds = added.Select(_ => Guid.CreateVersion7()).ToArray();
        if (added.Count > 0)
        {
            await using var insert = tx.Command(
                """
                INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id, group_name, assigned_by)
                SELECT @ws, a.id, a.role, @user, @group, @actor
                FROM unnest(@ids::uuid[], @roles::text[]) AS a (id, role)
                """);
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("ids", addedIds);
            insert.Parameters.AddWithValue("roles", added.Select(r => r.Key()).ToArray());
            insert.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)principal.UserId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("group", NpgsqlDbType.Text) { Value = (object?)principal.GroupName ?? DBNull.Value });
            insert.Parameters.AddWithValue("actor", actorId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The invariant on the rows this transaction commits (the workspace row lock serializes every replace).
        await using (var admins = tx.Command(
            "SELECT count(*) FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND role = @admin"))
        {
            admins.Parameters.AddWithValue("ws", workspaceId);
            admins.Parameters.AddWithValue("admin", WorkspaceRole.WorkspaceAdmin.Key());
            if (await admins.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not > 0L)
            {
                return new(RoleAssignmentWriteStatus.LastAdministrator);
            }
        }

        var subject = principal.UserId is { } u
            ? new Dictionary<string, string?> { ["userId"] = u.ToString() }
            : new Dictionary<string, string?> { ["groupName"] = principal.GroupName };
        for (var i = 0; i < added.Count; i++)
        {
            await AuditSql.InsertAsync(tx, Event(audit, AuditTaxonomy.Security.RoleAssigned, addedIds[i], added[i], subject), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var revoked in removed)
        {
            await AuditSql.InsertAsync(tx, Event(audit, AuditTaxonomy.Security.RoleRevoked, revoked.AssignmentId, revoked.Role, subject), cancellationToken)
                .ConfigureAwait(false);
        }

        // A revoked Break-glass role ends the user's live activation at once (it already stops counting without the role).
        if (principal.UserId is { } holder && removed.Any(a => a.Role == WorkspaceRole.BreakGlass))
        {
            await EndActivationsAsync(tx, holder, audit, cancellationToken).ConfigureAwait(false);
        }

        await using (var bump = tx.Command(
            "UPDATE opportunity.workspace SET assignment_set_version = assignment_set_version + 1 WHERE workspace_id = @ws"))
        {
            bump.Parameters.AddWithValue("ws", workspaceId);
            await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadSetAsync(tx, lockWorkspace: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(RoleAssignmentWriteStatus.Ok, saved);
    }

    public async Task<IReadOnlyList<RoleAssignmentCandidate>> FindCandidatesAsync(string? contains, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Installation-level: users and the IdP group names of their last sign-in (app_user is not workspace-owned).
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH users AS (
                SELECT u.user_id, coalesce(u.display_name, u.email, u.user_id::text) AS name, u.email
                  FROM opportunity.app_user u
                 WHERE @q::text IS NULL
                    OR u.display_name ILIKE '%' || @q || '%' ESCAPE '\'
                    OR u.email ILIKE '%' || @q || '%' ESCAPE '\'
                 ORDER BY lower(coalesce(u.display_name, u.email, u.user_id::text)), u.user_id
                 LIMIT @take),
            groups AS (
                SELECT DISTINCT g AS name
                  FROM opportunity.app_user u, unnest(u.groups) AS g
                 WHERE g <> '' AND (@q::text IS NULL OR g ILIKE '%' || @q || '%' ESCAPE '\')
                 ORDER BY 1
                 LIMIT @take)
            SELECT kind, user_id, name, email FROM (
                SELECT 0 AS kind, NULL::uuid AS user_id, name, NULL::text AS email FROM groups
                UNION ALL
                SELECT 1, user_id, name, email FROM users) c
             ORDER BY lower(name), kind, user_id
             LIMIT @take
            """);
        command.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = (object?)EscapeLike(contains) ?? DBNull.Value });
        command.Parameters.AddWithValue("take", limit);
        var items = new List<RoleAssignmentCandidate>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(2);
                items.Add(reader.GetInt32(0) == 0
                    ? new RoleAssignmentCandidate(null, name, name, null)
                    : new RoleAssignmentCandidate(reader.GetGuid(1), null, name, reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    private static async Task<RoleAssignmentSet?> ReadSetAsync(WorkspaceTransaction tx, bool lockWorkspace, CancellationToken cancellationToken)
    {
        long version;
        await using (var head = tx.Command(
            "SELECT assignment_set_version FROM opportunity.workspace WHERE workspace_id = @ws" + (lockWorkspace ? " FOR UPDATE" : string.Empty)))
        {
            head.Parameters.AddWithValue("ws", tx.WorkspaceId);
            if (await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long v)
            {
                return null;
            }

            version = v;
        }

        var items = new List<RoleAssignmentEntry>();
        await using var command = tx.Command(
            """
            SELECT a.assignment_id, a.role, a.user_id, a.group_name, u.display_name, u.email, a.assigned_at
            FROM opportunity.workspace_role_assignment a
            LEFT JOIN opportunity.app_user u ON u.user_id = a.user_id
            WHERE a.workspace_id = @ws
            ORDER BY a.assignment_id
            LIMIT @take
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("take", MaxAssignments);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Unknown role keys cannot exist (check constraint); skip defensively.
            if (!RoleCatalog.TryParse(reader.GetString(1), out var role))
            {
                continue;
            }

            items.Add(new RoleAssignmentEntry(
                reader.GetGuid(0),
                role,
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return new RoleAssignmentSet(items, version);
    }

    private static async Task EndActivationsAsync(WorkspaceTransaction tx, Guid userId, AuditEvent audit, CancellationToken cancellationToken)
    {
        var ended = new List<Guid>();
        await using (var update = tx.Command(
            """
            UPDATE opportunity.break_glass_activation SET ended_at = now(), ended_reason = 'Revoked'
            WHERE workspace_id = @ws AND user_id = @user AND ended_at IS NULL AND expires_at > now()
            RETURNING activation_id
            """))
        {
            update.Parameters.AddWithValue("ws", tx.WorkspaceId);
            update.Parameters.AddWithValue("user", userId);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ended.Add(reader.GetGuid(0));
            }
        }

        foreach (var activationId in ended)
        {
            await AuditSql.InsertAsync(tx, audit with
            {
                EventId = Guid.CreateVersion7(),
                Action = AuditTaxonomy.Security.BreakGlassEnded,
                ResourceType = "BreakGlassActivation",
                ResourceId = activationId.ToString(),
                Details = new Dictionary<string, string?> { ["EndedReason"] = "Revoked", ["Cause"] = "RoleRevoked", ["userId"] = userId.ToString() },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AuditEvent Event(
        AuditEvent template, string action, Guid assignmentId, WorkspaceRole role, IReadOnlyDictionary<string, string?> subject)
    {
        var details = new Dictionary<string, string?>(template.Details) { ["role"] = role.Key() };
        foreach (var (key, value) in subject)
        {
            details[key] = value;
        }

        return template with
        {
            EventId = Guid.CreateVersion7(),
            Action = action,
            ResourceId = assignmentId.ToString(),
            Details = details,
        };
    }

    private static string? EscapeLike(string? value) =>
        value?.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
