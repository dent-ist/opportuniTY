using System.Data;

using Npgsql;

using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Search;

/// <summary>PostgreSQL <see cref="ISearchWatermarkReader"/>: the V0011 watermark read in one REPEATABLE READ snapshot.</summary>
public sealed class SearchWatermarkReader(NpgsqlDataSource dataSource) : ISearchWatermarkReader
{
    public async Task<SearchWatermark> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var watermark = await SearchWorkSql.ReadWatermarkAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return watermark;
    }
}
