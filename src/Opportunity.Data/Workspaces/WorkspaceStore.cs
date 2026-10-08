using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Workspaces;

/// <summary>
/// PostgreSQL <see cref="IWorkspaceStore"/> (E04-T05). Writes run in the workspace's own RLS context and insert their
/// audit events in the same transaction. The member list of a principal comes from
/// <c>opportunity.member_workspace_ids</c> (V0014), which probes each workspace under its own context, so this class
/// never needs a cross-workspace read of role assignments.
/// </summary>
public sealed class WorkspaceStore(NpgsqlDataSource dataSource) : IWorkspaceStore
{
    private const string ResourceType = "Workspace";

    private const string TimeZoneSql = """
        SELECT @tz !~ '^(posix|right)/' AND EXISTS (SELECT FROM pg_catalog.pg_timezone_names WHERE name = @tz)
        """;

    public async Task<WorkspacePage> ListForPrincipalAsync(
        SecurityPrincipal principal, WorkspaceListPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (principal.UserId == Guid.Empty)
        {
            return new WorkspacePage([], 0, null);
        }

        // An installation transaction: the registry is read-open and the function sets each workspace's context itself.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            WITH w AS MATERIALIZED (
                SELECT w.*, lower(w.name) AS sort_name
                FROM opportunity.member_workspace_ids(@user, @groups) AS m (workspace_id)
                JOIN opportunity.workspace w ON w.workspace_id = m.workspace_id
            )
            SELECT {WorkspaceColumns.Select}, w.sort_name, (SELECT count(*) FROM w)
            FROM w
            WHERE @after_name::text IS NULL OR (w.sort_name, w.workspace_id) > (@after_name::text, @after_id)
            ORDER BY w.sort_name, w.workspace_id
            LIMIT @take
            """);
        command.Parameters.AddWithValue("user", principal.UserId);
        command.Parameters.Add(new NpgsqlParameter<string[]>("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. principal.Groups] });
        command.Parameters.Add(new NpgsqlParameter("after_name", NpgsqlDbType.Text) { Value = (object?)after?.SortName ?? DBNull.Value });
        command.Parameters.AddWithValue("after_id", after?.WorkspaceId ?? Guid.Empty);
        command.Parameters.AddWithValue("take", limit + 1);

        var items = new List<Workspace>(limit);
        string? lastSortName = null;
        long total = 0;
        var more = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                total = reader.GetInt64(WorkspaceColumns.Count + 1);
                if (items.Count == limit)
                {
                    more = true;
                    break;
                }

                items.Add(WorkspaceColumns.Read(reader));
                lastSortName = reader.GetString(WorkspaceColumns.Count);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        var next = more ? new WorkspaceListPosition(lastSortName!, items[^1].WorkspaceId) : null;
        return new WorkspacePage(items, total, next);
    }

    public async Task<WorkspaceWriteResult> CreateAsync(
        Guid workspaceId, WorkspaceSettings settings, SecurityPrincipal creator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(creator);
        if (creator.UserId == Guid.Empty)
        {
            throw new ArgumentException("A workspace is created by a signed-in user.", nameof(creator));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (!await IsTimeZoneAsync(tx, settings.DisplayTimeZone, cancellationToken).ConfigureAwait(false))
        {
            return new WorkspaceWriteResult(WorkspaceWriteOutcome.InvalidTimeZone);
        }

        Workspace workspace;
        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.workspace AS w (workspace_id, name, matter_number, display_time_zone, storage_profile)
            VALUES (@ws, @name, @matter, @tz, @profile)
            RETURNING {WorkspaceColumns.Select}
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            BindSettings(insert, settings);
            workspace = (await ReadOneAsync(insert, cancellationToken).ConfigureAwait(false))!;
        }

        var assignmentId = Guid.CreateVersion7();
        await using (var assign = tx.Command(
            """
            INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id, assigned_by)
            VALUES (@ws, @id, @role, @user, @user)
            """))
        {
            assign.Parameters.AddWithValue("ws", workspaceId);
            assign.Parameters.AddWithValue("id", assignmentId);
            assign.Parameters.AddWithValue("role", WorkspaceRole.WorkspaceAdmin.Key());
            assign.Parameters.AddWithValue("user", creator.UserId);
            await assign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Event(creator, workspace, AuditTaxonomy.Workspace.Category, AuditTaxonomy.Workspace.Created,
            ResourceType, workspaceId.ToString(), new Dictionary<string, string?>
            {
                ["displayTimeZone"] = workspace.DisplayTimeZone,
                ["storageProfile"] = workspace.StorageProfile,
            }), cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, Event(creator, workspace, AuditTaxonomy.Security.Category, AuditTaxonomy.Security.RoleAssigned,
            "RoleAssignment", assignmentId.ToString(), new Dictionary<string, string?>
            {
                ["role"] = WorkspaceRole.WorkspaceAdmin.Key(),
                ["userId"] = creator.UserId.ToString(),
                ["reason"] = "WorkspaceCreator",
            }), cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteResult(WorkspaceWriteOutcome.Ok, workspace);
    }

    public async Task<WorkspaceWriteResult> UpdateAsync(
        Guid workspaceId, long expectedVersion, WorkspaceSettings settings, SecurityPrincipal actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Workspace? current;
        await using (var select = tx.Command($"SELECT {WorkspaceColumns.Select} FROM opportunity.workspace w WHERE w.workspace_id = @ws FOR UPDATE"))
        {
            select.Parameters.AddWithValue("ws", workspaceId);
            current = await ReadOneAsync(select, cancellationToken).ConfigureAwait(false);
        }

        if (current is null || current.Status is WorkspaceStatus.Deleting or WorkspaceStatus.Purged)
        {
            return new WorkspaceWriteResult(WorkspaceWriteOutcome.NotFound);
        }

        if (current.RowVersion != expectedVersion)
        {
            return new WorkspaceWriteResult(WorkspaceWriteOutcome.VersionConflict, current);
        }

        var changed = new List<string>(4);
        if (!string.Equals(current.Name, settings.Name, StringComparison.Ordinal))
        {
            changed.Add("name");
        }

        if (!string.Equals(current.MatterNumber, settings.MatterNumber, StringComparison.Ordinal))
        {
            changed.Add("matterNumber");
        }

        if (!string.Equals(current.DisplayTimeZone, settings.DisplayTimeZone, StringComparison.Ordinal))
        {
            changed.Add("displayTimeZone");
            if (!await IsTimeZoneAsync(tx, settings.DisplayTimeZone, cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceWriteResult(WorkspaceWriteOutcome.InvalidTimeZone, current);
            }
        }

        if (!string.Equals(current.StorageProfile, settings.StorageProfile, StringComparison.Ordinal))
        {
            changed.Add("storageProfile");
            await using var stored = tx.Command("SELECT EXISTS (SELECT FROM opportunity.stored_object WHERE workspace_id = @ws)");
            stored.Parameters.AddWithValue("ws", workspaceId);
            if (await stored.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return new WorkspaceWriteResult(WorkspaceWriteOutcome.StorageProfileLocked, current);
            }
        }

        if (changed.Count == 0)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceWriteResult(WorkspaceWriteOutcome.Ok, current);
        }

        Workspace updated;
        await using (var update = tx.Command(
            $"""
            UPDATE opportunity.workspace AS w
            SET name = @name, matter_number = @matter, display_time_zone = @tz, storage_profile = @profile,
                row_version = w.row_version + 1, updated_at = now()
            WHERE w.workspace_id = @ws
            RETURNING {WorkspaceColumns.Select}
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            BindSettings(update, settings);
            updated = (await ReadOneAsync(update, cancellationToken).ConfigureAwait(false))!;
        }

        // Field names and configuration values only: the name and matter number are not copied into audit (ADR-013 §7).
        var details = new Dictionary<string, string?> { ["changed"] = string.Join(',', changed) };
        if (changed.Contains("displayTimeZone"))
        {
            details["displayTimeZone.old"] = current.DisplayTimeZone;
            details["displayTimeZone.new"] = updated.DisplayTimeZone;
        }

        if (changed.Contains("storageProfile"))
        {
            details["storageProfile.old"] = current.StorageProfile;
            details["storageProfile.new"] = updated.StorageProfile;
        }

        details["version"] = updated.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await AuditSql.InsertAsync(tx, Event(actor, updated, AuditTaxonomy.Workspace.Category, AuditTaxonomy.Workspace.SettingsChanged,
            ResourceType, workspaceId.ToString(), details), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteResult(WorkspaceWriteOutcome.Ok, updated);
    }

    public async Task<WorkspaceMemberPage> ListMembersAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var batch = tx.Batch();
        var page = new NpgsqlBatchCommand(
            """
            SELECT a.assignment_id, a.role, a.user_id, coalesce(u.display_name, u.email), a.group_name, a.assigned_at
            FROM opportunity.workspace_role_assignment a
            LEFT JOIN opportunity.app_user u ON u.user_id = a.user_id
            WHERE a.workspace_id = @ws AND (@after::uuid IS NULL OR a.assignment_id > @after::uuid)
            ORDER BY a.assignment_id
            LIMIT @take
            """);
        page.Parameters.AddWithValue("ws", workspaceId);
        page.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        page.Parameters.AddWithValue("take", limit + 1);
        var count = new NpgsqlBatchCommand("SELECT count(*) FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws");
        count.Parameters.AddWithValue("ws", workspaceId);
        batch.BatchCommands.Add(page);
        batch.BatchCommands.Add(count);

        var items = new List<WorkspaceMember>(limit);
        var more = false;
        long total;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count == limit)
                {
                    more = true;
                    continue;
                }

                // Unknown role keys cannot exist (check constraint); skip defensively rather than fail the page.
                if (!RoleCatalog.TryParse(reader.GetString(1), out var role))
                {
                    continue;
                }

                items.Add(new WorkspaceMember(
                    reader.GetGuid(0),
                    role,
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = reader.GetInt64(0);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceMemberPage(items, total, more ? items[^1].AssignmentId : null);
    }

    private static async Task<bool> IsTimeZoneAsync(WorkspaceTransaction tx, string timeZone, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(TimeZoneSql);
        command.Parameters.AddWithValue("tz", timeZone);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static void BindSettings(NpgsqlCommand command, WorkspaceSettings settings)
    {
        command.Parameters.AddWithValue("name", settings.Name);
        command.Parameters.Add(new NpgsqlParameter("matter", NpgsqlDbType.Text) { Value = (object?)settings.MatterNumber ?? DBNull.Value });
        command.Parameters.AddWithValue("tz", settings.DisplayTimeZone);
        command.Parameters.AddWithValue("profile", settings.StorageProfile);
    }

    private static async Task<Workspace?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? WorkspaceColumns.Read(reader) : null;
    }

    private static AuditEvent Event(
        SecurityPrincipal actor, Workspace workspace, string category, string action, string resourceType, string resourceId,
        IReadOnlyDictionary<string, string?> details) => new()
        {
            WorkspaceId = workspace.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId,
            Details = details,
        };
}

public static class WorkspaceStoreRegistration
{
    /// <summary>Registers the PostgreSQL workspace reader, store and role assignment store (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresWorkspaceStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWorkspaceReader, WorkspaceReader>();
        services.TryAddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.TryAddSingleton<IRoleAssignmentStore, RoleAssignmentStore>();
        return services;
    }
}
