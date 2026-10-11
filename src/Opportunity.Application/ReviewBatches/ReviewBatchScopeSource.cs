using Opportunity.Application.Search;

namespace Opportunity.Application.ReviewBatches;

/// <summary>
/// A batch's frozen members for a search within the batch (#178). Membership is a candidate set, never a grant: the
/// search filters it for the caller like any other search (ADR-015 D5.8, Q-52), so listing it needs nothing more than
/// the search itself.
/// </summary>
public sealed class ReviewBatchScopeSource(IReviewBatchStore store) : IReviewBatchScopeSource
{
    public async Task<IReadOnlyList<Guid>?> GetMembersAsync(SearchCaller caller, Guid batchId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await store.GetBatchAsync(caller.WorkspaceId, batchId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await store.ListMemberIdsAsync(caller.WorkspaceId, batchId, limit, cancellationToken).ConfigureAwait(false);
    }
}
