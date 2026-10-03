using System.Text.Json;

namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/me/preferences</c>: the signed-in user's UI preferences (E15-T03). Each value is JSON the web client
/// owns and validates (keyboard bindings, theme and density, pane sizes, grid views); a missing key means "default".
/// </summary>
/// <param name="Values">Preference values by key.</param>
public sealed record UserPreferencesResource(IReadOnlyDictionary<string, JsonElement> Values);
