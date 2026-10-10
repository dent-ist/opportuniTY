using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Workspaces;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Workspaces;

/// <summary>
/// PostgreSQL <see cref="IAcknowledgmentStore"/> (E20-T03, V0058). Publishing locks the workspace row so concurrent
/// publications serialize and the loser gets a version conflict; an acceptance is inserted only for the current
/// version with its hash (the composite foreign key and the hash check keep it consistent with the text), together
/// with its audit event.
/// </summary>
public sealed class AcknowledgmentStore(NpgsqlDataSource dataSource) : IAcknowledgmentStore
{
    private const string VersionColumns =
        """
        v.workspace_id, v.version, v.title, v.text_sha256, v.published_by, v.published_at,
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = v.published_by),
        (SELECT count(*)::int FROM opportunity.acknowledgment a WHERE a.workspace_id = v.workspace_id AND a.version = v.version)
        """;

    private const string RosterSql =
        """
        WITH people AS (
            SELECT a.user_id FROM opportunity.workspace_role_assignment a WHERE a.workspace_id = @ws AND a.user_id IS NOT NULL
            UNION
            SELECT k.user_id FROM opportunity.acknowledgment k WHERE k.workspace_id = @ws
        ), named AS (
            SELECT p.user_id, u.display_name, u.email,
                   lower(coalesce(u.display_name, u.email, p.user_id::text)) AS sort_name,
                   EXISTS (SELECT FROM opportunity.workspace_role_assignment a
                           WHERE a.workspace_id = @ws AND a.user_id = p.user_id) AS direct
            FROM people p
            LEFT JOIN opportunity.app_user u ON u.user_id = p.user_id
        )
        SELECT n.user_id, n.display_name, n.email, n.sort_name, n.direct, (SELECT count(*)::int FROM named)
        FROM named n
        WHERE @after_name::text IS NULL OR (n.sort_name, n.user_id) > (@after_name::text, @after_id::uuid)
        ORDER BY n.sort_name, n.user_id
        LIMIT @limit
        """;

