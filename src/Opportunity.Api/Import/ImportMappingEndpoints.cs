using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Opportunity.Api.Conventions;
using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Core.Security;
using Opportunity.Data.Fields;
using Opportunity.Data.Import;
using Opportunity.Hosting.Options;
using Opportunity.Import.Mapping;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Import;

/// <summary>Load-file import settings, section <c>Import</c>.</summary>
public sealed class ImportApiOptions
{
    public const string SectionName = "Import";

    /// <summary>Largest DAT sample accepted by the mapping preview; clients send the leading bytes of larger files.</summary>
    [Range(1024, 256L * 1024 * 1024)]
    public long MaxPreviewSampleBytes { get; set; } = 16L * 1024 * 1024;
}

/// <summary>
/// Field mapping for load-file import (E08-T02, guide §5.1 step 3): the targets a column can map to, saved import
/// profiles (export = GET, import/copy to another workspace = POST of that JSON) and the coercion preview.
/// </summary>
public sealed class ImportMappingEndpoints : IApiEndpointModule
{
    public const string ProfilesPath = "/import-profiles";
    public const string TargetsPath = "/import-targets";
    public const string PreviewPath = "/import-mapping-previews";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // Import setup (targets, profiles, previews) is part of running an import: Import.Run (E05-T02).
        var group = routes.Workspace.MapGroup(string.Empty).RequirePermission(Permission.ImportRun);

        group.MapGet(TargetsPath, ListTargetsAsync)
            .WithName("ListImportTargets")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Mapping targets for load-file columns: structural targets first, then system and custom fields.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(ProfilesPath, ListProfilesAsync)
            .WithName("ListImportProfiles")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Saved import profiles of the workspace.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(ProfilesPath, CreateProfileAsync)
            .WithName("CreateImportProfile")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Save an import profile. Posting an exported profile copies it into this workspace.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet(ProfilesPath + "/{profileId}", GetProfileAsync)
            .WithName("GetImportProfile")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("An import profile with its ETag; the body is also the profile's JSON export.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut(ProfilesPath + "/{profileId}", ReplaceProfileAsync)
            .WithName("ReplaceImportProfile")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Replace an import profile (If-Match required).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete(ProfilesPath + "/{profileId}", DeleteProfileAsync)
            .WithName("DeleteImportProfile")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Delete an import profile (If-Match required).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Multipart upload of the DAT sample; the cookie-session CSRF check (Origin + X-XSRF-TOKEN) still applies.
        group.MapPost(PreviewPath, PreviewAsync)
            .WithName("PreviewImportMapping")
            .WithTags("Import")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Parse the first rows of a DAT with a profile, auto-map columns and show coerced values with per-column error counts.")
            .DisableAntiforgery()
            .Accepts<MappingPreviewForm>("multipart/form-data")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    internal static async Task<Results<Ok<CursorPage<ImportTargetResource>>, ProblemHttpResult>> ListTargetsAsync(
        string workspaceId, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws))
        {
            return Problems.NotFound("No such workspace.");
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var targets = ImportTargetCatalog.List(catalog);
        return TypedResults.Ok(new CursorPage<ImportTargetResource>(targets, null, new TotalCount(targets.Count, TotalRelation.Eq)));
    }

