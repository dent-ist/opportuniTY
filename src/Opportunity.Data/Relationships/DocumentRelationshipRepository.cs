using Npgsql;

using Opportunity.Application.Documents;
using Opportunity.Core.Documents;
using Opportunity.Core.SearchWork;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Relationships;

/// <summary>
/// PostgreSQL implementation of <see cref="IDocumentRelationshipRepository"/> over <c>duplicate_group</c>,
/// <c>email_thread</c> and the document relationship columns (V0016). Writes use <see cref="RelationshipWriter"/> in a
/// transaction of their own; documents whose primary flag changed get SearchOutbox rows (ADR-001 R1).
/// </summary>
public sealed class DocumentRelationshipRepository(NpgsqlDataSource dataSource) : IDocumentRelationshipRepository
{
    public async Task<RelationshipSyncResult> SyncAsync(Guid workspaceId, RelationshipSync sync, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sync);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var result = await RelationshipWriter.SyncAsync(tx, sync, cancellationToken).ConfigureAwait(false);
        await CommitWithSearchWorkAsync(tx, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FamilyResolutionSummary> ResolveFamiliesAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var families = await FamilyWriter.ResolveAllAsync(tx, cancellationToken).ConfigureAwait(false);
        var groups = families.DuplicateGroupIds.Count == 0
            ? null
            : await RelationshipWriter.SyncAsync(tx, new RelationshipSync([], [], families.ChangedDocuments, families.DuplicateGroupIds), cancellationToken)
                .ConfigureAwait(false);
        var changed = families.Bumped.ToDictionary(b => b.DocumentId, b => b.DocumentVersion);
        foreach (var (documentId, version) in groups?.OtherChangedDocuments ?? [])
        {
            changed[documentId] = version;
        }

        // Duplicate flags of the changed documents themselves bumped them again: take the latest version.
        if (changed.Count > 0)
        {
            await using var versions = tx.Command(
                "SELECT document_id, document_version FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = ANY(@ids)");
            versions.Parameters.AddWithValue("ws", workspaceId);
            versions.Parameters.AddWithValue("ids", changed.Keys.ToArray());
            await using var reader = await versions.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        await CommitWithSearchWorkAsync(
            tx, new RelationshipSyncResult(0, 0, changed.Count, [.. changed.Select(c => (c.Key, c.Value)).OrderBy(c => c.Key)]), cancellationToken)
            .ConfigureAwait(false);
        return new FamilyResolutionSummary(families.Documents, families.ChangedDocuments.Count, families.Issues);
    }

    public async Task<RelationshipSyncResult> RecomputeAllAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var result = await RelationshipWriter.RecomputeAllAsync(tx, cancellationToken).ConfigureAwait(false);
        await CommitWithSearchWorkAsync(tx, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<DuplicateGroupInfo?> GetDuplicateGroupAsync(Guid workspaceId, Guid duplicateGroupId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT duplicate_group_id, source, hash_kind, hash_value, primary_family_id, primary_document_id, member_count
            FROM opportunity.duplicate_group WHERE workspace_id = @ws AND duplicate_group_id = @id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", duplicateGroupId);
        DuplicateGroupInfo? group = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                group = new DuplicateGroupInfo(
                    reader.GetGuid(0), (DuplicateGroupSource)reader.GetInt16(1), (DuplicateHashKind)reader.GetInt16(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt32(6));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return group;
    }

    public async Task<EmailThreadInfo?> GetEmailThreadAsync(Guid workspaceId, Guid emailThreadId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT email_thread_id, source, thread_key, member_count FROM opportunity.email_thread WHERE workspace_id = @ws AND email_thread_id = @id");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", emailThreadId);
        EmailThreadInfo? thread = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                thread = new EmailThreadInfo(reader.GetGuid(0), (EmailThreadSource)reader.GetInt16(1), reader.GetString(2), reader.GetInt32(3));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return thread;
    }

    public async Task<IReadOnlyList<Guid>> GetDuplicateMembersAsync(
        Guid workspaceId, Guid duplicateGroupId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT d.document_id
            FROM opportunity.document d
            JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            WHERE d.workspace_id = @ws AND d.duplicate_group_id = @id AND NOT s.is_deleted
            ORDER BY d.is_duplicate_primary DESC, d.control_number_sort_key, d.document_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", duplicateGroupId);
        command.Parameters.AddWithValue("limit", limit);
        var members = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return members;
    }

    public async Task<RelationshipConsistencyReport> CheckConsistencyAsync(Guid workspaceId, int sampleSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleSize, 1000);

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);

        long[] totals;
        await using (var command = tx.Command(
            """
            SELECT (SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws),
                   (SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND duplicate_group_id IS NOT NULL),
                   (SELECT count(*) FROM opportunity.email_thread WHERE workspace_id = @ws),
                   (SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND email_thread_id IS NOT NULL)
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            totals = [reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)];
        }

        var findings = new List<RelationshipFinding>();
        foreach (var (kind, sql) in ConsistencyChecks.All)
        {
            await using var command = tx.Command(
                $"SELECT count(*), coalesce((array_agg(item ORDER BY item))[1:@sample], '{{}}') FROM ({sql}) AS finding(item)");
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("sample", sampleSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            findings.Add(new RelationshipFinding(kind, reader.GetInt64(0), sampleSize == 0 ? [] : reader.GetFieldValue<string[]>(1)));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RelationshipConsistencyReport(workspaceId, totals[0], totals[1], totals[2], totals[3], findings);
    }

    private static async Task CommitWithSearchWorkAsync(WorkspaceTransaction tx, RelationshipSyncResult result, CancellationToken cancellationToken)
    {
        if (result.OtherChangedDocuments.Count > 0)
        {
            await SearchWorkSql.AddOutboxRowsAsync(tx, result.OtherChangedDocuments, SearchChangeMask.Relationships, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
