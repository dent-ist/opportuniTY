using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Jobs;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/job-events</c> (E06-T06, Q-35): a <c>text/event-stream</c> of
/// <see cref="JobEvent"/>s (event type <c>job</c>, id = the job's <c>updatedAt</c>) for the jobs the caller may see,
/// with a heartbeat comment every 15 s. It is an ordinary GET of the API: the BFF session cookie authenticates it like
/// any other request, and PEP-1 admits workspace members only. Access is re-checked at every heartbeat: a member who
/// loses the workspace sees the stream end, and a revoked <c>Job.ViewAll</c> narrows it to the caller's own jobs. A
/// reconnect with <c>Last-Event-ID</c> first replays the changes since that event. The polling fallback is
/// <c>GET …/jobs?updatedSince=</c>.
/// </summary>
public sealed class JobEventEndpoints : IApiEndpointModule
{
    public const string ContentType = "text/event-stream";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet("/job-events", StreamAsync)
            .WithName("StreamJobEvents")
            .WithTags("Jobs")
            .WithSummary("Live job changes as server-sent events (event 'job': {jobId, status, committed, searchable, updatedAt}).")
            .WithDescription(
                "Your own jobs, or all with Job.ViewAll. A ': heartbeat' comment every 15 s; the stream ends after at most 30 minutes " +
                "and EventSource reconnects with Last-Event-ID. Fallback: GET …/jobs?updatedSince=<ISO 8601> every ≥ 5 s.")
            .Produces(StatusCodes.Status200OK, contentType: ContentType)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();
    }

    internal static async Task<IResult> StreamAsync(
        string workspaceId, HttpContext context, JobEventHub hub, IJobOperationsStore store, IAuthorizationService authorization,
        IServiceScopeFactory scopes, IOptions<JobEventOptions> options, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var settings = options.Value;
        var serializer = json.Value.SerializerOptions;
        var viewAll = await JobEndpoints.ViewAllAsync(access, authorization, cancellationToken).ConfigureAwait(false);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache, no-transform";
        // nginx (the compose web proxy) streams a response that says so; see deploy/docker-compose/web/api-proxy.conf.
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(settings.MaxStreamDuration);
        var ct = stop.Token;
        using var listener = hub.Listen(access.WorkspaceId, access.Principal.UserId, viewAll);
        try
        {
            await WriteAsync(response, $"retry: 5000\n: connected\n\n", ct).ConfigureAwait(false);

            // A reconnect continues after the last event it saw: replay what changed since (state, so repeats are harmless).
            if (DateTimeOffset.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var lastEventAt))
            {
                var missed = await store.ListAsync(new JobListQuery(access.WorkspaceId)
                {
                    InitiatedBy = viewAll ? null : access.Principal.UserId,
                    UpdatedSince = lastEventAt - settings.Overlap,
                    Limit = JobListQuery.MaxLimit,
                }, ct).ConfigureAwait(false);
                foreach (var overview in missed.Items)
                {
                    await WriteEventAsync(response, JobMapping.ToEvent(overview, missed.AppliedWatermark), serializer, ct).ConfigureAwait(false);
                }
            }

            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(settings.HeartbeatInterval);
                bool ready;
                try
                {
                    ready = await listener.Reader.WaitToReadAsync(heartbeat.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (!await RecheckAsync(listener, access, scopes, ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    await WriteAsync(response, ": heartbeat\n\n", ct).ConfigureAwait(false);
                    continue;
                }

                if (!ready)
                {
                    break;
                }

                while (listener.Reader.TryRead(out var jobEvent))
                {
                    await WriteEventAsync(response, jobEvent, serializer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away or the stream reached its maximum duration.
        }

        return Results.Empty;
    }

    /// <summary>Access as of now, in a fresh scope (the PDP caches a principal's state per request).</summary>
    private static async Task<bool> RecheckAsync(JobEventListener listener, WorkspaceAccess access, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var effective = await authorization.GetEffectivePermissionsAsync(access.Principal, access.WorkspaceId, ct).ConfigureAwait(false);
        if (!effective.Decision.IsAllowed)
        {
            return false;
        }

        listener.ViewAll = effective.Permissions.Contains(Permission.JobViewAll);
        return true;
    }

    private static Task WriteEventAsync(HttpResponse response, JobEvent jobEvent, JsonSerializerOptions serializer, CancellationToken ct) =>
        WriteAsync(response,
            $"event: job\nid: {jobEvent.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}\ndata: {JsonSerializer.Serialize(jobEvent, serializer)}\n\n",
            ct);

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}

public static class JobEventRegistration
{
    public static IServiceCollection AddJobEventEndpoints(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = configuration;
        services.AddOptions<JobEventOptions>().BindConfiguration(JobEventOptions.SectionName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JobEventHub>();
        services.AddSingleton<IApiEndpointModule, JobEventEndpoints>();
        return services;
    }
}
