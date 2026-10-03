using System.Text.Json;

namespace Opportunity.Application.Identity;

/// <summary>
/// Per-user UI preferences (E15-T03): opaque JSON values the web client owns, keyed by name. Last write wins per key;
/// preferences are a convenience, not shared state, so they are not versioned.
/// </summary>
public interface IUserPreferenceStore
{
    Task<IReadOnlyDictionary<string, JsonElement>> GetAllAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces one value. Returns <c>false</c> (and stores nothing) when a new key would exceed <paramref name="maxKeys"/>.</summary>
    Task<bool> SetAsync(Guid userId, string key, JsonElement value, int maxKeys, CancellationToken cancellationToken = default);

    /// <summary>Removes one value (back to the client default). Removing a missing key is not an error.</summary>
    Task RemoveAsync(Guid userId, string key, CancellationToken cancellationToken = default);
}