    internal static async Task<Results<Ok<CursorPage<ImportProfileSummary>>, ProblemHttpResult>> ListProfilesAsync(
        string workspaceId, IImportProfileRepository profiles, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws))
        {
            return Problems.NotFound("No such workspace.");
        }

        // Profiles per workspace are few (one per vendor or volume layout); the page holds them all.
        var items = (await profiles.ListAsync(ws, cancellationToken).ConfigureAwait(false)).Select(r =>
        {
            var definition = ImportProfileRules.Deserialize(r.DefinitionJson);
            return new ImportProfileSummary(r.ProfileId, r.Name, r.Description, definition.Mode, definition.Columns.Count, r.Version, r.UpdatedAt);
        }).ToList();
        return TypedResults.Ok(new CursorPage<ImportProfileSummary>(items, null, new TotalCount(items.Count, TotalRelation.Eq)));
    }

    internal static async Task<Results<Ok<ImportProfileResource>, ProblemHttpResult>> GetProfileAsync(
        string workspaceId, string profileId, HttpResponse response, IImportProfileRepository profiles, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws) || !Guid.TryParse(profileId, out var id)
            || await profiles.GetAsync(ws, id, cancellationToken).ConfigureAwait(false) is not { } record)
        {
            return Problems.NotFound("No such import profile.");
        }

        response.Headers.ETag = EntityTags.ForVersion(record.Version);
        return TypedResults.Ok(ToResource(record));
    }

    internal static async Task<Results<Created<ImportProfileResource>, ValidationProblem, ProblemHttpResult>> CreateProfileAsync(
        string workspaceId, ImportProfileWrite body, HttpContext context, IImportProfileRepository profiles, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws))
        {
            return Problems.NotFound("No such workspace.");
        }

        if (Validate(body) is { } invalid)
        {
            return invalid;
        }

        var result = await profiles.CreateAsync(ws, body.Name, body.Description, ImportProfileRules.Serialize(body.Definition!), UserId(context.User), cancellationToken)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ImportProfileWriteOutcome.NameConflict:
                return NameConflict(body.Name);
            case ImportProfileWriteOutcome.NotFound:
                return Problems.NotFound("No such workspace.");
        }

        var record = result.Profile!;
        context.Response.Headers.ETag = EntityTags.ForVersion(record.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{Uri.EscapeDataString(workspaceId)}{ProfilesPath}/{record.ProfileId}", ToResource(record));
    }

    internal static async Task<Results<Ok<ImportProfileResource>, ValidationProblem, ProblemHttpResult>> ReplaceProfileAsync(
        string workspaceId, string profileId, ImportProfileWrite body, HttpContext context, IImportProfileRepository profiles, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws) || !Guid.TryParse(profileId, out var id)
            || await profiles.GetAsync(ws, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound("No such import profile.");
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        if (Validate(body) is { } invalid)
        {
            return invalid;
        }

        var result = await profiles.UpdateAsync(
            ws, id, current.Version, body.Name, body.Description, ImportProfileRules.Serialize(body.Definition!), UserId(context.User), cancellationToken)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ImportProfileWriteOutcome.NotFound:
                return Problems.NotFound("No such import profile.");
            case ImportProfileWriteOutcome.VersionConflict:
                return Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The resource was modified since it was read.");
            case ImportProfileWriteOutcome.NameConflict:
                return NameConflict(body.Name);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(result.Profile!.Version);
        return TypedResults.Ok(ToResource(result.Profile));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteProfileAsync(
        string workspaceId, string profileId, HttpRequest request, IImportProfileRepository profiles, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws) || !Guid.TryParse(profileId, out var id)
            || await profiles.GetAsync(ws, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound("No such import profile.");
        }

        if (EntityTags.CheckIfMatch(request, current.Version) is { } precondition)
        {
            return precondition;
        }

        return await profiles.DeleteAsync(ws, id, current.Version, cancellationToken).ConfigureAwait(false) switch
        {
            ImportProfileWriteOutcome.Ok => TypedResults.NoContent(),
            ImportProfileWriteOutcome.VersionConflict =>
                Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The resource was modified since it was read."),
            _ => Problems.NotFound("No such import profile."),
        };
    }

    internal static async Task<Results<Ok<MappingPreviewResult>, ValidationProblem, ProblemHttpResult>> PreviewAsync(
        string workspaceId,
        [FromForm] MappingPreviewForm form,
        IFieldCatalogRepository fields,
        IImportProfileRepository profiles,
        IOptions<ImportApiOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws))
        {
            return Problems.NotFound("No such workspace.");
        }

        if (form.File is not { } file)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["file"] = ["The DAT (or its leading bytes) is required."] });
        }

        if (file.Length > options.Value.MaxPreviewSampleBytes)
        {
            return Problems.Create(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge,
                $"Send at most {options.Value.MaxPreviewSampleBytes} bytes (the leading part of the DAT, with sampleIsPartial = true).");
        }

        MappingPreviewRequest request;
        try
        {
            request = string.IsNullOrWhiteSpace(form.Request)
                ? new MappingPreviewRequest()
                : JsonSerializer.Deserialize<MappingPreviewRequest>(form.Request, json.Value.SerializerOptions) ?? new MappingPreviewRequest();
        }
        catch (JsonException ex)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["request"] = [$"Not a valid preview request: {ex.Message}"] });
        }

        if (request.Rows is < 1 or > MappingPreviewOptions.MaxRows)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["rows"] = [$"rows must be between 1 and {MappingPreviewOptions.MaxRows}."] });
        }

        var profile = request.Profile;
        if (request.ProfileId is { } profileId)
        {
            if (profile is not null)
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["profile"] = ["Give either profileId or profile, not both."] });
            }

            if (await profiles.GetAsync(ws, profileId, cancellationToken).ConfigureAwait(false) is not { } saved)
            {
                return Problems.NotFound("No such import profile.");
            }

            profile = ImportProfileRules.Deserialize(saved.DefinitionJson);
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            var result = await MappingPreviewer.PreviewAsync(stream, profile, catalog, new MappingPreviewOptions
            {
                AutoMap = request.AutoMap,
                Rows = request.Rows,
                SampleIsPartial = request.SampleIsPartial,
            }, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(result);
        }
    }

    private static ValidationProblem? Validate(ImportProfileWrite? body)
    {
        if (body is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["A profile is required."] });
        }

        var errors = ImportProfileRules.Validate(body);
        return errors.Count == 0
            ? null
            : Problems.Validation(errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => $"{e.Code}: {e.Message}").ToArray()));
    }

    private static ProblemHttpResult NameConflict(string name) =>
        Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, $"An import profile named '{name.Trim()}' already exists.");

    private static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(OpportunityClaimTypes.UserId)?.Value, out var id) ? id : null;

    private static ImportProfileResource ToResource(ImportProfileRecord record) => new(
        record.ProfileId,
        record.WorkspaceId,
        record.Name,
        record.Description,
        ImportProfileRules.Deserialize(record.DefinitionJson),
        record.Version,
        record.CreatedBy,
        record.CreatedAt,
        record.UpdatedBy,
        record.UpdatedAt);
}

/// <summary>Form of <c>POST …/import-mapping-previews</c>: <c>file</c> (DAT bytes) and <c>request</c> (JSON <see cref="MappingPreviewRequest"/>).</summary>
public sealed class MappingPreviewForm
{
    public IFormFile File { get; set; } = null!;

    /// <summary>JSON of a <see cref="MappingPreviewRequest"/>; empty auto-maps with default settings.</summary>
    public string? Request { get; set; }
}

public static class ImportMappingRegistration
{
    public static IServiceCollection AddImportMappingEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddValidatedOptions<ImportApiOptions>(ImportApiOptions.SectionName);
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IImportProfileRepository, ImportProfileRepository>();
        services.AddSingleton<IApiEndpointModule, ImportMappingEndpoints>();
        return services;
    }
}
