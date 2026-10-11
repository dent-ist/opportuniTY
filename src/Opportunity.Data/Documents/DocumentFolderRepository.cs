using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Documents;

namespace Opportunity.Data.Documents;

/// <summary>PostgreSQL reads of the read-only folder tree (#179, V0068): one level at a time, by name.</summary>
public sealed class DocumentFolderRepository(NpgsqlDataSource dataSource) : IDocumentFolderStore
{
    private const string Columns = "folder_id, parent_folder_id, name, path, depth, ancestor_ids, path_key";

    public async Task<IReadOnlyList<DocumentFolderRow>> ListChildrenAsync(
        Guid workspaceId, Guid? parentFolderId, string? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.document_folder
            WHERE workspace_id = @ws
              AND coalesce(parent_folder_id, '00000000-0000-0000-0000-000000000000'::uuid) = @parent
              AND (@after::text IS NULL OR path_key > @after)
            ORDER BY path_key, folder_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("parent", parentFolderId ?? Guid.Empty);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<DocumentFolderRow>> GetAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderIds);
        if (folderIds.Count == 0)
        {
            return [];
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT {Columns} FROM opportunity.document_folder WHERE workspace_id = @ws AND folder_id = ANY(@ids) ORDER BY depth");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("ids", folderIds.Distinct().ToArray());
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static async Task<List<DocumentFolderRow>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<DocumentFolderRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DocumentFolderRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetFieldValue<Guid[]>(5),
                reader.GetString(6)));
        }

        return rows;
    }
}

public static class DocumentFolderRegistration
{
    /// <summary>The PostgreSQL <see cref="IDocumentFolderStore"/>.</summary>
    public static IServiceCollection AddPostgresDocumentFolderStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDocumentFolderStore>(sp => new DocumentFolderRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
