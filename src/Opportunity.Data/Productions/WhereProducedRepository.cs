using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Productions;
using Opportunity.Core.Productions;

namespace Opportunity.Data.Productions;

/// <summary>
/// PostgreSQL reads of the where-produced lookup and the Bates cross-reference (E13-T04): the document's family
/// (<c>family_id</c>) and duplicate group (<c>duplicate_group_id</c>) through their indexes, and the
/// <c>production_document</c> rows of produced productions (status Finalized or Voided) with their completed volume runs
/// (<c>export</c> rows with <c>production_id</c>). Each read is one workspace transaction (RLS).
/// </summary>
public sealed class WhereProducedRepository(NpgsqlDataSource dataSource) : IWhereProducedStore
{
    private const string Produced = "p.status IN (2, 3)";

    private const string RowColumns =
        """
        p.production_id, p.name, p.version, p.status, p.finalized_at, p.voided_at, pd.sequence, pd.document_id, d.control_number,
        pd.output, pd.prod_beg_bates, pd.prod_end_bates, pd.prod_beg_attach, pd.prod_end_attach
        """;

    public async Task<IReadOnlyList<WhereProducedDocument>?> ReadRelatedAsync(
        Guid workspaceId, Guid documentId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH anchor AS (
                SELECT a.family_id, a.duplicate_group_id FROM opportunity.document a WHERE a.workspace_id = @ws AND a.document_id = @doc
            ), related AS (
                SELECT d.document_id FROM opportunity.document d, anchor a WHERE d.workspace_id = @ws AND d.family_id = a.family_id
                UNION
                SELECT d.document_id FROM opportunity.document d, anchor a
                 WHERE a.duplicate_group_id IS NOT NULL AND d.workspace_id = @ws AND d.duplicate_group_id = a.duplicate_group_id
                UNION
                SELECT @doc FROM anchor
            )
            SELECT d.document_id, d.control_number, d.document_id = @doc, d.document_id <> @doc AND d.family_id = a.family_id,
                   d.document_id <> @doc AND a.duplicate_group_id IS NOT NULL AND d.duplicate_group_id IS NOT DISTINCT FROM a.duplicate_group_id
              FROM related r
              JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = r.document_id
             CROSS JOIN anchor a
             ORDER BY d.document_id = @doc DESC, d.family_id = a.family_id DESC, d.family_sequence, d.control_number_sort_key, d.document_id
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<WhereProducedDocument>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var relation = (reader.GetBoolean(2) ? WhereProducedRelation.Self : WhereProducedRelation.None)
                    | (reader.GetBoolean(3) ? WhereProducedRelation.Family : WhereProducedRelation.None)
                    | (reader.GetBoolean(4) ? WhereProducedRelation.Duplicate : WhereProducedRelation.None);
                rows.Add(new WhereProducedDocument(reader.GetGuid(0), reader.GetString(1), relation));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows;
    }

    public async Task<IReadOnlyList<ProducedDocumentRow>> ReadProducedAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return [];
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {RowColumns}
              FROM opportunity.production_document pd
              JOIN opportunity.production p ON p.workspace_id = pd.workspace_id AND p.production_id = pd.production_id
              LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
             WHERE pd.workspace_id = @ws AND pd.document_id = ANY(@docs) AND pd.beg_number IS NOT NULL AND {Produced}
             ORDER BY p.finalized_at, p.production_id, pd.sequence
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("docs", documentIds.ToArray());
        var rows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<ProducedDocumentRow>> ReadProducedPageAsync(
        Guid workspaceId, Guid? productionId, ProducedDocumentCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {RowColumns}
              FROM opportunity.production p
             CROSS JOIN LATERAL (
                   SELECT x.* FROM opportunity.production_document x
                    WHERE x.workspace_id = p.workspace_id AND x.production_id = p.production_id AND x.beg_number IS NOT NULL
                      AND (@after_id::uuid IS NULL OR x.production_id <> @after_id OR x.sequence > @after_seq)
                    ORDER BY x.sequence
                    LIMIT @limit) pd
              LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
             WHERE p.workspace_id = @ws AND {Produced} AND (@production::uuid IS NULL OR p.production_id = @production)
               AND (@after_id::uuid IS NULL OR (p.finalized_at, p.production_id) >= (@after_at, @after_id))
             ORDER BY p.finalized_at, p.production_id, pd.sequence
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(Nullable("production", NpgsqlDbType.Uuid, productionId));
        command.Parameters.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, after?.FinalizedAt));
        command.Parameters.Add(Nullable("after_id", NpgsqlDbType.Uuid, after?.ProductionId));
        command.Parameters.Add(Nullable("after_seq", NpgsqlDbType.Bigint, after?.Sequence));
        command.Parameters.AddWithValue("limit", limit);
        var rows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<ProducedVolumeRow>> ReadVolumesAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> productionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionIds);
        if (productionIds.Count == 0)
        {
            return [];
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT e.production_id, e.export_id, e.settings::text, e.completed_at
              FROM opportunity.export e
             WHERE e.workspace_id = @ws AND e.production_id = ANY(@productions) AND e.status = 2
             ORDER BY e.completed_at, e.export_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("productions", productionIds.ToArray());
        var rows = new List<ProducedVolumeRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProducedVolumeRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<bool> IsProducedAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT EXISTS (SELECT 1 FROM opportunity.production p WHERE p.workspace_id = @ws AND p.production_id = @id AND {Produced})");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        var produced = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return produced;
    }

    private static async Task<List<ProducedDocumentRow>> ReadRowsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<ProducedDocumentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProducedDocumentRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                (ProductionStatus)reader.GetInt16(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt64(6),
                reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                (ProductionOutputKind)reader.GetInt16(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13)));
        }

        return rows;
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}

public static class WhereProducedStoreRegistration
{
    public static IServiceCollection AddPostgresWhereProducedStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWhereProducedStore, WhereProducedRepository>();
        return services;
    }
}
