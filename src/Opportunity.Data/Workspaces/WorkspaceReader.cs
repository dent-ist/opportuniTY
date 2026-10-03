using Npgsql;

using Opportunity.Application.Workspaces;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Workspaces;

public sealed class WorkspaceReader(NpgsqlDataSource dataSource) : IWorkspaceReader
{
    public async Task<Workspace?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {WorkspaceColumns.Select} FROM opportunity.workspace w WHERE w.workspace_id = @ws");
        command.Parameters.AddWithValue("ws", workspaceId);
        Workspace? workspace = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                workspace = WorkspaceColumns.Read(reader);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return workspace;
    }
}

/// <summary>The workspace registry columns (alias <c>w</c>) and their mapping, shared by the reader and the store.</summary>
internal static class WorkspaceColumns
{
    public const string Select =
        """
        w.workspace_id, w.name, w.matter_number, w.display_time_zone, w.status, w.closed_at, w.epoch,
        w.control_number_case_sensitive, w.created_at, w.updated_at, w.storage_profile, w.row_version
        """;

    public const int Count = 12;

    public static Workspace Read(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        Name = reader.GetString(1),
        MatterNumber = reader.IsDBNull(2) ? null : reader.GetString(2),
        DisplayTimeZone = reader.GetString(3),
        Status = Enum.Parse<WorkspaceStatus>(reader.GetString(4)),
        ClosedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        Epoch = reader.GetInt64(6),
        ControlNumberCaseSensitive = reader.GetBoolean(7),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        StorageProfile = reader.GetString(10),
        RowVersion = reader.GetInt64(11),
    };
}
