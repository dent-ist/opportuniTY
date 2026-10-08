using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Opportunity.Api.Conventions;
using Opportunity.Application.Audit;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Fields;

/// <summary>
/// Field and coding layout administration (E04-T06; Admin › Fields, Choices and Coding Layouts) under
/// <c>/api/v1/workspaces/{workspaceId}</c>. Every route needs <c>Workspace.ManageFields</c>; changes carry
/// <c>If-Match</c> with the resource's version (checked under the row lock in the transaction of the change) and are
/// audited in that transaction (<c>Workspace</c> / <c>Field.*</c>, <c>CodingLayout.*</c>). No change rewrites document
/// rows (ADR-003): a rename is O(1), a retired field's values are hidden and purged later, a type change is refused
/// once values exist. New fields take a free typed search slot (projection generation 2 maps every slot up front, so
/// no reindex is needed) or, past the per-type budget, the overflow container with reduced capabilities (ADR-007 R6).
/// </summary>
public sealed class FieldAdminEndpoints : IApiEndpointModule
{
    public const string FieldPath = FieldEndpoints.Path + "/{fieldId}";
    public const string ChoicesPath = FieldPath + "/choices";
    public const string ChoicePath = ChoicesPath + "/{choiceId}";
    public const string ChoiceOrderPath = FieldPath + "/choice-order";
    public const string CapacityPath = "/field-capacity";
    public const string LayoutPath = FieldEndpoints.LayoutsPath + "/{layoutId}";

    public const int MaxDescriptionLength = 2_000;
    public const int MaxSections = 50;
    public const int MaxLayoutFields = 500;

