using System.Data;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Compliance;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Compliance;

/// <summary>
/// PostgreSQL reads for the compliance reports (E14-T06). Everything runs in the workspace's RLS context: the audit
/// trail, checkpoints and purge records of the workspace's own chain (V0010, V0056, V0063), its walls, role assignments
/// and the installation-level users they name. Walls are evaluated as they are now (scope and membership have no
/// history); a member counts only from the time it was added to the wall, and wall changes in the period are listed.
/// </summary>
public sealed class ComplianceReportStore(NpgsqlDataSource dataSource) : IComplianceReportStore
{
    private const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

    private const int MaxWallChanges = 1000;

    public async Task<WallProofData> ReadWallProofAsync(
        Guid workspaceId, DateTimeOffset periodStart, DateTimeOffset periodEnd, int maxAttempts, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        var members = new Dictionary<Guid, List<WallProofMember>>();
        await using (var command = tx.Command(
            """
            SELECT m.wall_id, m.user_id, m.group_name, coalesce(u.display_name, u.email), m.added_at
            FROM opportunity.ethical_wall_member m
            LEFT JOIN opportunity.app_user u ON u.user_id = m.user_id
            WHERE m.workspace_id = @ws
            ORDER BY m.wall_id, m.group_name NULLS FIRST, 4, m.user_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var wall = reader.GetGuid(0);
                if (!members.TryGetValue(wall, out var list))
                {
                    members[wall] = list = [];
                }

                list.Add(new WallProofMember(
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        var walls = new List<WallProofWall>();
        await using (var command = tx.Command(
            """
            SELECT w.wall_id, w.name, w.created_at, w.updated_at,
                   (SELECT count(*) FROM opportunity.document_wall dw WHERE dw.workspace_id = w.workspace_id AND dw.wall_id = w.wall_id)
            FROM opportunity.ethical_wall w
            WHERE w.workspace_id = @ws
            ORDER BY lower(w.name), w.wall_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                walls.Add(new WallProofWall(id, reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
                    members.TryGetValue(id, out var list) ? list : [], reader.GetInt64(4)));
            }
        }

        // Attempts: audited actions of users on documents that a wall the user belongs to (directly, or through a group the
        // IdP last reported for them) covers, from the time they joined it; plus every attempt a wall refused. Breaches are
        // listed first, so a truncated list never hides one; the counts cover every attempt.
        var attempts = new List<WallProofAttempt>();
        long total = 0, denied = 0, breakGlass = 0, allowed = 0, failed = 0;
        await using (var command = tx.Command(
            $"""
            WITH member AS (
                SELECT m.wall_id, m.user_id::text AS actor_id, m.added_at
                FROM opportunity.ethical_wall_member m
                WHERE m.workspace_id = @ws AND m.user_id IS NOT NULL
                UNION ALL
                SELECT m.wall_id, u.user_id::text, m.added_at
                FROM opportunity.ethical_wall_member m
                JOIN opportunity.app_user u ON m.group_name = ANY (u.groups)
                WHERE m.workspace_id = @ws AND m.group_name IS NOT NULL
            ), candidate AS (
                SELECT e.event_id, e.occurred_at, e.category, e.action, e.actor_id, e.actor_display, e.outcome, e.reason_code,
                       e.access_path, e.correlation_id,
                       CASE WHEN e.resource_id ~ '{UuidPattern}' THEN e.resource_id::uuid END AS document_id
                FROM audit.audit_event e
                WHERE e.workspace_id = @ws AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.actor_type = 'User' AND e.resource_type = 'Document'
            ), attempt AS (
                SELECT c.*,
                       ARRAY(SELECT DISTINCT dw.wall_id FROM opportunity.document_wall dw
                             JOIN member mb ON mb.wall_id = dw.wall_id
                             WHERE dw.workspace_id = @ws AND dw.document_id = c.document_id
                               AND mb.actor_id = c.actor_id AND mb.added_at <= c.occurred_at) AS member_walls
                FROM candidate c
                WHERE c.document_id IS NOT NULL
            ), walled AS (
                SELECT a.*,
                       CASE WHEN cardinality(a.member_walls) > 0 THEN a.member_walls
                            ELSE ARRAY(SELECT dw.wall_id FROM opportunity.document_wall dw
                                       WHERE dw.workspace_id = @ws AND dw.document_id = a.document_id ORDER BY 1) END AS walls,
                       (a.outcome = 'Success' AND a.access_path <> 'BreakGlass') AS breach
                FROM attempt a
                WHERE cardinality(a.member_walls) > 0 OR a.reason_code = 'EthicalWall'
            )
            SELECT event_id, occurred_at, category, action, actor_id, actor_display, document_id, walls, outcome, reason_code,
                   access_path, correlation_id,
                   count(*) OVER (),
                   count(*) FILTER (WHERE outcome = 'Denied') OVER (),
                   count(*) FILTER (WHERE outcome = 'Success' AND access_path = 'BreakGlass') OVER (),
                   count(*) FILTER (WHERE breach) OVER (),
                   count(*) FILTER (WHERE outcome = 'Failure') OVER ()
            FROM walled
            ORDER BY breach DESC, occurred_at, event_id
            LIMIT @take
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("from", periodStart);
            command.Parameters.AddWithValue("to", periodEnd);
            command.Parameters.AddWithValue("take", maxAttempts);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                attempts.Add(new WallProofAttempt(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetGuid(6),
                    reader.GetFieldValue<Guid[]>(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11)));
                (total, denied, breakGlass, allowed, failed) =
                    (reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16));
            }
        }

        var changes = new List<WallProofChange>();
        await using (var command = tx.Command(
            """
            SELECT event_id, occurred_at, action, actor_display, resource_id
            FROM audit.audit_event
            WHERE workspace_id = @ws AND category = 'Security'
              AND action IN ('WallCreated', 'WallChanged', 'WallDeleted', 'WallMemberAdded', 'WallMemberRemoved')
              AND occurred_at >= @from AND occurred_at < @to
            ORDER BY occurred_at, event_id
            LIMIT @take
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("from", periodStart);
            command.Parameters.AddWithValue("to", periodEnd);
            command.Parameters.AddWithValue("take", MaxWallChanges);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new WallProofChange(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WallProofData(walls, new WallProofCounts(total, denied, breakGlass, allowed, failed), attempts, total > attempts.Count, changes);
    }

    public async Task<IReadOnlyList<AccessReviewEntry>> ReadAccessReviewAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        var assignments = new List<(WorkspaceRole Role, Guid? User, string? Group, DateTimeOffset At, Guid? By)>();
        await using (var command = tx.Command(
            "SELECT role, user_id, group_name, assigned_at, assigned_by FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws ORDER BY assigned_at, assignment_id"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (RoleCatalog.TryParse(reader.GetString(0), out var role))
                {
                    assignments.Add((role, reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetGuid(4)));
                }
            }
        }

        var wallMembers = new List<(Guid Wall, string Name, Guid? User, string? Group)>();
        await using (var command = tx.Command(
            """
            SELECT m.wall_id, w.name, m.user_id, m.group_name
            FROM opportunity.ethical_wall_member m
            JOIN opportunity.ethical_wall w ON w.workspace_id = m.workspace_id AND w.wall_id = m.wall_id
            WHERE m.workspace_id = @ws
            ORDER BY lower(w.name), m.wall_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                wallMembers.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var groups = assignments.Select(a => a.Group).Concat(wallMembers.Select(m => m.Group)).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var directUsers = assignments.Select(a => a.User).Concat(wallMembers.Select(m => m.User)).OfType<Guid>().Distinct().ToArray();

        var users = new Dictionary<Guid, (string? Name, string? Email, DateTimeOffset? SignIn, string[] Groups, DateTimeOffset? LastAccess)>();
        await using (var command = tx.Command(
            """
            SELECT u.user_id, u.display_name, u.email, u.last_sign_in_at,
                   ARRAY(SELECT g FROM unnest(u.groups) g WHERE g = ANY (@groups) ORDER BY g COLLATE "C"),
                   (SELECT max(e.occurred_at) FROM audit.audit_event e
                    WHERE e.actor_id = u.user_id::text AND e.workspace_id = @ws AND e.actor_type = 'User' AND e.outcome = 'Success')
            FROM opportunity.app_user u
            WHERE u.user_id = ANY (@users) OR u.groups && @groups
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.Add(new NpgsqlParameter("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = groups });
            command.Parameters.Add(new NpgsqlParameter("users", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = directUsers });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                users[reader.GetGuid(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<string[]>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var entries = new List<AccessReviewEntry>();
        foreach (var userId in users.Keys.Concat(directUsers).Distinct())
        {
            users.TryGetValue(userId, out var user);
            var memberOf = user.Groups ?? [];
            var roles = assignments.Where(a => a.User == userId).Select(a => new AccessReviewRole(a.Role, null, a.At, a.By))
                .Concat(assignments.Where(a => a.Group is { } g && memberOf.Contains(g, StringComparer.Ordinal))
                    .Select(a => new AccessReviewRole(a.Role, a.Group, a.At, a.By)))
                .ToList();
            var userWalls = wallMembers.Where(m => m.User == userId).Select(m => new AccessReviewWall(m.Wall, m.Name, null))
                .Concat(wallMembers.Where(m => m.Group is { } g && memberOf.Contains(g, StringComparer.Ordinal))
                    .Select(m => new AccessReviewWall(m.Wall, m.Name, m.Group)))
                .ToList();
            entries.Add(new AccessReviewEntry(AccessReviewPrincipal.User, userId, null, user.Name, user.Email, roles, userWalls,
                user.LastAccess, user.SignIn, null));
        }

        entries.Sort((a, b) =>
        {
            var byName = string.Compare(a.DisplayName ?? a.Email ?? "￿", b.DisplayName ?? b.Email ?? "￿", StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.UserId!.Value.CompareTo(b.UserId!.Value);
        });

        foreach (var group in groups)
        {
            entries.Add(new AccessReviewEntry(
                AccessReviewPrincipal.Group, null, group, null, null,
                [.. assignments.Where(a => a.Group == group).Select(a => new AccessReviewRole(a.Role, null, a.At, a.By))],
                [.. wallMembers.Where(m => m.Group == group).Select(m => new AccessReviewWall(m.Wall, m.Name, null))],
                null, null,
                users.Values.Count(u => u.Groups.Contains(group, StringComparer.Ordinal))));
        }

        return entries;
    }

    public async Task<AuditExportPlan> PlanAuditExportAsync(
        Guid workspaceId, DateTimeOffset? periodStart, DateTimeOffset? periodEnd, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        long? first = null, last = null;
        long unsealed;
        await using (var command = tx.Command(
            """
            SELECT min(sequence), max(sequence), count(*) FILTER (WHERE sequence IS NULL)
            FROM audit.audit_event
            WHERE workspace_id = @ws AND (@from::timestamptz IS NULL OR occurred_at >= @from) AND (@to::timestamptz IS NULL OR occurred_at < @to)
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)periodStart ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)periodEnd ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.IsDBNull(0))
            {
                first = reader.GetInt64(0);
                last = reader.GetInt64(1);
            }

            unsealed = reader.GetInt64(2);
        }

        if (first is not { } s1 || last is not { } s2)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AuditExportPlan(1, 0, AuditChainFormat.Genesis, null, [], [], 0, unsealed);
        }

        var anchor = await CheckpointsAsync(tx, workspaceId, "sequence < @a ORDER BY sequence DESC LIMIT 1", s1, 0, cancellationToken).ConfigureAwait(false);
        var end = await CheckpointsAsync(tx, workspaceId, "sequence >= @a ORDER BY sequence LIMIT 1", s2, 0, cancellationToken).ConfigureAwait(false);
        var fromSequence = anchor.Count > 0 ? anchor[0].Sequence + 1 : 1;
        var toSequence = end.Count > 0 ? end[0].Sequence : s2;
        var checkpoints = await CheckpointsAsync(tx, workspaceId, "sequence >= @a AND sequence <= @b ORDER BY sequence", fromSequence, toSequence, cancellationToken)
            .ConfigureAwait(false);

        var gaps = new List<AuditChainGap>();
        await using (var command = tx.Command(
            """
            SELECT chain_id, first_sequence, last_sequence, last_event_hash, partition_name FROM audit.chain_gap
            WHERE chain_id = @ws AND last_sequence >= @a AND first_sequence <= @b ORDER BY first_sequence
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("a", fromSequence);
            command.Parameters.AddWithValue("b", toSequence);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                gaps.Add(new AuditChainGap(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetString(4)));
            }
        }

        long events;
        await using (var command = tx.Command("SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND sequence BETWEEN @a AND @b"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("a", fromSequence);
            command.Parameters.AddWithValue("b", toSequence);
            events = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AuditExportPlan(
            fromSequence, toSequence, anchor.Count > 0 ? anchor[0].Data.EventHash : AuditChainFormat.Genesis, anchor.Count > 0 ? anchor[0] : null,
            checkpoints, gaps, events, unsealed);
    }

    public async IAsyncEnumerable<AuditChainRow> ReadChainAsync(
        Guid workspaceId, long fromSequence, long toSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {AuditChainSql.EnvelopeColumns} FROM audit.audit_event
            WHERE workspace_id = @ws AND sequence BETWEEN @a AND @b
            ORDER BY sequence, event_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("a", fromSequence);
        command.Parameters.AddWithValue("b", toSequence);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = AuditChainSql.Read(reader);
                yield return new AuditChainRow(row.Envelope, row.Sequence, row.PrevHash, row.EventHash);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<AuditCheckpoint>> CheckpointsAsync(
        WorkspaceTransaction tx, Guid workspaceId, string condition, long a, long b, CancellationToken cancellationToken)
    {
        var list = new List<AuditCheckpoint>();
        await using var command = tx.Command($"SELECT {AuditChainSql.CheckpointColumns} FROM audit.checkpoint WHERE chain_id = @ws AND {condition}");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("a", a);
        command.Parameters.AddWithValue("b", b);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(AuditChainSql.ReadCheckpoint(reader));
        }

        return list;
    }
}

public static class ComplianceReportStoreRegistration
{
    public static IServiceCollection AddPostgresComplianceReports(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IComplianceReportStore, ComplianceReportStore>();
        return services;
    }
}
