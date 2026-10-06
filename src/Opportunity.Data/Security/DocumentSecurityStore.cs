using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Security;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Security;

/// <summary>
/// PostgreSQL <see cref="IDocumentSecurityStore"/> (V0012, V0044). Each write locks what it changes, checks the
/// version, writes its audit event and, when document visibility changes, the restriction classes or wall coverage of
/// the affected documents and their security-lane search work, all in one workspace transaction (§24 rule 1).
/// </summary>
public sealed class DocumentSecurityStore(NpgsqlDataSource dataSource) : IDocumentSecurityStore
{
    private const string DocumentsChangedDetail = "DocumentsChanged";

    // ----- Restriction classes -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RestrictionClassState>> ListClassesAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var classes = await ReadClassesAsync(tx, null, false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return classes;
    }

    public async Task<SecurityWrite<RestrictionClassState>> PutClassAsync(
        Guid workspaceId, string classKey, RestrictionClassDefinition definition, long? expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadClassesAsync(tx, classKey, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null && expectedVersion is not null)
        {
            return new(SecurityWriteStatus.NotFound);
        }

        if (current is not null && current.Version != expectedVersion)
        {
            return new(SecurityWriteStatus.VersionConflict);
        }

        await using (var upsert = tx.Command(
            """
            INSERT INTO opportunity.restriction_class (workspace_id, class_key, display_name, is_builtin, updated_by)
            VALUES (@ws, @class, @name, false, @actor)
            ON CONFLICT (workspace_id, class_key) DO UPDATE
                SET display_name = EXCLUDED.display_name, updated_by = EXCLUDED.updated_by, updated_at = now(),
                    version = restriction_class.version + 1;
            DELETE FROM opportunity.restriction_class_grant WHERE workspace_id = @ws AND class_key = @class;
            INSERT INTO opportunity.restriction_class_grant (workspace_id, class_key, role)
            SELECT @ws, @class, r FROM unnest(@roles::text[]) AS r;
            """))
        {
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("class", classKey);
            upsert.Parameters.AddWithValue("name", definition.DisplayName);
            upsert.Parameters.AddWithValue("actor", actorId);
            upsert.Parameters.AddWithValue("roles", definition.Roles.Select(r => r.Key()).ToArray());
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Guid> changed = [];
        var rulesChanged = current is null
            ? definition.Rules.Count > 0
            : !current.Definition.Rules.ToHashSet().SetEquals(definition.Rules);
        if (rulesChanged)
        {
            await using (var rules = tx.Command(
                """
                DELETE FROM opportunity.restriction_class_rule WHERE workspace_id = @ws AND class_key = @class;
                INSERT INTO opportunity.restriction_class_rule (workspace_id, class_key, field_id, choice_id)
                SELECT @ws, @class, f, c FROM unnest(@fields::integer[], @choices::integer[]) AS u(f, c);
                """))
            {
                rules.Parameters.AddWithValue("ws", workspaceId);
                rules.Parameters.AddWithValue("class", classKey);
                rules.Parameters.AddWithValue("fields", definition.Rules.Select(r => r.FieldId).ToArray());
                rules.Parameters.AddWithValue("choices", definition.Rules.Select(r => r.ChoiceId).ToArray());
                await rules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            changed = await DocumentSecuritySql.ResyncClassAsync(tx, classKey, cancellationToken).ConfigureAwait(false);
        }

        var saved = (await ReadClassesAsync(tx, classKey, false, cancellationToken).ConfigureAwait(false)).Single();
        await AuditSql.InsertAsync(tx, WithChanged(audit, changed.Count), cancellationToken).ConfigureAwait(false);
        await DocumentSecuritySql.ReprojectAsync(tx, changed, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(current is null ? SecurityWriteStatus.Created : SecurityWriteStatus.Ok, saved, changed.Count);
    }

    public async Task<SecurityWrite<RestrictionClassState>> DeleteClassAsync(
        Guid workspaceId, string classKey, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadClassesAsync(tx, classKey, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null || current.IsBuiltIn)
        {
            return new(SecurityWriteStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(SecurityWriteStatus.VersionConflict);
        }

        var changed = new List<Guid>();
        await using (var delete = tx.Command(
            """
            DELETE FROM opportunity.document_restriction WHERE workspace_id = @ws AND class_key = @class RETURNING document_id
            """))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("class", classKey);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed.Add(reader.GetGuid(0));
            }
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.restriction_class WHERE workspace_id = @ws AND class_key = @class"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("class", classKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, WithChanged(audit, changed.Count), cancellationToken).ConfigureAwait(false);
        await DocumentSecuritySql.ReprojectAsync(tx, changed, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SecurityWriteStatus.Ok, current, changed.Count);
    }

    private static async Task<List<RestrictionClassState>> ReadClassesAsync(
        WorkspaceTransaction tx, string? classKey, bool lockRow, CancellationToken cancellationToken)
    {
        var classes = new List<RestrictionClassState>();
        await using var command = tx.Command(
            $"""
            SELECT c.class_key, c.display_name, c.is_builtin, c.updated_at, c.version,
                   ARRAY(SELECT g.role FROM opportunity.restriction_class_grant g
                         WHERE g.workspace_id = c.workspace_id AND g.class_key = c.class_key ORDER BY g.role),
                   ARRAY(SELECT r.field_id FROM opportunity.restriction_class_rule r
                         WHERE r.workspace_id = c.workspace_id AND r.class_key = c.class_key ORDER BY r.field_id, r.choice_id),
                   ARRAY(SELECT r.choice_id FROM opportunity.restriction_class_rule r
                         WHERE r.workspace_id = c.workspace_id AND r.class_key = c.class_key ORDER BY r.field_id, r.choice_id)
            FROM opportunity.restriction_class c
            WHERE c.workspace_id = @ws {(classKey is null ? string.Empty : "AND c.class_key = @class")}
            ORDER BY c.is_builtin DESC, c.class_key
            {(lockRow ? "FOR UPDATE OF c" : string.Empty)}
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        if (classKey is not null)
        {
            command.Parameters.AddWithValue("class", classKey);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fields = reader.GetFieldValue<int[]>(6);
            var choices = reader.GetFieldValue<int[]>(7);
            classes.Add(new RestrictionClassState(
                reader.GetString(0),
                reader.GetBoolean(2),
                new RestrictionClassDefinition(reader.GetString(1), Roles(reader.GetFieldValue<string[]>(5)),
                    [.. fields.Select((f, i) => new SecurityChoiceRef(f, choices[i]))]),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt64(4)));
        }

        return classes;
    }

    // ----- Ethical walls -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<EthicalWallState>> ListWallsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var walls = await ReadWallsAsync(tx, null, false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return walls;
    }

    public async Task<EthicalWallState?> GetWallAsync(Guid workspaceId, Guid wallId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var walls = await ReadWallsAsync(tx, wallId, false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return walls.SingleOrDefault();
    }

    public async Task<SecurityWrite<EthicalWallState>> CreateWallAsync(
        Guid workspaceId, Guid wallId, EthicalWallDefinition definition, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await UnknownDocumentsAsync(tx, definition.DocumentIds, cancellationToken).ConfigureAwait(false) is { Count: > 0 } unknown)
        {
            return new(SecurityWriteStatus.UnknownDocuments) { UnknownDocuments = unknown };
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name, description, created_by, updated_by)
            VALUES (@ws, @wall, @name, @description, @actor, @actor)
            ON CONFLICT (workspace_id, name) DO NOTHING
            """))
        {
            BindWall(insert.Parameters, workspaceId, wallId, definition, actorId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return new(SecurityWriteStatus.NameTaken);
            }
        }

        return await FinishWallAsync(tx, wallId, definition, audit, SecurityWriteStatus.Created, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecurityWrite<EthicalWallState>> UpdateWallAsync(
        Guid workspaceId, Guid wallId, EthicalWallDefinition definition, long expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadWallsAsync(tx, wallId, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null)
        {
            return new(SecurityWriteStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(SecurityWriteStatus.VersionConflict);
        }

        if (await UnknownDocumentsAsync(tx, definition.DocumentIds, cancellationToken).ConfigureAwait(false) is { Count: > 0 } unknown)
        {
            return new(SecurityWriteStatus.UnknownDocuments) { UnknownDocuments = unknown };
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.ethical_wall
               SET name = @name, description = @description, updated_by = @actor, updated_at = now(), version = version + 1
             WHERE workspace_id = @ws AND wall_id = @wall
               AND NOT EXISTS (SELECT FROM opportunity.ethical_wall o WHERE o.workspace_id = @ws AND o.name = @name AND o.wall_id <> @wall)
            """))
        {
            BindWall(update.Parameters, workspaceId, wallId, definition, actorId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return new(SecurityWriteStatus.NameTaken);
            }
        }

        await using (var clear = tx.Command(
            """
            DELETE FROM opportunity.ethical_wall_member WHERE workspace_id = @ws AND wall_id = @wall;
            DELETE FROM opportunity.ethical_wall_scope WHERE workspace_id = @ws AND wall_id = @wall;
            """))
        {
            clear.Parameters.AddWithValue("ws", workspaceId);
            clear.Parameters.AddWithValue("wall", wallId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FinishWallAsync(tx, wallId, definition, audit, SecurityWriteStatus.Ok, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecurityWrite<EthicalWallState>> DeleteWallAsync(
        Guid workspaceId, Guid wallId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadWallsAsync(tx, wallId, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null)
        {
            return new(SecurityWriteStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(SecurityWriteStatus.VersionConflict);
        }

        var changed = new List<Guid>();
        await using (var delete = tx.Command(
            """
            DELETE FROM opportunity.document_wall WHERE workspace_id = @ws AND wall_id = @wall RETURNING document_id
            """))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("wall", wallId);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed.Add(reader.GetGuid(0));
            }
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.ethical_wall WHERE workspace_id = @ws AND wall_id = @wall"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("wall", wallId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, WithChanged(audit, changed.Count), cancellationToken).ConfigureAwait(false);
        await DocumentSecuritySql.ReprojectAsync(tx, changed, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SecurityWriteStatus.Ok, current, changed.Count);
    }

    private static void BindWall(NpgsqlParameterCollection parameters, Guid workspaceId, Guid wallId, EthicalWallDefinition definition, Guid actorId)
    {
        parameters.AddWithValue("ws", workspaceId);
        parameters.AddWithValue("wall", wallId);
        parameters.AddWithValue("name", definition.Name);
        parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)definition.Description ?? DBNull.Value });
        parameters.AddWithValue("actor", actorId);
    }

    /// <summary>Members and scope rows, coverage of the whole workspace, audit and re-projection; then commits.</summary>
    private static async Task<SecurityWrite<EthicalWallState>> FinishWallAsync(
        WorkspaceTransaction tx, Guid wallId, EthicalWallDefinition definition, AuditEvent audit, SecurityWriteStatus status,
        CancellationToken cancellationToken)
    {
        await using (var rows = tx.Command(
            """
            INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id)
            SELECT @ws, @wall, gen_random_uuid(), u FROM unnest(@users::uuid[]) AS u;
            INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, group_name)
            SELECT @ws, @wall, gen_random_uuid(), g FROM unnest(@groups::text[]) AS g;
            INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, document_id)
            SELECT @ws, @wall, gen_random_uuid(), 1, d FROM unnest(@documents::uuid[]) AS d;
            INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, custodian)
            SELECT @ws, @wall, gen_random_uuid(), 2, c FROM unnest(@custodians::text[]) AS c;
            INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, field_id, choice_id)
            SELECT @ws, @wall, gen_random_uuid(), 3, f, c FROM unnest(@fields::integer[], @choices::integer[]) AS u(f, c);
            """))
        {
            rows.Parameters.AddWithValue("ws", tx.WorkspaceId);
            rows.Parameters.AddWithValue("wall", wallId);
            rows.Parameters.AddWithValue("users", definition.UserIds.ToArray());
            rows.Parameters.AddWithValue("groups", definition.Groups.ToArray());
            rows.Parameters.AddWithValue("documents", definition.DocumentIds.ToArray());
            rows.Parameters.AddWithValue("custodians", definition.Custodians.ToArray());
            rows.Parameters.AddWithValue("fields", definition.Choices.Select(c => c.FieldId).ToArray());
            rows.Parameters.AddWithValue("choices", definition.Choices.Select(c => c.ChoiceId).ToArray());
            await rows.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Coverage of the whole workspace: a removed scope leaves stale rows anywhere, a new one may cover anything.
        var changed = await DocumentSecuritySql.SyncWallsAsync(tx, null, cancellationToken).ConfigureAwait(false);
        var saved = (await ReadWallsAsync(tx, wallId, false, cancellationToken).ConfigureAwait(false)).Single();
        await AuditSql.InsertAsync(tx, WithChanged(audit, changed.Count), cancellationToken).ConfigureAwait(false);
        await DocumentSecuritySql.ReprojectAsync(tx, changed, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(status, saved, changed.Count);
    }

    private static async Task<IReadOnlyList<Guid>> UnknownDocumentsAsync(
        WorkspaceTransaction tx, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var unknown = new List<Guid>();
        await using var command = tx.Command(
            """
            SELECT u.id FROM unnest(@ids::uuid[]) AS u(id)
            WHERE NOT EXISTS (
                SELECT FROM opportunity.document d
                LEFT JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
                WHERE d.workspace_id = @ws AND d.document_id = u.id AND s.is_deleted IS NOT TRUE)
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("ids", documentIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            unknown.Add(reader.GetGuid(0));
        }

        return unknown;
    }

    private static async Task<List<EthicalWallState>> ReadWallsAsync(WorkspaceTransaction tx, Guid? wallId, bool lockRow, CancellationToken cancellationToken)
    {
        var walls = new List<EthicalWallState>();
        await using var command = tx.Command(
            $"""
            SELECT w.wall_id, w.name, w.description, w.updated_at, w.version,
                   ARRAY(SELECT m.user_id FROM opportunity.ethical_wall_member m
                         WHERE m.workspace_id = w.workspace_id AND m.wall_id = w.wall_id AND m.user_id IS NOT NULL ORDER BY m.user_id),
                   ARRAY(SELECT m.group_name FROM opportunity.ethical_wall_member m
                         WHERE m.workspace_id = w.workspace_id AND m.wall_id = w.wall_id AND m.group_name IS NOT NULL ORDER BY m.group_name),
                   ARRAY(SELECT s.document_id FROM opportunity.ethical_wall_scope s
                         WHERE s.workspace_id = w.workspace_id AND s.wall_id = w.wall_id AND s.kind = 1 ORDER BY s.document_id),
                   ARRAY(SELECT s.custodian FROM opportunity.ethical_wall_scope s
                         WHERE s.workspace_id = w.workspace_id AND s.wall_id = w.wall_id AND s.kind = 2 ORDER BY s.custodian),
                   ARRAY(SELECT s.field_id FROM opportunity.ethical_wall_scope s
                         WHERE s.workspace_id = w.workspace_id AND s.wall_id = w.wall_id AND s.kind = 3 ORDER BY s.field_id, s.choice_id),
                   ARRAY(SELECT s.choice_id FROM opportunity.ethical_wall_scope s
                         WHERE s.workspace_id = w.workspace_id AND s.wall_id = w.wall_id AND s.kind = 3 ORDER BY s.field_id, s.choice_id)
            FROM opportunity.ethical_wall w
            WHERE w.workspace_id = @ws {(wallId is null ? string.Empty : "AND w.wall_id = @wall")}
            ORDER BY lower(w.name), w.wall_id
            {(lockRow ? "FOR UPDATE OF w" : string.Empty)}
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        if (wallId is { } id)
        {
            command.Parameters.AddWithValue("wall", id);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fields = reader.GetFieldValue<int[]>(9);
            var choices = reader.GetFieldValue<int[]>(10);
            walls.Add(new EthicalWallState(
                reader.GetGuid(0),
                new EthicalWallDefinition(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetFieldValue<Guid[]>(5),
                    reader.GetFieldValue<string[]>(6),
                    reader.GetFieldValue<Guid[]>(7),
                    reader.GetFieldValue<string[]>(8),
                    [.. fields.Select((f, i) => new SecurityChoiceRef(f, choices[i]))]),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt64(4)));
        }

        return walls;
    }

    // ----- Field restrictions ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<FieldRestrictionState>> ListFieldRestrictionsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var restrictions = await ReadFieldRestrictionsAsync(tx, null, false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return restrictions;
    }

    public async Task<SecurityWrite<FieldRestrictionState>> PutFieldRestrictionAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<WorkspaceRole> visibleTo, IReadOnlyList<WorkspaceRole> editableBy, long? expectedVersion,
        Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visibleTo);
        ArgumentNullException.ThrowIfNull(editableBy);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadFieldRestrictionsAsync(tx, fieldId, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if ((current is null && expectedVersion is not null) || (current is not null && current.Version != expectedVersion))
        {
            return new(current is null ? SecurityWriteStatus.NotFound : SecurityWriteStatus.VersionConflict);
        }

        await using (var upsert = tx.Command(
            """
            INSERT INTO opportunity.field_security (workspace_id, field_id, visible_roles, editable_roles, updated_by)
            VALUES (@ws, @field, @visible, @editable, @actor)
            ON CONFLICT (workspace_id, field_id) DO UPDATE
                SET visible_roles = EXCLUDED.visible_roles, editable_roles = EXCLUDED.editable_roles, updated_by = EXCLUDED.updated_by,
                    updated_at = now(), version = field_security.version + 1
            """))
        {
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("field", fieldId);
            upsert.Parameters.AddWithValue("visible", visibleTo.Select(r => r.Key()).ToArray());
            upsert.Parameters.AddWithValue("editable", editableBy.Select(r => r.Key()).ToArray());
            upsert.Parameters.AddWithValue("actor", actorId);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = (await ReadFieldRestrictionsAsync(tx, fieldId, false, cancellationToken).ConfigureAwait(false)).Single();
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(current is null ? SecurityWriteStatus.Created : SecurityWriteStatus.Ok, saved);
    }

    public async Task<SecurityWrite<FieldRestrictionState>> DeleteFieldRestrictionAsync(
        Guid workspaceId, int fieldId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = (await ReadFieldRestrictionsAsync(tx, fieldId, true, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null)
        {
            return new(SecurityWriteStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(SecurityWriteStatus.VersionConflict);
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.field_security WHERE workspace_id = @ws AND field_id = @field"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("field", fieldId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SecurityWriteStatus.Ok, current);
    }

    public async Task<FieldAccessState> ReadFieldAccessAsync(Guid workspaceId, SecurityPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var restrictions = await ReadFieldRestrictionsAsync(tx, null, false, cancellationToken).ConfigureAwait(false);
        var roles = new HashSet<WorkspaceRole>();
        if (restrictions.Count > 0)
        {
            await using var command = tx.Command(
                """
                SELECT DISTINCT a.role FROM opportunity.workspace_role_assignment a
                WHERE a.workspace_id = @ws AND (a.user_id = @user OR a.group_name = ANY (@groups))
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("user", principal.UserId);
            command.Parameters.Add(new NpgsqlParameter<string[]>("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. principal.Groups] });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (RoleCatalog.TryParse(reader.GetString(0), out var role))
                {
                    roles.Add(role);
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FieldAccessState(roles, restrictions);
    }

    private static async Task<List<FieldRestrictionState>> ReadFieldRestrictionsAsync(
        WorkspaceTransaction tx, int? fieldId, bool lockRow, CancellationToken cancellationToken)
    {
        var restrictions = new List<FieldRestrictionState>();
        await using var command = tx.Command(
            $"""
            SELECT field_id, visible_roles, editable_roles, updated_at, version FROM opportunity.field_security
            WHERE workspace_id = @ws {(fieldId is null ? string.Empty : "AND field_id = @field")}
            ORDER BY field_id
            {(lockRow ? "FOR UPDATE" : string.Empty)}
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        if (fieldId is { } id)
        {
            command.Parameters.AddWithValue("field", id);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            restrictions.Add(new FieldRestrictionState(
                reader.GetInt32(0), Roles(reader.GetFieldValue<string[]>(1)), Roles(reader.GetFieldValue<string[]>(2)),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt64(4)));
        }

        return restrictions;
    }

    // ----- Break-glass -------------------------------------------------------------------------------------------

    private const string BreakGlassSelect =
        """
        SELECT b.activation_id, b.user_id, coalesce(u.display_name, u.email), b.reason, b.activated_at, b.expires_at, b.ended_at, b.ended_reason
        FROM opportunity.break_glass_activation b
        LEFT JOIN opportunity.app_user u ON u.user_id = b.user_id
        """;

    public async Task<SecurityWrite<BreakGlassActivationState>> ActivateBreakGlassAsync(
        Guid workspaceId, Guid activationId, Guid userId, string reason, TimeSpan duration, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var serialize = tx.Command("SELECT pg_advisory_xact_lock(hashtextextended('break-glass:' || @ws::text || ':' || @user::text, 0))"))
        {
            serialize.Parameters.AddWithValue("ws", workspaceId);
            serialize.Parameters.AddWithValue("user", userId);
            await serialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.break_glass_activation (workspace_id, activation_id, user_id, reason, activated_at, expires_at)
            SELECT @ws, @id, @user, @reason, now(), now() + @duration
            WHERE NOT EXISTS (
                SELECT FROM opportunity.break_glass_activation b
                WHERE b.workspace_id = @ws AND b.user_id = @user AND b.ended_at IS NULL AND b.expires_at > now())
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", activationId);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("duration", duration);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) <= 0)
            {
                return new(SecurityWriteStatus.AlreadyActive);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = (await ReadBreakGlassAsync(tx, "AND b.activation_id = @id", p => p.AddWithValue("id", activationId), cancellationToken)
            .ConfigureAwait(false)).Single();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SecurityWriteStatus.Created, saved);
    }

    public async Task<SecurityWrite<BreakGlassActivationState>> EndBreakGlassAsync(
        Guid workspaceId, Guid activationId, string endedReason, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.break_glass_activation SET ended_at = now(), ended_reason = @reason
            WHERE workspace_id = @ws AND activation_id = @id AND ended_at IS NULL AND expires_at > now()
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", activationId);
            update.Parameters.AddWithValue("reason", endedReason);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return new(SecurityWriteStatus.NotFound);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = (await ReadBreakGlassAsync(tx, "AND b.activation_id = @id", p => p.AddWithValue("id", activationId), cancellationToken)
            .ConfigureAwait(false)).Single();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SecurityWriteStatus.Ok, saved);
    }

    public async Task<BreakGlassActivationState?> GetBreakGlassAsync(Guid workspaceId, Guid activationId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadBreakGlassAsync(tx, "AND b.activation_id = @id", p => p.AddWithValue("id", activationId), cancellationToken)
            .ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found.SingleOrDefault();
    }

    public async Task<IReadOnlyList<BreakGlassActivationState>> ListBreakGlassAsync(
        Guid workspaceId, Guid? userId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadBreakGlassAsync(
            tx,
            (userId is null ? string.Empty : "AND b.user_id = @user") + " ORDER BY b.activated_at DESC, b.activation_id DESC LIMIT @limit",
            p =>
            {
                if (userId is { } id)
                {
                    p.AddWithValue("user", id);
                }

                p.AddWithValue("limit", limit);
            },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    private static async Task<List<BreakGlassActivationState>> ReadBreakGlassAsync(
        WorkspaceTransaction tx, string filter, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var found = new List<BreakGlassActivationState>();
        await using var command = tx.Command($"{BreakGlassSelect} WHERE b.workspace_id = @ws {filter}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(new BreakGlassActivationState(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return found;
    }

    // ----- Shared ------------------------------------------------------------------------------------------------

    private static List<WorkspaceRole> Roles(string[] keys) =>
        [.. keys.Select(k => RoleCatalog.TryParse(k, out var role) ? role : (WorkspaceRole?)null).OfType<WorkspaceRole>().Order()];

    private static AuditEvent WithChanged(AuditEvent audit, int changed) =>
        audit with
        {
            Details = new Dictionary<string, string?>(audit.Details)
            {
                [DocumentsChangedDetail] = changed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
}