    private const string Tag = "Fields";
    private const string NoField = "No such field.";
    private const string NoLayout = "No such coding layout.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(CapacityPath, GetCapacityAsync)
            .WithName("GetFieldCapacity")
            .WithTags(Tag)
            .WithSummary("What a new field of each type and storage gets from the search mapping: free slots and capabilities, with limitations in words.")
            .Produces<FieldCapacityResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPost(FieldEndpoints.Path, CreateFieldAsync)
            .WithName("CreateField")
            .WithTags(Tag)
            .WithSummary("Create a custom coding or metadata field (Workspace.ManageFields; audited).")
            .Produces<FieldDefinitionResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapGet(FieldPath, GetFieldAsync)
            .WithName("GetField")
            .WithTags(Tag)
            .WithSummary("A field with its type attributes, choices, mapping limitations, whether documents hold values, and its ETag.")
            .Produces<FieldDefinitionResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(FieldPath, UpdateFieldAsync)
            .WithName("UpdateField")
            .WithTags(Tag)
            .WithSummary("Rename, describe, hide or retype a field (If-Match required; audited). Retyping is refused once documents hold values (409).")
            .Produces<FieldDefinitionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapDelete(FieldPath, RetireFieldAsync)
            .WithName("RetireField")
            .WithTags(Tag)
            .WithSummary("Retire a custom field (If-Match required; audited): it leaves every layout and its values are hidden. System fields cannot be retired (409).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPost(ChoicesPath, AddChoiceAsync)
            .WithName("AddFieldChoice")
            .WithTags(Tag)
            .WithSummary("Add a choice at the end of a choice field (If-Match of the field; audited). Returns the field.")
            .Produces<FieldDefinitionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(ChoicePath, UpdateChoiceAsync)
            .WithName("UpdateFieldChoice")
            .WithTags(Tag)
            .WithSummary("Rename, deactivate or reactivate a choice (If-Match of the field; audited). Existing values keep a deactivated choice. Returns the field.")
            .Produces<FieldDefinitionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapDelete(ChoicePath, DeleteChoiceAsync)
            .WithName("DeleteFieldChoice")
            .WithTags(Tag)
            .WithSummary("Delete a choice that was never used (If-Match of the field; audited); a used choice can only be deactivated (409). Returns the field.")
            .Produces<FieldDefinitionResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(ChoiceOrderPath, ReorderChoicesAsync)
            .WithName("ReorderFieldChoices")
            .WithTags(Tag)
            .WithSummary("Set the display order of a field's choices; list every choice once (If-Match of the field; audited). Returns the field.")
            .Produces<FieldDefinitionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPost(FieldEndpoints.LayoutsPath, CreateLayoutAsync)
            .WithName("CreateCodingLayout")
            .WithTags(Tag)
            .WithSummary("Create a coding layout: sections, field order, required, read-only, conditional and apply-to-family fields, roles (audited).")
            .Produces<CodingLayoutDefinitionResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapGet(LayoutPath, GetLayoutAsync)
            .WithName("GetCodingLayout")
            .WithTags(Tag)
            .WithSummary("A coding layout as the editor sees it: every field, its roles and its ETag.")
            .Produces<CodingLayoutDefinitionResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(LayoutPath, UpdateLayoutAsync)
            .WithName("UpdateCodingLayout")
            .WithTags(Tag)
            .WithSummary("Replace a coding layout (If-Match required; audited). Making it the default unsets the previous default.")
            .Produces<CodingLayoutDefinitionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapDelete(LayoutPath, DeleteLayoutAsync)
            .WithName("DeleteCodingLayout")
            .WithTags(Tag)
            .WithSummary("Delete a coding layout (If-Match required; audited). The default layout cannot be deleted (409).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Fields
    // ---------------------------------------------------------------------------------------------------------------

    internal static async Task<IResult> GetCapacityAsync(
        string workspaceId, HttpContext context, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        // Retired fields keep their slot until purged (ADR-007 R4), so they count as used.
        var catalog = await fields.GetCatalogAsync(access.WorkspaceId, includeDeleted: true, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new FieldCapacityResource(Capacity(catalog)));
    }

    internal static IReadOnlyList<FieldCapacityEntry> Capacity(FieldCatalog catalog)
    {
        var entries = new List<FieldCapacityEntry>();
        foreach (var storage in new[] { FieldStorage.Coding, FieldStorage.Metadata })
        {
            var budgets = FieldRules.SlotBudgetsFor(storage);
            foreach (var type in Enum.GetValues<FieldType>())
            {
                foreach (var analysis in type == FieldType.Text ? new TextAnalysis?[] { TextAnalysis.Prose, TextAnalysis.Identifier } : [null])
                {
                    var kind = FieldRules.SlotKind(type, analysis);
                    var used = catalog.Fields.Count(f => f.Storage == storage && f.SearchSlot is { } slot
                        && slot.StartsWith(kind + ".s", StringComparison.Ordinal));
                    var budget = budgets[kind];
                    var available = Math.Max(0, budget - used);
                    var capabilities = FieldRules.CapabilitiesForSlot(available > 0 ? FieldRules.Slot(kind, 1) : FieldRules.OverflowSlot);
                    entries.Add(new FieldCapacityEntry(
                        ToResource(storage),
                        Enum.Parse<FieldResourceType>(type.ToString()),
                        analysis is { } a ? Enum.Parse<FieldTextAnalysis>(a.ToString()) : null,
                        budget,
                        available,
                        FieldEndpoints.ToResource(capabilities),
                        Limitations(type, isSearchable: true, capabilities, overflow: available == 0)));
                }
            }
        }

        return entries;
    }

    /// <summary>The mapping limitations of a field in words (ADR-007 R8): what reviewers will not be able to do.</summary>
    internal static IReadOnlyList<string> Limitations(FieldType type, bool isSearchable, FieldCapabilities capabilities, bool overflow)
    {
        if (!isSearchable)
        {
            return ["This field will not be searchable: it cannot be used in searches, conditions, filters or sorting."];
        }

        var limits = new List<string>();
        if (overflow)
        {
            limits.Add("This workspace has used all search slots for this type, so the field gets reduced search: exact and prefix matches only.");
        }

        if (!capabilities.HasFlag(FieldCapabilities.Sortable))
        {
            limits.Add("This field will not be sortable.");
        }

        if (type is FieldType.Integer or FieldType.Decimal or FieldType.Date && !capabilities.HasFlag(FieldCapabilities.Rangeable))
        {
            limits.Add("Range conditions (between, before, after) will not be available.");
        }

        if (!capabilities.HasFlag(FieldCapabilities.Aggregatable))
        {
            limits.Add("Value counts (facets) will not be available for this field.");
        }

        return limits;
    }

    internal static async Task<IResult> CreateFieldAsync(
        string workspaceId, CreateFieldRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the field."] });
        }

        var errors = new Dictionary<string, string[]>();
        if (request.Storage == FieldResourceStorage.Column)
        {
            errors["storage"] = ["New fields are coding fields (set by reviewers) or metadata fields (filled by imports)."];
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"A description has at most {MaxDescriptionLength} characters."];
        }

        if (!Enum.IsDefined(request.Type) || !Enum.IsDefined(request.Storage))
        {
            errors["type"] = ["Unknown field type or storage."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var type = Enum.Parse<FieldType>(request.Type.ToString());
        var storage = Enum.Parse<FieldStorage>(request.Storage.ToString());
        var securityClass = request.SecurityClass is { } c ? Enum.Parse<SecurityClass>(c.ToString()) : (SecurityClass?)null;
        var field = new NewField(
            access.WorkspaceId,
            request.DisplayName ?? string.Empty,
            type,
            storage,
            IsMultiValue: type == FieldType.MultiChoice || (type is FieldType.Text or FieldType.Keyword && request.MultiValue),
            DatePrecision: type == FieldType.Date
                ? request.DatePrecision == FieldResourceDatePrecision.Date ? DatePrecision.Date : DatePrecision.DateTime
                : null,
            DecimalPrecision: type == FieldType.Decimal ? (short)(request.DecimalPrecision ?? FieldLimits.MaxDecimalPrecision) : null,
            DecimalScale: type == FieldType.Decimal ? (short)(request.DecimalScale ?? 2) : null,
            TextAnalysis: type == FieldType.Text
                ? request.TextAnalysis == FieldTextAnalysis.Identifier ? TextAnalysis.Identifier : TextAnalysis.Prose
                : null,
            SecurityClass: securityClass,
            IsSearchable: request.IsSearchable,
            Description: string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim());
        var audit = FieldAdminAudit.Field(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.FieldCreated, time.GetUtcNow(),
            FieldDetails(type, storage, securityClass, "created"));
        var result = await fields.CreateFieldAsync(field, new CatalogWrite(null, audit), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(result, NoField);
        }

        var resource = await FieldResourceAsync(fields, access.WorkspaceId, result.Value!.FieldId, hasValues: false, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(resource!.Version);
        return TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{FieldEndpoints.Path}/{resource.FieldId}", resource);
    }

    internal static async Task<IResult> GetFieldAsync(
        string workspaceId, string fieldId, HttpContext context, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound(NoField);
        }

        var hasValues = await fields.FieldHasValuesAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        var resource = await FieldResourceAsync(fields, access.WorkspaceId, id, hasValues, cancellationToken).ConfigureAwait(false);
        return resource is null ? Problems.NotFound(NoField) : Ok(context, resource);
    }

    internal static async Task<IResult> UpdateFieldAsync(
        string workspaceId, string fieldId, UpdateFieldRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        if (request is null || !Enum.IsDefined(request.Type))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["type"] = ["Send the field with a known type."] });
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["description"] = [$"A description has at most {MaxDescriptionLength} characters."] });
        }

        var catalog = await fields.GetCatalogAsync(access.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (catalog.Find(id) is not { IsDeleted: false } current)
        {
            return Problems.NotFound(NoField);
        }

        var type = Enum.Parse<FieldType>(request.Type.ToString());
        var change = new FieldChange(access.WorkspaceId, id)
        {
            Name = request.DisplayName ?? string.Empty,
            Description = request.Description ?? string.Empty,
            IsHidden = request.IsHidden,
            Type = type,
            IsMultiValue = type == FieldType.MultiChoice || (type is FieldType.Text or FieldType.Keyword && request.MultiValue),
            DatePrecision = request.DatePrecision is { } p ? Enum.Parse<DatePrecision>(p.ToString()) : null,
            DecimalPrecision = request.DecimalPrecision is { } dp ? (short)Math.Clamp(dp, short.MinValue, short.MaxValue) : null,
            DecimalScale = request.DecimalScale is { } ds ? (short)Math.Clamp(ds, short.MinValue, short.MaxValue) : null,
        };
        var retype = type != current.Type || change.IsMultiValue != current.IsMultiValue
            || (change.DatePrecision is { } d && d != current.DatePrecision)
            || (change.DecimalPrecision is { } prec && prec != current.DecimalPrecision)
            || (change.DecimalScale is { } scale && scale != current.DecimalScale);
        var audit = FieldAdminAudit.Field(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.FieldModified, time.GetUtcNow(),
            FieldDetails(type, current.Storage, current.SecurityClass, retype ? "retyped" : "attributes"));
        var result = await fields.UpdateFieldAsync(change, new CatalogWrite(expected, audit), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(result, NoField);
        }

        var hasValues = await fields.FieldHasValuesAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        var resource = await FieldResourceAsync(fields, access.WorkspaceId, id, hasValues, cancellationToken).ConfigureAwait(false);
        return resource is null ? Problems.NotFound(NoField) : Ok(context, resource);
    }

    internal static async Task<IResult> RetireFieldAsync(
        string workspaceId, string fieldId, HttpContext context, IFieldCatalogRepository fields, TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        var catalog = await fields.GetCatalogAsync(access.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (catalog.Find(id) is not { } current)
        {
            return Problems.NotFound(NoField);
        }

        var audit = FieldAdminAudit.Field(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.FieldRetired, time.GetUtcNow(),
            FieldDetails(current.Type, current.Storage, current.SecurityClass, "retired"));
        var result = await fields.DeleteFieldAsync(access.WorkspaceId, id, new CatalogWrite(expected, audit), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? TypedResults.NoContent() : Problem(result, NoField);
    }

    internal static async Task<IResult> AddChoiceAsync(
        string workspaceId, string fieldId, CreateChoiceRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        var current = await LiveFieldAsync(fields, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Problems.NotFound(NoField);
        }

        var audit = ChoiceAudit(access, current, time, "choiceAdded", null);
        var result = await fields.AddChoiceAsync(access.WorkspaceId, id, request?.Name ?? string.Empty, new CatalogWrite(expected, audit), cancellationToken)
            .ConfigureAwait(false);
        return await ChoiceResultAsync(context, fields, access.WorkspaceId, id, result.Outcome, result.Errors, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IResult> UpdateChoiceAsync(
        string workspaceId, string fieldId, string choiceId, UpdateChoiceRequest request, HttpContext context, IFieldCatalogRepository fields,
        TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id) || !TryChoiceId(choiceId, out var choice))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        if (request is null || (request.Name is null && request.IsActive is null))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send a new name, isActive, or both."] });
        }

        var current = await LiveFieldAsync(fields, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Problems.NotFound(NoField);
        }

        var kind = request.IsActive switch
        {
            false => "choiceDeactivated",
            true when request.Name is null => "choiceActivated",
            _ => "choiceRenamed",
        };
        var audit = ChoiceAudit(access, current, time, kind, choice);
        var result = await fields.UpdateChoiceAsync(access.WorkspaceId, id, choice, request.Name, request.IsActive, new CatalogWrite(expected, audit),
            cancellationToken).ConfigureAwait(false);
        return await ChoiceResultAsync(context, fields, access.WorkspaceId, id, result.Outcome, result.Errors, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IResult> DeleteChoiceAsync(
        string workspaceId, string fieldId, string choiceId, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id) || !TryChoiceId(choiceId, out var choice))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        var current = await LiveFieldAsync(fields, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Problems.NotFound(NoField);
        }

        var audit = ChoiceAudit(access, current, time, "choiceDeleted", choice);
        var result = await fields.DeleteChoiceAsync(access.WorkspaceId, id, choice, new CatalogWrite(expected, audit), cancellationToken).ConfigureAwait(false);
        return await ChoiceResultAsync(context, fields, access.WorkspaceId, id, result.Outcome, result.Errors, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IResult> ReorderChoicesAsync(
        string workspaceId, string fieldId, ChoiceOrderRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound(NoField);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        if (request?.ChoiceIds is not { } order || order.Count > FieldLimits.MaxChoicesPerField)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["choiceIds"] = ["List every choice of the field once."] });
        }

        var current = await LiveFieldAsync(fields, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Problems.NotFound(NoField);
        }

        var audit = ChoiceAudit(access, current, time, "choicesReordered", null);
        var result = await fields.ReorderChoicesAsync(access.WorkspaceId, id, order, new CatalogWrite(expected, audit), cancellationToken).ConfigureAwait(false);
        return await ChoiceResultAsync(context, fields, access.WorkspaceId, id, result.Outcome, result.Errors, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Coding layouts
    // ---------------------------------------------------------------------------------------------------------------

    internal static async Task<IResult> GetLayoutAsync(
        string workspaceId, string layoutId, HttpContext context, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(layoutId, out var id))
        {
            return Problems.NotFound(NoLayout);
        }

        var layout = await fields.GetLayoutAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return layout is null ? Problems.NotFound(NoLayout) : Ok(context, layout);
    }

    internal static async Task<IResult> CreateLayoutAsync(
        string workspaceId, CodingLayoutRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound(NoLayout);
        }

        if (ToLayout(access.WorkspaceId, Guid.Empty, request, out var errors) is not { } layout)
        {
            return Problems.Validation(errors);
        }

        var audit = FieldAdminAudit.Layout(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.LayoutCreated, time.GetUtcNow(),
            LayoutDetails(layout));
        var result = await fields.SaveLayoutAsync(layout, new CatalogWrite(null, audit), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(result, NoLayout);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(result.Value!.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{FieldEndpoints.LayoutsPath}/{result.Value.LayoutId}", ToResource(result.Value));
    }

    internal static async Task<IResult> UpdateLayoutAsync(
        string workspaceId, string layoutId, CodingLayoutRequest request, HttpContext context, IFieldCatalogRepository fields, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(layoutId, out var id))
        {
            return Problems.NotFound(NoLayout);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        if (ToLayout(access.WorkspaceId, id, request, out var errors) is not { } layout)
        {
            return Problems.Validation(errors);
        }

        var audit = FieldAdminAudit.Layout(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.LayoutModified, time.GetUtcNow(),
            LayoutDetails(layout));
        // A replace never creates: without If-Match "*" the store requires the layout to exist.
        var result = await fields.SaveLayoutAsync(layout, new CatalogWrite(expected ?? -1, audit), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == CatalogOutcome.VersionConflict && expected is null)
        {
            // If-Match: * matches any current version: retry the replace with the version just seen.
            var existing = await fields.GetLayoutAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Problems.NotFound(NoLayout);
            }

            result = await fields.SaveLayoutAsync(layout, new CatalogWrite(existing.Version, audit), cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Ok(context, result.Value!) : Problem(result, NoLayout);
    }

    internal static async Task<IResult> DeleteLayoutAsync(
        string workspaceId, string layoutId, HttpContext context, IFieldCatalogRepository fields, TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(layoutId, out var id))
        {
            return Problems.NotFound(NoLayout);
        }

        if (IfMatch(context.Request, out var expected) is { } precondition)
        {
            return precondition;
        }

        var existing = await fields.GetLayoutAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return Problems.NotFound(NoLayout);
        }

        var audit = FieldAdminAudit.Layout(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.LayoutDeleted, time.GetUtcNow(),
            LayoutDetails(existing));
        var result = await fields.DeleteLayoutAsync(access.WorkspaceId, id, new CatalogWrite(expected, audit), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? TypedResults.NoContent() : Problem(result, NoLayout);
    }

    /// <summary>The request as a layout, or null with the problems keyed like the request.</summary>
    internal static CodingLayout? ToLayout(Guid workspaceId, Guid layoutId, CodingLayoutRequest? request, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (request is null)
        {
            errors["body"] = ["Send the layout."];
            return null;
        }

        var sections = request.Sections ?? [];
        if (sections.Count > MaxSections)
        {
            errors["sections"] = [$"A layout has at most {MaxSections} sections."];
        }

        if (sections.Sum(s => s?.Fields?.Count ?? 0) > MaxLayoutFields)
        {
            errors["sections"] = [$"A layout has at most {MaxLayoutFields} fields."];
        }

        var roles = (request.Roles ?? []).Select(r => r?.Trim() ?? string.Empty).ToList();
        var assignable = RoleCatalog.All.Where(r => r.Role != WorkspaceRole.BreakGlass).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        if (roles.Any(r => !assignable.Contains(r)) || roles.Distinct(StringComparer.Ordinal).Count() != roles.Count)
        {
            errors["roles"] = [$"Roles are distinct keys of: {string.Join(", ", assignable.Order(StringComparer.Ordinal))}."];
        }

        if (sections.Any(s => s is null))
        {
            errors["sections"] = ["Sections cannot be null."];
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var layout = new CodingLayout
        {
            WorkspaceId = workspaceId,
            LayoutId = layoutId,
            Name = request.Name ?? string.Empty,
            IsDefault = request.IsDefault,
            Roles = roles,
        };
        foreach (var section in sections)
        {
            layout.Sections.Add(new CodingLayoutSection
            {
                SectionId = section.SectionId is { } given && given != Guid.Empty ? given : Guid.CreateVersion7(),
                Title = section.Title ?? string.Empty,
                Fields =
                [
                    .. (section.Fields ?? []).Where(f => f is not null).Select(f => new CodingLayoutField
                    {
                        FieldId = f.FieldId,
                        IsRequired = f.IsRequired,
                        IsReadOnly = f.IsReadOnly,
                        ApplyToFamilyByDefault = f.ApplyToFamilyByDefault,
                        VisibleWhen = f.VisibleWhen is { } c
                            ? new VisibilityCondition(c.FieldId, c.ChoiceIds is { Count: > 0 } ids ? [.. ids] : null, c.BooleanValue)
                            : null,
                    }),
                ],
            });
        }

        return layout;
    }

    internal static CodingLayoutDefinitionResource ToResource(CodingLayout layout) => new(
        layout.LayoutId,
        layout.Name,
        layout.IsDefault,
        [.. layout.Roles],
        FieldEndpoints.ToResource(layout, new HashSet<int>()).Sections,
        layout.Version);

    // ---------------------------------------------------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The version of a required <c>If-Match</c>: 428 when it is missing, 412 when it is not one of our ETags. <c>*</c>
    /// gives a null version (any).
    /// </summary>
    internal static IResult? IfMatch(HttpRequest request, out long? version)
    {
        version = null;
        var header = request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(header))
        {
            return Problems.Create(StatusCodes.Status428PreconditionRequired, ProblemCodes.PreconditionRequired,
                "This resource is versioned; send If-Match with its current ETag.");
        }

        if (!EntityTagHeaderValue.TryParseList(header, out var tags) || tags.Count != 1)
        {
            return VersionConflict();
        }

        var tag = tags[0];
        if (tag.Equals(EntityTagHeaderValue.Any))
        {
            return null;
        }

        if (tag.IsWeak || !long.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return VersionConflict();
        }

        version = parsed;
        return null;
    }

    private static ProblemHttpResult VersionConflict() =>
        Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The resource was modified since it was read.");

    private static IResult Problem<T>(CatalogResult<T> result, string notFound) => Problem(result.Outcome, result.Errors, notFound);

    private static IResult Problem(CatalogOutcome outcome, IReadOnlyList<FieldError> errors, string notFound) => outcome switch
    {
        CatalogOutcome.Invalid => Problems.Validation(errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray())),
        CatalogOutcome.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
            string.Join(' ', errors.Select(e => e.Message))),
        CatalogOutcome.VersionConflict => VersionConflict(),
        _ => Problems.NotFound(notFound),
    };

    private static async Task<IResult> ChoiceResultAsync(
        HttpContext context, IFieldCatalogRepository fields, Guid workspaceId, int fieldId, CatalogOutcome outcome, IReadOnlyList<FieldError> errors,
        CancellationToken cancellationToken)
    {
        if (outcome != CatalogOutcome.Ok)
        {
            return Problem(outcome, errors, outcome == CatalogOutcome.NotFound ? "No such field or choice." : NoField);
        }

        var resource = await FieldResourceAsync(fields, workspaceId, fieldId, hasValues: null, cancellationToken).ConfigureAwait(false);
        return resource is null ? Problems.NotFound(NoField) : Ok(context, resource);
    }

    private static async Task<FieldDefinition?> LiveFieldAsync(IFieldCatalogRepository fields, Guid workspaceId, int fieldId, CancellationToken cancellationToken)
    {
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return catalog.Find(fieldId);
    }

    private static AuditEvent ChoiceAudit(WorkspaceAccess access, FieldDefinition field, TimeProvider time, string change, int? choiceId)
    {
        var details = FieldDetails(field.Type, field.Storage, field.SecurityClass, change);
        if (choiceId is { } id)
        {
            details["choiceId"] = id.ToString(CultureInfo.InvariantCulture);
        }

        return FieldAdminAudit.Field(access.Principal, access.WorkspaceId, AuditTaxonomy.FieldCatalog.FieldModified, time.GetUtcNow(), details);
    }

    private static Dictionary<string, string?> FieldDetails(FieldType type, FieldStorage storage, SecurityClass? securityClass, string change) => new()
    {
        ["type"] = type.ToString(),
        ["storage"] = storage.ToString(),
        ["securityClass"] = securityClass?.ToString(),
        ["change"] = change,
    };

    private static Dictionary<string, string?> LayoutDetails(CodingLayout layout) => new()
    {
        ["isDefault"] = layout.IsDefault ? "true" : "false",
        ["sectionCount"] = layout.Sections.Count.ToString(CultureInfo.InvariantCulture),
        ["fieldCount"] = layout.AllFields.Count().ToString(CultureInfo.InvariantCulture),
        ["roles"] = string.Join(',', layout.Roles),
    };

    private static async Task<FieldDefinitionResource?> FieldResourceAsync(
        IFieldCatalogRepository fields, Guid workspaceId, int fieldId, bool? hasValues, CancellationToken cancellationToken)
    {
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return catalog.Find(fieldId) is { IsDeleted: false } field ? ToResource(catalog, field, hasValues) : null;
    }

    internal static FieldDefinitionResource ToResource(FieldCatalog catalog, FieldDefinition f, bool? hasValues)
    {
        var queryNames = FieldQueryNames.Assign([.. catalog.Fields.Where(x => !x.IsDeleted).OrderBy(x => x.FieldId)]);
        var overflow = f.SearchSlot == FieldRules.OverflowSlot;
        return new FieldDefinitionResource(
            f.FieldId,
            f.Name,
            queryNames[f.FieldId],
            f.Description,
            Enum.Parse<FieldResourceType>(f.Type.ToString()),
            ToResource(f.Storage),
            f.IsMultiValue,
            f.IsSystem,
            f.IsHidden,
            f.SecurityClass is { } c ? Enum.Parse<FieldSecurityClass>(c.ToString()) : null,
            f.DatePrecision is { } p ? Enum.Parse<FieldResourceDatePrecision>(p.ToString()) : null,
            f.DecimalPrecision,
            f.DecimalScale,
            f.TextAnalysis is { } a ? Enum.Parse<FieldTextAnalysis>(a.ToString()) : null,
            f.IsSearchable,
            FieldEndpoints.ToResource(f.Capabilities),
            overflow,
            Limitations(f.Type, f.IsSearchable, f.Capabilities, overflow),
            hasValues,
            f.IsChoice
                ? [.. catalog.ChoicesOf(f.FieldId).Select(ch => new FieldDefinitionChoiceResource(ch.ChoiceId, ch.Name, ch.IsActive, ch.FirstUsedAt is not null, ch.SystemKey))]
                : null,
            f.Version);
    }

    private static FieldResourceStorage ToResource(FieldStorage storage) => Enum.Parse<FieldResourceStorage>(storage.ToString());

    private static Ok<FieldDefinitionResource> Ok(HttpContext context, FieldDefinitionResource resource)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(resource.Version);
        return TypedResults.Ok(resource);
    }

    private static Ok<CodingLayoutDefinitionResource> Ok(HttpContext context, CodingLayout layout)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(layout.Version);
        return TypedResults.Ok(ToResource(layout));
    }

    private static bool TryFieldId(string value, out int id) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private static bool TryChoiceId(string value, out int id) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
