using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Identity;
using Opportunity.Contracts.Api;
using Opportunity.Data.Identity;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Preferences;

/// <summary>
/// The signed-in user's UI preferences (E15-T03): <c>GET /api/v1/me/preferences</c>, <c>PUT</c> and <c>DELETE</c>
/// <c>/api/v1/me/preferences/{key}</c>. Values are opaque JSON the web client owns (keyboard bindings, theme, pane
/// sizes, grid views). Only the caller's own preferences are reachable (<see cref="OwnProfileAuthorization"/>). Last
/// write wins per key; there is no ETag because two tabs of one user disagreeing about a pane size is not a conflict
/// worth a 412. Changes are not audited: they carry no workspace data and change no access.
/// </summary>
public sealed partial class UserPreferenceEndpoints : IApiEndpointModule
{
    public const string Path = "/me/preferences";

    /// <summary>Largest value, as JSON text. A full key map with every command rebound is well under 4 KiB.</summary>
    public const int MaxValueLength = 16 * 1024;

    public const int MaxKeys = 100;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.V1.MapGroup(Path).WithTags("Preferences").RequireOwnProfile();

        group.MapGet(string.Empty, GetAllAsync)
            .WithName("GetUserPreferences")
            .WithSummary("The signed-in user's UI preferences, by key.")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPut("/{key}", SetAsync)
            .WithName("SetUserPreference")
            .WithSummary("Store one preference value (any JSON except null, at most 16 KiB).")
            .WithDescription("Keys start with a letter and use letters, digits, '.', '_' and '-' (at most 100 characters); at most 100 keys per user.")
            .WithMetadata(new RequestSizeLimitAttribute(MaxValueLength * 2))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{key}", RemoveAsync)
            .WithName("DeleteUserPreference")
            .WithSummary("Remove one preference, restoring the default.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    internal static async Task<Ok<UserPreferencesResource>> GetAllAsync(
        HttpContext context, IUserPreferenceStore store, CancellationToken cancellationToken)
    {
        var values = await store.GetAllAsync(context.GetOwnUserId(), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new UserPreferencesResource(values));
    }

    internal static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetAsync(
        string key, [FromBody] JsonElement value, HttpContext context, IUserPreferenceStore store, CancellationToken cancellationToken)
    {
        if (ValidateKey(key) is { } invalid)
        {
            return invalid;
        }

        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["value"] = ["Send a JSON value; use DELETE to restore the default."] });
        }

        if (value.GetRawText().Length > MaxValueLength)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["value"] = [$"A preference value is at most {MaxValueLength / 1024} KiB of JSON."] });
        }

        if (!await store.SetAsync(context.GetOwnUserId(), key, value, MaxKeys, cancellationToken).ConfigureAwait(false))
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, $"At most {MaxKeys} preferences can be stored per user.");
        }

        return TypedResults.NoContent();
    }

    internal static async Task<Results<NoContent, ValidationProblem>> RemoveAsync(
        string key, HttpContext context, IUserPreferenceStore store, CancellationToken cancellationToken)
    {
        if (ValidateKey(key) is { } invalid)
        {
            return invalid;
        }

        await store.RemoveAsync(context.GetOwnUserId(), key, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? ValidateKey(string key) =>
        KeyPattern().IsMatch(key)
            ? null
            : Problems.Validation(new Dictionary<string, string[]>
            {
                ["key"] = ["A preference key starts with a letter and uses letters, digits, '.', '_' and '-' (at most 100 characters)."],
            });

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

public static class UserPreferenceEndpointRegistration
{
    public static IServiceCollection AddUserPreferenceEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUserPreferenceStore, PostgresUserPreferenceStore>();
        services.AddSingleton<IApiEndpointModule, UserPreferenceEndpoints>();
        return services;
    }
}
