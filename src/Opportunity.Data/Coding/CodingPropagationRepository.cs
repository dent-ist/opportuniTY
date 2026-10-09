using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Coding;

namespace Opportunity.Data.Coding;

/// <summary>
/// PostgreSQL side of coding propagation (E09-T05, V0039): the related documents of a source through the
/// (workspace_id, family_id) and (workspace_id, duplicate_group_id) indexes, and the stored previews.
/// </summary>
public sealed class CodingPropagationRepository(NpgsqlDataSource dataSource) : ICodingPropagationRepository
{
    public async Task<PropagationCandidates?> GetCandidatesAsync(
        Guid workspaceId, Guid sourceDocumentId, CodingPropagationScope scope, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Guid familyId;
        Guid? duplicateGroupId;
        await using (var source = tx.Command(
            """
            SELECT d.family_id, d.duplicate_group_id
            FROM opportunity.document d
            JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
            WHERE d.workspace_id = @ws AND d.document_id = @id
            """))
        {
            source.Parameters.AddWithValue("ws", workspaceId);
            source.Parameters.AddWithValue("id", sourceDocumentId);
            await using var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            familyId = reader.GetGuid(0);
            duplicateGroupId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }

        var useFamily = scope is CodingPropagationScope.Family or CodingPropagationScope.FamilyAndDuplicates;
        var useDuplicates = scope is CodingPropagationScope.Duplicates or CodingPropagationScope.FamilyAndDuplicates && duplicateGroupId is not null;
        var candidates = new List<PropagationCandidate>();
        await using (var command = tx.Command(
            """
            SELECT d.document_id, ps.document_version, d.control_number
            FROM opportunity.document d
            JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
            WHERE d.workspace_id = @ws AND d.document_id <> @id
              AND ((@family AND d.family_id = @family_id) OR (@duplicates AND d.duplicate_group_id = @group_id))
            ORDER BY d.document_id
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", sourceDocumentId);
            command.Parameters.AddWithValue("family", useFamily);
            command.Parameters.AddWithValue("family_id", familyId);
            command.Parameters.AddWithValue("duplicates", useDuplicates);
            command.Parameters.Add(new NpgsqlParameter("group_id", NpgsqlDbType.Uuid) { Value = (object?)duplicateGroupId ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new PropagationCandidate(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PropagationCandidates(candidates, candidates.Count > limit);
    }

    public async Task<IReadOnlyList<(Guid DuplicateGroupId, PropagationCandidate Member)>> GetDuplicateGroupMembersAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> duplicateGroupIds, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duplicateGroupIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var members = new List<(Guid, PropagationCandidate)>();
        if (duplicateGroupIds.Count == 0)
        {
            return members;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT d.duplicate_group_id, d.document_id, ps.document_version, d.control_number
            FROM opportunity.document d
            JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
            WHERE d.workspace_id = @ws AND d.duplicate_group_id = ANY(@groups)
            ORDER BY d.duplicate_group_id, d.document_id
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("groups", duplicateGroupIds.Distinct().ToArray());
            command.Parameters.AddWithValue("limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add((reader.GetGuid(0), new PropagationCandidate(reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3))));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return members;
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> GetDuplicateGroupIdsAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var groups = new Dictionary<Guid, Guid>();
        if (documentIds.Count == 0)
        {
            return groups;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT d.document_id, d.duplicate_group_id
            FROM opportunity.document d
            JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids) AND d.duplicate_group_id IS NOT NULL
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", documentIds.Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups[reader.GetGuid(0)] = reader.GetGuid(1);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return groups;
    }

    public async Task SaveAsync(CodingPropagationPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, preview.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var cleanup = tx.Command(
            "DELETE FROM opportunity.coding_propagation_preview WHERE workspace_id = @ws AND created_at < now() - interval '1 day'"))
        {
            cleanup.Parameters.AddWithValue("ws", preview.WorkspaceId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.coding_propagation_preview
                (workspace_id, preview_id, created_by, created_at, source_document_id, scope, operations, security_affecting,
                 target_ids, target_versions, mode, threshold)
            VALUES (@ws, @id, @by, @at, @source, @scope, @operations::jsonb, @security, @targets, @versions, @mode, @threshold)
            """))
        {
            insert.Parameters.AddWithValue("ws", preview.WorkspaceId);
            insert.Parameters.AddWithValue("id", preview.PreviewId);
            insert.Parameters.AddWithValue("by", preview.CreatedBy);
            insert.Parameters.AddWithValue("at", preview.CreatedAt.ToUniversalTime());
            insert.Parameters.AddWithValue("source", preview.SourceDocumentId);
            insert.Parameters.AddWithValue("scope", (short)preview.Scope);
            insert.Parameters.AddWithValue("operations", ToJson(preview.Fields));
            insert.Parameters.AddWithValue("security", preview.SecurityAffecting);
            insert.Parameters.AddWithValue("targets", preview.TargetIds.ToArray());
            insert.Parameters.AddWithValue("versions", preview.TargetVersions.ToArray());
            insert.Parameters.AddWithValue("mode", (short)preview.Mode);
            insert.Parameters.AddWithValue("threshold", preview.Threshold);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodingPropagationPreview?> GetAsync(Guid workspaceId, Guid previewId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT created_by, created_at, source_document_id, scope, operations::text, security_affecting, target_ids, target_versions,
                   mode, threshold
            FROM opportunity.coding_propagation_preview
            WHERE workspace_id = @ws AND preview_id = @id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", previewId);
        CodingPropagationPreview? preview = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                preview = new CodingPropagationPreview
                {
                    WorkspaceId = workspaceId,
                    PreviewId = previewId,
                    CreatedBy = reader.GetGuid(0),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(1),
                    SourceDocumentId = reader.GetGuid(2),
                    Scope = (CodingPropagationScope)reader.GetInt16(3),
                    Fields = FromJson(reader.GetString(4)),
                    SecurityAffecting = reader.GetBoolean(5),
                    TargetIds = reader.GetFieldValue<Guid[]>(6),
                    TargetVersions = reader.GetFieldValue<long[]>(7),
                    Mode = (CodingPropagationMode)reader.GetInt16(8),
                    Threshold = reader.GetInt32(9),
                };
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return preview;
    }

    private static string ToJson(IReadOnlyList<PropagationFieldValue> fields) =>
        new JsonArray([.. fields.Select(f => (JsonNode)new JsonObject
        {
            ["fieldId"] = f.FieldId,
            ["value"] = f.Value?.DeepClone(),
            ["changedAtVersion"] = f.ChangedAtVersion,
            ["originEventId"] = f.OriginEventId?.ToString("D"),
        })]).ToJsonString();

    private static List<PropagationFieldValue> FromJson(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().Select(o => new PropagationFieldValue(
            o["fieldId"]!.GetValue<int>(),
            o["value"]?.DeepClone(),
            o["changedAtVersion"]!.GetValue<long>(),
            o["originEventId"]?.GetValue<string>() is { } origin ? Guid.Parse(origin) : null))];
}
