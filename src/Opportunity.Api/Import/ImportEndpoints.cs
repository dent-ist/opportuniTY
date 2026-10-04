using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Api.Workspaces;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Fields;
using Opportunity.Data.Import;
using Opportunity.Data.Jobs;
using Opportunity.Data.Workspaces;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Import;

/// <summary>
/// Start and monitor load-file imports (E08-T03): <c>POST …/imports</c> uploads the DAT (and optionally its OPT image
/// cross-reference, E08-T05; an OPT alone re-loads the pages of existing documents) with a saved or ad-hoc import
/// profile and answers <c>202 Accepted</c> with the import and its job; the import worker prepares and runs it in
/// chunks. Progress is the job's (committed / indexed) plus the import report counters; row-level errors download as
/// CSV. Append needs <c>Import.Run</c>; overlay modes and Q-31 coding-field overlay need <c>Import.Overlay</c>; creating
/// fields or choices at load time needs <c>Workspace.ManageFields</c>.
/// </summary>
public sealed class ImportEndpoints : IApiEndpointModule
{
    public const string ImportsPath = "/imports";

    private const int MaxNameLength = 200;
    private const int MaxRequestPartBytes = 1024 * 1024;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(ImportsPath).RequirePermission(Permission.ImportRun);

        // Multipart upload of the DAT; the cookie-session CSRF check (Origin + X-XSRF-TOKEN) still applies.
        group.MapPost(string.Empty, StartAsync)
            .WithName("StartImport")
            .WithTags("Import")
            .WithSummary("Start an import: upload the DAT (and/or an OPT) with a saved or ad-hoc import profile; 202 with the import and its job.")
            .DisableAntiforgery()
            .Accepts<ImportStartForm>("multipart/form-data")
            .RequireIdempotencyKey()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListImports")
            .WithTags("Import")
            .WithSummary("Imports of the workspace, newest first, with their report counters and job progress.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{importId}", GetAsync)
            .WithName("GetImport")
            .WithTags("Import")
            .WithSummary("One import with its report (read / imported / overlaid / skipped / errored) and job progress.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // JSON pages: non-JSON downloads belong to the protected-content gateway (ADR-015 D12.1); the CSV error file
        // and import report are retained with the job by E08-T06.
        group.MapGet("/{importId}/errors", ListErrorsAsync)
            .WithName("ListImportErrors")
            .WithTags("Import")
            .WithSummary("Row-level errors (and warnings with includeWarnings=true) of an import, DAT rows then OPT rows, in row order.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{importId}/family-issues", ListFamilyIssuesAsync)
            .WithName("ListImportFamilyIssues")
            .WithTags("Import")
            .WithSummary("Family report of an import: orphan attachments, range gaps, documents claimed by two families, invalid ranges, cycles.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Accepted<ImportResource>, ValidationProblem, ProblemHttpResult>> StartAsync(
        string workspaceId,
        HttpContext context,
        IImportBatchStore batches,
        IImportProfileRepository profiles,
        IFieldCatalogRepository fields,
        IWorkspaceReader workspaces,
        IAuthorizationService authorization,
        IImportSourceStore sources,
        IOptions<ImportStartOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound("No such workspace.");
        }

        var ws = access.WorkspaceId;
        if (!sources.IsAvailable)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (!context.Request.HasFormContentType)
        {
            return Problems.Create(StatusCodes.Status415UnsupportedMediaType, ProblemCodes.UnsupportedMediaType, "Send multipart/form-data with a file and a request part.");
        }

        // Bound by hand: the form carries a file larger than any buffer, and the JSON part needs the API's options.
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files.GetFile("file") is { Length: > 0 } dat ? dat : null;
        var opt = form.Files.GetFile("opt") is { Length: > 0 } optFile ? optFile : null;
        if (file is null && opt is null)
        {
            return Validation("file", "The DAT file is required (or an OPT alone, to re-load the pages of existing documents).");
        }

        // The request part may also arrive as a JSON file part (a Blob in browser FormData, most HTTP clients' file
        // parts); ignoring it would run the import with default settings instead of the caller's profile.
        var requestJson = form["request"].ToString();
        if (requestJson.Length == 0 && form.Files.GetFile("request") is { } requestPart)
        {
            if (requestPart.Length > MaxRequestPartBytes)
            {
                return Validation("request", $"The request part has at most {MaxRequestPartBytes} bytes.");
            }

            using var reader = new StreamReader(requestPart.OpenReadStream(), Encoding.UTF8);
            requestJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (file?.Length > options.Value.MaxDatBytes || opt?.Length > options.Value.MaxDatBytes)
        {
            return Problems.Create(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge,
                $"A DAT or OPT of at most {options.Value.MaxDatBytes} bytes can be uploaded.");
        }

        // An OPT without a DAT replaces the pages of existing documents (ticket review E08-T05): an overlay.
        var imagesOnly = file is null;

        ImportStartRequest request;
        try
        {
            request = string.IsNullOrWhiteSpace(requestJson)
                ? new ImportStartRequest()
                : JsonSerializer.Deserialize<ImportStartRequest>(requestJson, json.Value.SerializerOptions) ?? new ImportStartRequest();
        }
        catch (JsonException ex)
        {
            return Validation("request", $"Not a valid import request: {ex.Message}");
        }

        var fileName = SafeFileName(file?.FileName, "loadfile.dat");
        var optFileName = SafeFileName(opt?.FileName, "images.opt");
        if (imagesOnly)
        {
            fileName = optFileName;
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{fileName} {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : request.Name.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return Validation("name", $"The import name has at most {MaxNameLength} characters and no control characters.");
        }

        // Profile: saved, ad hoc or defaults; the mode chosen in step 1 wins.
        var profile = request.Profile;
        long? profileVersion = null;
        if (request.ProfileId is { } profileId)
        {
            if (profile is not null)
            {
                return Validation("profile", "Give either profileId or profile, not both.");
            }

            if (await profiles.GetAsync(ws, profileId, cancellationToken).ConfigureAwait(false) is not { } saved)
            {
                return Problems.NotFound("No such import profile.");
            }

            profile = ImportProfileRules.Deserialize(saved.DefinitionJson);
            profileVersion = saved.Version;
        }

        profile ??= new ImportProfileDefinition();
        if (request.Mode is { } mode)
        {
            profile = profile with { Mode = mode };
        }

        if (imagesOnly)
        {
            if (request.Mode is ImportMode.Append or ImportMode.AppendOverlay)
            {
                return Validation("mode", "An OPT without a DAT replaces the pages of existing documents: use mode overlay.");
            }

            if (request.CodingOverlayFieldIds.Count > 0)
            {
                return Validation("codingOverlayFieldIds", "An OPT-only load loads no field values.");
            }

            profile = profile with { Mode = ImportMode.Overlay };
        }

        var codingFields = request.CodingOverlayFieldIds.Distinct().Order().ToList();
        profile = profile with { Overlay = profile.Overlay with { AllowCodingFieldOverlay = codingFields.Count > 0 } };

        // Authorization beyond Import.Run (Q-31: coding overlay is Workspace Admin only via Import.Overlay).
        if (profile.Mode != ImportMode.Append || codingFields.Count > 0)
        {
            var decision = await authorization.AuthorizeAsync(access.Principal, ws, Permission.ImportOverlay, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return AuthorizationResults.Problem(decision);
            }
        }

        // The system fields must exist before the header is mapped (Control Number, file metadata).
        await WorkspaceProvisioning.EnsureFieldsAsync(context.RequestServices, ws, cancellationToken).ConfigureAwait(false);

        // The header decides the mapping; validate it before anything is stored.
        var workspace = await workspaces.GetAsync(ws, cancellationToken).ConfigureAwait(false);
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var settingsIssues = new List<MappingIssue>();
        var readerOptions = ImportSource.ReaderOptions(profile, null, settingsIssues);
        if (settingsIssues.Any(i => i.Severity == MappingIssueSeverity.Error))
        {
            return MappingProblem(settingsIssues);
        }

        if (imagesOnly)
        {
            return await StartImagesOnlyAsync(context, access, batches, sources, opt!, name, optFileName, profile, request, profileVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        CompiledMapping mapping;
        var sample = file!.OpenReadStream();
        await using (sample.ConfigureAwait(false))
        {
            var reader = await DatReader.OpenAsync(sample, readerOptions, leaveOpen: true, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (reader.HasPreflightErrors)
                {
                    return Problems.Validation(new Dictionary<string, string[]>
                    {
                        ["file"] = [.. reader.PreflightIssues.Where(i => i.Severity == DatIssueSeverity.Error).Select(i => i.Message)],
                    }, "The load file cannot be read with these settings.");
                }

                mapping = MappingCompiler.Compile(profile, reader.Header.Names, catalog, new MappingOptions
                {
                    AutoMap = request.AutoMap,
                    ControlNumberCaseSensitive = workspace?.ControlNumberCaseSensitive ?? false,
                    MultiValueDelimiter = readerOptions.Profile.MultiValue,
                });
            }
        }

        var errors = mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).ToList();
        var mappedCoding = mapping.Targets.Where(t => t.Usable && t.FieldId is not null
                && (t.Definition.Storage == FieldStorage.Coding || t.Definition.IsSecurityAffecting))
            .Select(t => t.FieldId!.Value).ToHashSet();
        errors.AddRange(mappedCoding.Where(id => !codingFields.Contains(id)).Select(id => new MappingIssue(
            MappingIssueSeverity.Error, "coding-field-not-enabled", $"Coding field {id} is mapped but not listed in codingOverlayFieldIds (Q-31).")));
        errors.AddRange(codingFields.Where(id => !mappedCoding.Contains(id)).Select(id => new MappingIssue(
            MappingIssueSeverity.Error, "coding-field-not-mapped", $"Field {id} is enabled for overlay but no column maps a coding field with that id.")));
        if (errors.Count > 0)
        {
            return MappingProblem(errors);
        }

        var createsFields = mapping.Targets.Any(t => t.CreatesField is not null)
            || mapping.Columns.Any(c => c.Parsing?.CreateMissingChoices == true);
        if (createsFields)
        {
            var decision = await authorization.AuthorizeAsync(access.Principal, ws, Permission.WorkspaceManageFields, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return AuthorizationResults.Problem(decision);
            }
        }

        // Content-addressed upload (ADR-011): a retried start stores nothing new.
        var importId = Guid.CreateVersion7();
        var source = await sources.StoreAsync(ws, importId, file.OpenReadStream, file.Length, cancellationToken).ConfigureAwait(false);
        ImportOptSource? optSource = null;
        if (opt is not null)
        {
            var storedOpt = await sources.StoreAsync(ws, importId, opt.OpenReadStream, opt.Length, cancellationToken).ConfigureAwait(false);
            optSource = new ImportOptSource(optFileName, storedOpt.ObjectKey, storedOpt.Sha256, storedOpt.Size);
        }

        var principal = access.Principal;
        var creation = await batches.CreateAsync(new NewImportBatch
        {
            WorkspaceId = ws,
            ImportBatchId = importId,
            Name = name,
            Mode = profile.Mode,
            SourceFileName = fileName,
            SourceObjectKey = source.ObjectKey,
            SourceSha256 = source.Sha256,
            SourceSize = file.Length,
            Opt = optSource,
            ProfileId = request.ProfileId,
            ProfileVersion = profileVersion,
            ProfileJson = ImportProfileRules.Serialize(mapping.EffectiveProfile),
            CodingOverlayFieldIds = codingFields,
            // Only what was authorized above; the worker refuses to create fields or choices without it.
            MayCreateFields = createsFields,
            InitiatedBy = principal.UserId,
            ClientIdempotencyKey = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString() is { Length: > 0 } clientKey ? clientKey : null,
            CorrelationId = principal.CorrelationId,
            AuditTemplate = StartedAudit(principal),
        }, cancellationToken).ConfigureAwait(false);

        var resource = ToResource(creation.Batch, creation.Job);
        return ApiResults.JobAccepted(ws.ToString(), creation.Job.JobId.ToString(), resource);
    }

    /// <summary>An OPT alone: stored as the import's source; its documents overlay the pages of existing documents.</summary>
    private static async Task<Results<Accepted<ImportResource>, ValidationProblem, ProblemHttpResult>> StartImagesOnlyAsync(
        HttpContext context, WorkspaceAccess access, IImportBatchStore batches, IImportSourceStore sources, IFormFile opt, string name,
        string optFileName, ImportProfileDefinition profile, ImportStartRequest request, long? profileVersion, CancellationToken cancellationToken)
    {
        var ws = access.WorkspaceId;
        var importId = Guid.CreateVersion7();
        var stored = await sources.StoreAsync(ws, importId, opt.OpenReadStream, opt.Length, cancellationToken).ConfigureAwait(false);
        var principal = access.Principal;
        var creation = await batches.CreateAsync(new NewImportBatch
        {
            WorkspaceId = ws,
            ImportBatchId = importId,
            Name = name,
            Mode = ImportMode.Overlay,
            SourceFileName = optFileName,
            SourceObjectKey = stored.ObjectKey,
            SourceSha256 = stored.Sha256,
            SourceSize = stored.Size,
            Opt = new ImportOptSource(optFileName, stored.ObjectKey, stored.Sha256, stored.Size),
            ImagesOnly = true,
            ProfileId = request.ProfileId,
            ProfileVersion = profileVersion,
            ProfileJson = ImportProfileRules.Serialize(profile),
            InitiatedBy = principal.UserId,
            ClientIdempotencyKey = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString() is { Length: > 0 } clientKey ? clientKey : null,
            CorrelationId = principal.CorrelationId,
            AuditTemplate = StartedAudit(principal),
        }, cancellationToken).ConfigureAwait(false);

        var resource = ToResource(creation.Batch, creation.Job);
        return ApiResults.JobAccepted(ws.ToString(), creation.Job.JobId.ToString(), resource);
    }

    private static AuditEvent StartedAudit(SecurityPrincipal principal) => new()
    {
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Import.Category,
        Action = AuditTaxonomy.Import.Started,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        Outcome = AuditOutcome.Success,
        CorrelationId = principal.CorrelationId,
    };

    private static string SafeFileName(string? uploaded, string fallback)
    {
        var name = Path.GetFileName(uploaded ?? string.Empty).Trim();
        return string.IsNullOrEmpty(name) || name.Any(char.IsControl) ? fallback : name[..Math.Min(name.Length, 255)];
    }

    internal static async Task<Results<Ok<CursorPage<ImportResource>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IImportBatchStore batches, IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound("No such workspace.");
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        ImportBatchCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (DecodeCursor(cursor) is not { } decoded)
            {
                return Validation("cursor", "The cursor is not valid.");
            }

            after = decoded;
        }

        var limit = page.EffectiveLimit;
        var records = await batches.ListAsync(access.WorkspaceId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = new List<ImportResource>(Math.Min(records.Count, limit));
        foreach (var record in records.Take(limit))
        {
            if (await jobs.GetAsync(access.WorkspaceId, record.JobId, cancellationToken).ConfigureAwait(false) is { } job)
            {
                items.Add(ToResource(record, job));
            }
        }

        var next = records.Count > limit ? EncodeCursor(new ImportBatchCursor(records[limit - 1].CreatedAt, records[limit - 1].ImportBatchId)) : null;
        return TypedResults.Ok(new CursorPage<ImportResource>(items, next, new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<Results<Ok<ImportResource>, ProblemHttpResult>> GetAsync(
        string workspaceId, string importId, HttpContext context, IImportBatchStore batches, IJobRepository jobs, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(importId, out var id)
            || await batches.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } record
            || await jobs.GetAsync(access.WorkspaceId, record.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound("No such import.");
        }

        return TypedResults.Ok(ToResource(record, job));
    }

    internal static async Task<Results<Ok<CursorPage<ImportRowIssueResource>>, ValidationProblem, ProblemHttpResult>> ListErrorsAsync(
        string workspaceId, string importId, bool? includeWarnings, [AsParameters] PageQuery page, HttpContext context, IImportBatchStore batches,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(importId, out var id)
            || await batches.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } record)
        {
            return Problems.NotFound("No such import.");
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        ImportRowIssueCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (DecodeIssueCursor(cursor) is not { } decoded)
            {
                return Validation("cursor", "The cursor is not valid.");
            }

            after = decoded;
        }

        var limit = page.EffectiveLimit;
        var severity = includeWarnings == true ? (ImportIssueSeverity?)null : ImportIssueSeverity.Error;
        var issues = await batches.GetRowIssuesAsync(access.WorkspaceId, record.ImportBatchId, severity, after, limit + 1, cancellationToken)
            .ConfigureAwait(false);
        var items = issues.Take(limit).Select(i => new ImportRowIssueResource(
            i.RowNo, i.LineNo, i.Severity == ImportIssueSeverity.Error ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning,
            i.ControlNumber, i.Column, i.Code, i.Message, i.Source == ImportIssueSource.Opt ? ImportIssueFile.Opt : ImportIssueFile.Dat)).ToList();
        var next = issues.Count > limit
            ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"{issues[limit - 1].RowNo}|{issues[limit - 1].IssueNo}|{(short)issues[limit - 1].Source}")))
            : null;
        return TypedResults.Ok(new CursorPage<ImportRowIssueResource>(
            items, next, new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<Results<Ok<CursorPage<ImportFamilyIssueResource>>, ValidationProblem, ProblemHttpResult>> ListFamilyIssuesAsync(
        string workspaceId, string importId, [AsParameters] PageQuery page, HttpContext context, IImportBatchStore batches,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(importId, out var id)
            || await batches.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } record)
        {
            return Problems.NotFound("No such import.");
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        ImportRowIssueCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (DecodeIssueCursor(cursor) is not { } decoded)
            {
                return Validation("cursor", "The cursor is not valid.");
            }

            after = decoded;
        }

        var limit = page.EffectiveLimit;
        var issues = await batches.GetFamilyIssuesAsync(access.WorkspaceId, record.ImportBatchId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = issues.Take(limit).Select(i => new ImportFamilyIssueResource(
            i.RowNo, i.DocumentId, i.ControlNumber, (ImportFamilyIssueKind)((int)i.Kind - 1), (ImportFamilyStatus)(int)i.Status, i.Message, i.Related,
            i.MissingCount)).ToList();
        var next = issues.Count > limit
            ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{issues[limit - 1].RowNo}|{issues[limit - 1].IssueNo}")))
            : null;
        return TypedResults.Ok(new CursorPage<ImportFamilyIssueResource>(
            items, next, new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    private static ImportRowIssueCursor? DecodeIssueCursor(string cursor)
    {
        try
        {
            // row|issue (DAT) or row|issue|source.
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('|');
            var source = ImportIssueSource.Dat;
            return parts.Length is 2 or 3 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var row)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issue)
                && (parts.Length == 2 || (short.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                    && Enum.IsDefined((ImportIssueSource)s) && (source = (ImportIssueSource)s) == source))
                    ? new ImportRowIssueCursor(row, issue, source)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static ImportResource ToResource(ImportBatchRecord record, JobInfo job) => new(
        record.ImportBatchId,
        record.WorkspaceId,
        record.Name,
        record.Mode,
        record.SourceFileName,
        record.SourceSize,
        Convert.ToHexStringLower(record.SourceSha256),
        record.ProfileId,
        record.CodingOverlayFieldIds,
        new ImportReport(
            record.Preparation?.RowsTotal,
            record.RowsImported,
            record.RowsOverlaid,
            record.RowsSkipped,
            record.RowsErrored,
            record.Preparation?.FieldsCreated ?? 0,
            record.Preparation?.ChoicesCreated ?? 0),
        JobEndpoints.ToResource(job),
        record.CreatedAt,
        record.CompletedAt,
        record.Opt?.FileName,
        record.ImagesOnly);

    private static string EncodeCursor(ImportBatchCursor cursor) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            cursor.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + cursor.ImportBatchId.ToString("N")));

    private static ImportBatchCursor? DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && Guid.TryParseExact(parts[1], "N", out var id)
                    ? new ImportBatchCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ValidationProblem Validation(string field, string message) =>
        Problems.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static ValidationProblem MappingProblem(IEnumerable<MappingIssue> issues) =>
        Problems.Validation(
            issues.GroupBy(i => i.Column is null ? "mapping" : "mapping." + i.Column)
                .ToDictionary(g => g.Key, g => g.Select(i => $"{i.Code}: {i.Message}").ToArray()),
            "The import profile does not fit the load file.");
}

/// <summary>
/// Form of <c>POST …/imports</c>: <c>file</c> (the DAT), <c>opt</c> (an optional OPT image cross-reference; alone, an
/// OPT-only page re-load) and <c>request</c> (JSON <see cref="ImportStartRequest"/>).
/// </summary>
public sealed class ImportStartForm
{
    public IFormFile? File { get; set; }

    public IFormFile? Opt { get; set; }

    public string? Request { get; set; }
}

/// <summary>Import upload settings, section <c>Import</c>.</summary>
public sealed class ImportStartOptions
{
    public const string SectionName = "Import";

    /// <summary>Largest DAT accepted by <c>POST …/imports</c> (natives and text are linked, not uploaded here).</summary>
    [System.ComponentModel.DataAnnotations.Range(1024, long.MaxValue)]
    public long MaxDatBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

public static class ImportEndpointRegistration
{
    /// <summary>
    /// The import API with its stores. Object storage comes from the content gateway's registration
    /// (<c>AddProtectedContentGateway</c>, <c>ObjectStorage</c> section); without it, starting an import answers 503.
    /// </summary>
    public static IServiceCollection AddImportEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ImportStartOptions>().Bind(configuration.GetSection(ImportStartOptions.SectionName)).ValidateDataAnnotations();
        services.TryAddSingleton<IImportBatchStore, ImportBatchRepository>();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IWorkspaceReader, WorkspaceReader>();
        services.AddImportSourceStore();
        services.AddSingleton<IApiEndpointModule, ImportEndpoints>();
        return services;
    }
}
