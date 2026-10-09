using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Application.ReviewBatches;
using Opportunity.Contracts.Api;
using Opportunity.Core.ReviewBatches;
using Opportunity.Core.Security;
using Opportunity.Data.ReviewBatches;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.ReviewBatches;

/// <summary>
/// Review batches (E10-T05; Q-53; familiarity guide §5.3) under <c>/api/v1/workspaces/{workspaceId}</c>. Domain and API
/// only — the batching screens are E10-T06. <c>ReviewBatch.Manage</c> creates Batch Sets from a ReviewBatch snapshot,
/// assigns batches and reads first-pass/QC conflicts; members with <c>Document.View</c> list sets, batches and members
/// (documents and counts they may not see are left out, Q-52); <c>Coding.Write</c> checks a batch out to oneself and
/// back in. Status changes need <c>If-Match</c> (the batch's ETag) and are audited in their transaction.
/// </summary>
public sealed class ReviewBatchEndpoints : IApiEndpointModule
{
    public const string SetsPath = "/review-batch-sets";
    public const string BatchesPath = "/review-batches";

    private const string SetTag = "Review batches";
    private const string NoSuchSet = "No such batch set.";
    private const string NoSuchBatch = "No such batch.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapPost(SetsPath, CreateSetAsync)
            .WithName("CreateReviewBatchSet")
            .WithTags(SetTag)
            .WithSummary("Cut a ReviewBatch snapshot into batches of at most N documents, keeping families (and optionally threads) together.")
            .WithDescription(
                "Freeze the documents first (POST …/snapshots, purpose ReviewBatch, e.g. from a saved search). Membership is frozen " +
                "at creation; batches are named {prefix}_0001…. A QC set names the first-pass set it checks. Audited.")
            .RequireIdempotencyKey()
            .Produces<ReviewBatchSetResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.ReviewBatchManage);

        ws.MapGet(SetsPath, ListSetsAsync)
            .WithName("ListReviewBatchSets")
            .WithTags(SetTag)
            .WithSummary("Batch Sets, newest first, with batch counts by status and the documents you may see.")
            .Produces<CursorPage<ReviewBatchSetResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapGet(SetsPath + "/{batchSetId}", GetSetAsync)
            .WithName("GetReviewBatchSet")
            .WithTags(SetTag)
            .WithSummary("A Batch Set with its settings and batch counts by status.")
            .Produces<ReviewBatchSetResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapGet(SetsPath + "/{batchSetId}/conflicts", ConflictsAsync)
            .WithName("ListReviewBatchConflicts")
            .WithTags(SetTag)
            .WithSummary("Where a QC set's calls differ from its first pass: document, field, both calls and reviewers.")
            .WithDescription(
                "A call is the reviewer's last own coding change of the field while holding the document's batch (CodingEvent " +
                "provenance). Documents and fields you may not see are left out. 409 for a first-pass set.")
            .Produces<CursorPage<ReviewConflictResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.ReviewBatchManage);

        ws.MapGet(BatchesPath, ListBatchesAsync)
            .WithName("ListReviewBatches")
            .WithTags(SetTag)
            .WithSummary("Batches by set and number; filter by batchSetId, status (available, checkedOut, completed) and assigneeId (or me).")
            .Produces<CursorPage<ReviewBatchResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapGet(BatchesPath + "/{batchId}", GetBatchAsync)
            .WithName("GetReviewBatch")
            .WithTags(SetTag)
            .WithSummary("A batch with its status, assignee and ETag.")
            .Produces<ReviewBatchResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapGet(BatchesPath + "/{batchId}/documents", ListDocumentsAsync)
            .WithName("ListReviewBatchDocuments")
            .WithTags(SetTag)
            .WithSummary("The batch's documents in review order (documents you may not see are left out).")
            .Produces<CursorPage<ReviewBatchDocumentResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapPost(BatchesPath + "/{batchId}/check-out", CheckOutAsync)
            .WithName("CheckOutReviewBatch")
            .WithTags(SetTag)
            .WithSummary("Check an available batch out to yourself (If-Match required; audited).")
            .Produces<ReviewBatchResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.CodingWrite);

        ws.MapPost(BatchesPath + "/{batchId}/check-in", CheckInAsync)
            .WithName("CheckInReviewBatch")
            .WithTags(SetTag)
            .WithSummary("Check your batch in, completed or not (managers may check in anyone's; If-Match required; audited).")
            .Produces<ReviewBatchResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.CodingWrite);

        ws.MapPut(BatchesPath + "/{batchId}/assignment", AssignAsync)
            .WithName("AssignReviewBatch")
            .WithTags(SetTag)
            .WithSummary("Assign a batch to a reviewer (checked out to them, also to reopen a completed batch) or make it available (If-Match; audited).")
            .Produces<ReviewBatchResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.ReviewBatchManage);
    }

    internal static async Task<IResult> CreateSetAsync(
        string workspaceId, CreateReviewBatchSetRequest request, HttpContext context, ReviewBatchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the Batch Set to create."] });
        }

        var outcome = await service.CreateSetAsync(access.Principal, access.WorkspaceId, new ReviewBatchSetRequest(
            request.Name,
            request.SnapshotId,
            request.BatchPrefix,
            request.MaxBatchSize,
            request.KeepFamiliesTogether,
            request.KeepThreadsTogether,
            request.ReviewPass == ReviewPassResource.Qc ? ReviewPass.Qc : ReviewPass.FirstPass,
            request.QcOfBatchSetId,
            request.ReviewerGroup), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ReviewBatchOutcomeStatus.Created)
        {
            return Problem(outcome, NoSuchSet);
        }

        var set = outcome.Set!;
        return TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{SetsPath}/{set.Set.BatchSetId}", ToResource(set));
    }

    internal static async Task<IResult> ListSetsAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, ReviewBatchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        Guid? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 1) is not [var text] || !Guid.TryParse(text, out var id))
            {
                return PageCursor.Invalid();
            }

            after = id;
        }

        var limit = page.EffectiveLimit;
        var sets = await service.ListSetsAsync(access.Principal, access.WorkspaceId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = sets.Take(limit).Select(ToResource).ToList();
        var next = sets.Count > limit ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, items[^1].BatchSetId.ToString("N")) : null;
        return TypedResults.Ok(new CursorPage<ReviewBatchSetResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetSetAsync(
        string workspaceId, string batchSetId, HttpContext context, ReviewBatchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(batchSetId, out var id)
            || await service.GetSetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } set)
        {
            return Problems.NotFound(NoSuchSet);
        }

        return TypedResults.Ok(ToResource(set));
    }

    internal static async Task<IResult> ConflictsAsync(
        string workspaceId, string batchSetId, [AsParameters] PageQuery page, HttpContext context, ReviewBatchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(batchSetId, out var id)
            || await service.GetSetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } view)
        {
            return Problems.NotFound(NoSuchSet);
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (view.Set.Pass != ReviewPass.Qc)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                "Conflicts are read on a QC Batch Set, which compares its calls with its first pass.");
        }

        ReviewConflictCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var setText, var docText, var fieldText]
                || setText != id.ToString("N")
                || !Guid.TryParse(docText, out var documentId)
                || !int.TryParse(fieldText, NumberStyles.None, CultureInfo.InvariantCulture, out var fieldId))
            {
                return PageCursor.Invalid();
            }

            after = new ReviewConflictCursor(documentId, fieldId);
        }

        var (conflicts, next) = await service.GetConflictsAsync(access.Principal, access.WorkspaceId, view.Set, after,
            Math.Min(page.EffectiveLimit, ReviewBatchService.MaxLimit), cancellationToken).ConfigureAwait(false);
        var items = conflicts.Select(c => new ReviewConflictResource(
            c.Conflict.DocumentId,
            c.Conflict.ControlNumber,
            c.Conflict.FieldId,
            c.FieldName,
            Call(c.Conflict.FirstPass, c.FirstPassReviewer),
            Call(c.Conflict.Qc, c.QcReviewer))).ToList();
        var nextCursor = next is { } n
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, id.ToString("N"), n.DocumentId.ToString("N"),
                n.FieldId.ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new CursorPage<ReviewConflictResource>(items, nextCursor,
            new TotalCount(items.Count, nextCursor is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> ListBatchesAsync(
        string workspaceId,
        [FromQuery] Guid? batchSetId,
        [FromQuery] string? status,
        [FromQuery] string? assigneeId,
        [AsParameters] PageQuery page,
        HttpContext context,
        ReviewBatchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        ReviewBatchStatus? statusFilter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<ReviewBatchStatusResource>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)
                || int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["status"] = ["status is available, checkedOut or completed."] });
            }

            statusFilter = Status(parsed);
        }

        Guid? assignee = null;
        if (assigneeId is not null)
        {
            if (string.Equals(assigneeId, "me", StringComparison.OrdinalIgnoreCase))
            {
                assignee = access.Principal.UserId;
            }
            else if (Guid.TryParse(assigneeId, out var user))
            {
                assignee = user;
            }
            else
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["assigneeId"] = ["assigneeId is a user ID or me."] });
            }
        }

        ReviewBatchCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var setText, var ordinalText]
                || !Guid.TryParse(setText, out var setId)
                || !int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                return PageCursor.Invalid();
            }

            after = new ReviewBatchCursor(setId, ordinal);
        }

        var limit = page.EffectiveLimit;
        var batches = await service.ListBatchesAsync(access.Principal, access.WorkspaceId, new ReviewBatchFilter(batchSetId, statusFilter, assignee),
            after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = batches.Take(limit).Select(ToResource).ToList();
        var next = batches.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, items[^1].BatchSetId.ToString("N"),
                items[^1].Ordinal.ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new CursorPage<ReviewBatchResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetBatchAsync(
        string workspaceId, string batchId, HttpContext context, ReviewBatchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, batchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoSuchBatch);
        }

        return Ok(context, current.Batch);
    }

    internal static async Task<IResult> ListDocumentsAsync(
        string workspaceId, string batchId, [AsParameters] PageQuery page, HttpContext context, ReviewBatchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, batchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoSuchBatch);
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        var access = current.Access;
        var id = current.Batch.Batch.BatchId;
        var afterPosition = 0;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var batchText, var positionText]
                || batchText != id.ToString("N")
                || !int.TryParse(positionText, NumberStyles.None, CultureInfo.InvariantCulture, out afterPosition))
            {
                return PageCursor.Invalid();
            }
        }

        var (members, next) = await service.ListMembersAsync(access.Principal, access.WorkspaceId, id, afterPosition, page.EffectiveLimit, cancellationToken)
            .ConfigureAwait(false);
        var items = members.Select(m => new ReviewBatchDocumentResource(m.DocumentId, m.ControlNumber, m.Position)).ToList();
        var nextCursor = next is { } n
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, id.ToString("N"), n.ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new CursorPage<ReviewBatchDocumentResource>(items, nextCursor,
            new TotalCount(items.Count, nextCursor is null && afterPosition == 0 ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> CheckOutAsync(
        string workspaceId, string batchId, HttpContext context, ReviewBatchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, batchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoSuchBatch);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Batch.Batch.Version) is { } precondition)
        {
            return precondition;
        }

        return Result(context, await service.CheckOutAsync(current.Access.Principal, current.Access.WorkspaceId, current.Batch.Batch, cancellationToken)
            .ConfigureAwait(false));
    }

    internal static async Task<IResult> CheckInAsync(
        string workspaceId, string batchId, CheckInReviewBatchRequest request, HttpContext context, ReviewBatchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, batchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoSuchBatch);
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["completed"] = ["Say whether the batch is completed."] });
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Batch.Batch.Version) is { } precondition)
        {
            return precondition;
        }

        return Result(context, await service.CheckInAsync(current.Access.Principal, current.Access.WorkspaceId, current.Batch.Batch, request.Completed,
            cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> AssignAsync(
        string workspaceId, string batchId, ReviewBatchAssignmentRequest request, HttpContext context, ReviewBatchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, batchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoSuchBatch);
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["assigneeId"] = ["Send the assignee (null makes the batch available)."] });
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Batch.Batch.Version) is { } precondition)
        {
            return precondition;
        }

        return Result(context, await service.AssignAsync(current.Access.Principal, current.Access.WorkspaceId, current.Batch.Batch, request.AssigneeId,
            cancellationToken).ConfigureAwait(false));
    }

    internal static ReviewBatchSetResource ToResource(ReviewBatchSetView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var s = view.Set;
        return new ReviewBatchSetResource(
            s.BatchSetId,
            s.Name,
            s.BatchPrefix,
            s.MaxBatchSize,
            s.KeepFamiliesTogether,
            s.KeepThreadsTogether,
            Pass(s.Pass),
            s.QcOfBatchSetId,
            s.ReviewerGroup,
            s.SnapshotId,
            s.BatchCount,
            view.VisibleDocuments,
            new ReviewBatchStatusCounts(s.Available, s.CheckedOut, s.Completed),
            new ReviewBatchUserResource(s.CreatedBy, view.CreatorName),
            s.CreatedAt);
    }

    internal static ReviewBatchResource ToResource(ReviewBatchView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var b = view.Batch;
        return new ReviewBatchResource(
            b.BatchId,
            b.BatchSetId,
            b.BatchSetName,
            b.Ordinal,
            b.Name,
            Pass(b.Pass),
            b.Status switch
            {
                ReviewBatchStatus.CheckedOut => ReviewBatchStatusResource.CheckedOut,
                ReviewBatchStatus.Completed => ReviewBatchStatusResource.Completed,
                _ => ReviewBatchStatusResource.Available,
            },
            (int)view.VisibleDocuments,
            b.AssigneeId is { } a ? new ReviewBatchUserResource(a, view.AssigneeName) : null,
            b.StatusChangedAt,
            b.StatusChangedBy,
            b.Version);
    }

    private static ReviewCallResource Call(ReviewCall call, string? reviewerName) =>
        new(call.Value?.DeepClone(), new ReviewBatchUserResource(call.ReviewerId, reviewerName), call.At, call.BatchId, call.BatchName, call.EventId);

    private static ReviewPassResource Pass(ReviewPass pass) => pass == ReviewPass.Qc ? ReviewPassResource.Qc : ReviewPassResource.FirstPass;

    private static ReviewBatchStatus Status(ReviewBatchStatusResource status) => status switch
    {
        ReviewBatchStatusResource.CheckedOut => ReviewBatchStatus.CheckedOut,
        ReviewBatchStatusResource.Completed => ReviewBatchStatus.Completed,
        _ => ReviewBatchStatus.Available,
    };

    private static async Task<(WorkspaceAccess Access, ReviewBatchView Batch)?> CurrentAsync(
        HttpContext context, string batchId, ReviewBatchService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(batchId, out var id))
        {
            return null;
        }

        return await service.GetBatchAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is { } batch
            ? (access, batch)
            : null;
    }

    private static Ok<ReviewBatchResource> Ok(HttpContext context, ReviewBatchView batch)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(batch.Batch.Version);
        return TypedResults.Ok(ToResource(batch));
    }

    private static IResult Result(HttpContext context, ReviewBatchOutcome outcome)
    {
        if (outcome.Status == ReviewBatchOutcomeStatus.Ok)
        {
            return Ok(context, outcome.Batch!);
        }

        if (outcome.Status == ReviewBatchOutcomeStatus.VersionConflict && outcome.Batch is { } current)
        {
            context.Response.Headers.ETag = EntityTags.ForVersion(current.Batch.Version);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                detail: "The batch changed since you read it.",
                type: ProblemCodes.TypeFor(ProblemCodes.VersionConflict),
                extensions: new Dictionary<string, object?>
                {
                    [Problems.CodeExtension] = ProblemCodes.VersionConflict,
                    ["current"] = ToResource(current),
                });
        }

        return Problem(outcome, NoSuchBatch);
    }

    private static IResult Problem(ReviewBatchOutcome outcome, string notFound) => outcome.Status switch
    {
        ReviewBatchOutcomeStatus.Invalid => Problems.Validation(outcome.Errors.ToDictionary()),
        ReviewBatchOutcomeStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            outcome.Detail ?? "You do not have permission for this operation."),
        ReviewBatchOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, outcome.Detail),
        ReviewBatchOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The batch changed since you read it."),
        _ => Problems.NotFound(outcome.Detail ?? notFound),
    };

    private static bool TryParseId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;
}

public static class ReviewBatchEndpointRegistration
{
    /// <summary>The review batch endpoints, use case and PostgreSQL store.</summary>
    public static IServiceCollection AddReviewBatchEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresReviewBatchStore();
        services.TryAddScoped<ReviewBatchService>();
        services.AddSingleton<IApiEndpointModule, ReviewBatchEndpoints>();
        return services;
    }
}