    public async Task<AcknowledgmentVersion?> GetCurrentAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadVersionAsync(tx, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<AcknowledgmentVersion?> GetVersionAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadVersionAsync(tx, version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<IReadOnlyList<AcknowledgmentVersion>> ListVersionsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT {VersionColumns}, NULL::text FROM opportunity.acknowledgment_version v WHERE v.workspace_id = @ws ORDER BY v.version DESC");
        command.Parameters.AddWithValue("ws", workspaceId);
        var versions = new List<AcknowledgmentVersion>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                versions.Add(ReadVersion(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return versions;
    }

    public async Task<AcknowledgmentPublishResult> PublishAsync(
        Guid workspaceId, int expectedCurrentVersion, string title, string body, string textSha256, Guid publishedBy,
        DateTimeOffset publishedAt, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var lockRow = tx.Command("SELECT 1 FROM opportunity.workspace WHERE workspace_id = @ws FOR NO KEY UPDATE"))
        {
            lockRow.Parameters.AddWithValue("ws", workspaceId);
            await lockRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await CurrentVersionAsync(tx, cancellationToken).ConfigureAwait(false);
        if (current != expectedCurrentVersion)
        {
            return new AcknowledgmentPublishResult(AcknowledgmentPublishOutcome.VersionConflict, null, current);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.acknowledgment_version (workspace_id, version, title, body, text_sha256, published_by, published_at)
            VALUES (@ws, @version, @title, @body, @hash, @by, @at)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("version", current + 1);
            insert.Parameters.AddWithValue("title", title);
            insert.Parameters.AddWithValue("body", body);
            insert.Parameters.AddWithValue("hash", textSha256);
            insert.Parameters.AddWithValue("by", publishedBy);
            insert.Parameters.AddWithValue("at", publishedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadVersionAsync(tx, current + 1, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AcknowledgmentPublishResult(AcknowledgmentPublishOutcome.Ok, saved, current + 1);
    }

    public async Task<AcknowledgmentAcceptance?> GetAcceptanceAsync(Guid workspaceId, Guid userId, int version, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadAcceptanceAsync(tx, userId, version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<AcknowledgmentAcceptResult> AcceptAsync(
        Guid workspaceId, Guid userId, int version, string textSha256, DateTimeOffset acceptedAt, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        int inserted;
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.acknowledgment (workspace_id, user_id, version, text_sha256, accepted_at, audit_event_id)
            SELECT v.workspace_id, @user, v.version, v.text_sha256, @at, @event
            FROM opportunity.acknowledgment_version v
            WHERE v.workspace_id = @ws AND v.version = @version AND v.text_sha256 = @hash
              AND v.version = (SELECT max(c.version) FROM opportunity.acknowledgment_version c WHERE c.workspace_id = @ws)
            ON CONFLICT (workspace_id, user_id, version) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("hash", textSha256);
            insert.Parameters.AddWithValue("at", acceptedAt);
            insert.Parameters.AddWithValue("event", audit.EventId);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        AcknowledgmentAcceptResult result;
        if (inserted == 1)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
            result = new AcknowledgmentAcceptResult(AcknowledgmentAcceptOutcome.Created,
                await ReadAcceptanceAsync(tx, userId, version, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            // Accepting the current version again is a no-op; an earlier version is outdated even if it was accepted once.
            var current = await CurrentVersionAsync(tx, cancellationToken).ConfigureAwait(false);
            var existing = current == version ? await ReadAcceptanceAsync(tx, userId, version, cancellationToken).ConfigureAwait(false) : null;
            result = current == 0 ? new AcknowledgmentAcceptResult(AcknowledgmentAcceptOutcome.NotRequired, null)
                : existing is not null && existing.TextSha256 == textSha256 ? new AcknowledgmentAcceptResult(AcknowledgmentAcceptOutcome.AlreadyAccepted, existing)
                : new AcknowledgmentAcceptResult(AcknowledgmentAcceptOutcome.Outdated, null);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<AcknowledgmentRosterPage> ListRosterAsync(
        Guid workspaceId, AcknowledgmentRosterPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var (entries, total) = await ReadRosterAsync(tx, after, limit + 1, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        AcknowledgmentRosterPosition? next = null;
        if (entries.Count > limit)
        {
            entries.RemoveAt(limit);
            var last = entries[^1];
            next = new AcknowledgmentRosterPosition(last.SortName, last.Entry.UserId);
        }

        return new AcknowledgmentRosterPage([.. entries.Select(e => e.Entry)], next, total);
    }

    public async Task<IReadOnlyList<AcknowledgmentRosterEntry>> ExportRosterAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var (entries, _) = await ReadRosterAsync(tx, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(e => e.Entry)];
    }

    private static async Task<(List<(string SortName, AcknowledgmentRosterEntry Entry)> Entries, int Total)> ReadRosterAsync(
        WorkspaceTransaction tx, AcknowledgmentRosterPosition? after, int limit, CancellationToken cancellationToken)
    {
        var people = new List<(Guid UserId, string? Name, string? Email, string SortName, bool Direct)>();
        var total = 0;
        await using (var command = tx.Command(RosterSql))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.Add(new NpgsqlParameter("after_name", NpgsqlDbType.Text) { Value = (object?)after?.SortName ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = (object?)after?.UserId ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", (long)limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                people.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.GetBoolean(4)));
                total = reader.GetInt32(5);
            }
        }

        var acceptances = new Dictionary<Guid, List<AcknowledgmentAcceptance>>();
        if (people.Count > 0)
        {
            await using var command = tx.Command(
                """
                SELECT a.user_id, a.version, a.text_sha256, a.accepted_at, a.audit_event_id
                FROM opportunity.acknowledgment a
                WHERE a.workspace_id = @ws AND a.user_id = ANY(@users)
                ORDER BY a.user_id, a.version DESC
                """);
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("users", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = [.. people.Select(p => p.UserId)] });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var acceptance = ReadAcceptance(reader);
                if (!acceptances.TryGetValue(acceptance.UserId, out var list))
                {
                    acceptances[acceptance.UserId] = list = [];
                }

                list.Add(acceptance);
            }
        }

        return ([.. people.Select(p => (p.SortName, new AcknowledgmentRosterEntry(
            p.UserId, p.Name, p.Email, p.Direct, acceptances.TryGetValue(p.UserId, out var list) ? list : [])))], total);
    }

    private static async Task<int> CurrentVersionAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT coalesce(max(version), 0) FROM opportunity.acknowledgment_version WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<AcknowledgmentVersion?> ReadVersionAsync(WorkspaceTransaction tx, int? version, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            SELECT {VersionColumns}, v.body FROM opportunity.acknowledgment_version v
            WHERE v.workspace_id = @ws AND (@version::int IS NULL OR v.version = @version::int)
            ORDER BY v.version DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = (object?)version ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    private static async Task<AcknowledgmentAcceptance?> ReadAcceptanceAsync(WorkspaceTransaction tx, Guid userId, int version, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT a.user_id, a.version, a.text_sha256, a.accepted_at, a.audit_event_id
            FROM opportunity.acknowledgment a
            WHERE a.workspace_id = @ws AND a.user_id = @user AND a.version = @version
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAcceptance(reader) : null;
    }

    private static AcknowledgmentVersion ReadVersion(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        Version = reader.GetInt32(1),
        Title = reader.GetString(2),
        TextSha256 = reader.GetString(3),
        PublishedBy = reader.GetGuid(4),
        PublishedAt = reader.GetFieldValue<DateTimeOffset>(5),
        PublishedByName = reader.IsDBNull(6) ? null : reader.GetString(6),
        AcceptedCount = reader.GetInt32(7),
        Body = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static AcknowledgmentAcceptance ReadAcceptance(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetGuid(4));
}

public static class AcknowledgmentStoreRegistration
{
    public static IServiceCollection AddPostgresAcknowledgments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAcknowledgmentStore, AcknowledgmentStore>();
        return services;
    }
}
