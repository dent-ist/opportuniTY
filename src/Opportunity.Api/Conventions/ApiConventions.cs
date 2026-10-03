using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Conventions.Json;
using Opportunity.Application.Idempotency;
using Opportunity.Hosting.Options;
using Opportunity.Security.Authentication;

namespace Opportunity.Api.Conventions;

/// <summary>Wires the ADR-019 §2 HTTP conventions into the API host.</summary>
public static class ApiConventions
{
    public const string OpenApiDocumentName = "v1";

    public static IServiceCollection AddApiConventions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options =>
        {
            var json = options.SerializerOptions;
            json.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            json.Converters.Add(new UtcDateTimeOffsetConverter());
            json.Converters.Add(new UtcDateTimeConverter());
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.AddValidatedOptions<IdempotencyOptions>(IdempotencyOptions.SectionName);
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

        services.AddOpenApi(OpenApiDocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.ShouldInclude = description =>
                description.RelativePath?.StartsWith(ApiRoutes.V1Prefix[1..] + "/", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "opportuniTY API",
                    Version = "v1",
                    Description = "Conventions: docs/adr/0019-layering-and-api-conventions.md §2.",
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequiresIdempotencyKeyMetadata>().Any())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyMiddleware.HeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Opaque client-chosen key; a retry with the same key and body returns the original response.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = IdempotencyMiddleware.MaxKeyLength },
                    });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Middleware order matters: security headers and errors outermost; authentication, CSRF and authorization after
    /// routing (they read endpoint metadata) and before idempotency (keys are scoped to the user).
    /// </summary>
    public static WebApplication UseApiConventions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseOpportunitySecurityHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseRouting();
        app.UseOpportunityAuthentication();
        app.UseMiddleware<WorkspaceTelemetryMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        return app;
    }
}
