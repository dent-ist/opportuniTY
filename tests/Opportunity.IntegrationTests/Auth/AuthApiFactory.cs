using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using Opportunity.Api.Conventions;
using Opportunity.Application.Audit;
using Opportunity.Data.Migrations;
using Opportunity.Hosting.Health;
using Opportunity.Security.Authentication;
using Opportunity.Testing.Postgres;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>PostgreSQL whose template database is fully migrated once; every test clones it.</summary>
public sealed class IdentityPostgresFixture : PostgresFixture
{
    protected override async Task SeedTemplateAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var template = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = TemplateDatabase, Pooling = false };
        await using var dataSource = NpgsqlDataSource.Create(template.ConnectionString);
        await new PostgresMigrator(dataSource).MigrateAsync(cancellationToken);
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityPostgresGroup : ICollectionFixture<IdentityPostgresFixture>
{
    public const string Name = "Identity PostgreSQL";
}

/// <summary>
/// The real API host (session scheme, OIDC handler, CSRF, headers, PostgreSQL stores) with a controllable clock and an
/// in-memory audit writer. Defaults to <see cref="FakeOidcProvider"/> as the IdP (pass it as <c>backchannel</c>);
/// <c>configure</c> can point it at a real provider instead (Keycloak tests).
/// </summary>
public sealed class AuthApiFactory(string connectionString, HttpMessageHandler? backchannel, Action<IWebHostBuilder>? configure = null)
    : WebApplicationFactory<Program>
{
    public const string MfaAcr = "urn:test:mfa";

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public InMemoryAuditEventWriter Audit { get; } = new();

    public TestBrowser CreateBrowser() =>
        new(CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(TestBrowser.Origin),
        }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{PostgresReadiness.ConnectionStringName}", connectionString);
        builder.UseSetting("Authentication:PublicOrigin", TestBrowser.Origin);
        builder.UseSetting("Authentication:Oidc:Authority", FakeOidcProvider.Issuer);
        builder.UseSetting("Authentication:Oidc:ClientId", FakeOidcProvider.ClientId);
        builder.UseSetting("Authentication:Oidc:ClientSecret", FakeOidcProvider.ClientSecret);
        builder.UseSetting("Authentication:Mfa:AcrValues:0", MfaAcr);
        configure?.Invoke(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IAuditEventWriter>(Audit);
            if (backchannel is not null)
            {
                services.Configure<OpenIdConnectOptions>(AuthenticationSchemes.Oidc, options => options.BackchannelHttpHandler = backchannel);
            }
            services.AddSingleton<IApiEndpointModule, AuthProbeEndpoints>();
        });
    }
}

/// <summary>Probe endpoints: a workspace-scoped read and write, and an installation endpoint that always needs MFA.</summary>
internal sealed class AuthProbeEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        routes.Workspace.MapGet("/auth-probe", (HttpContext context) =>
            TypedResults.Ok(new { user = context.User.FindFirst(OpportunityClaimTypes.UserId)?.Value }));
        routes.Workspace.MapPost("/auth-probe", () => TypedResults.Ok(new { changed = true }));
        routes.V1.MapPost("/auth-probe", () => TypedResults.Ok(new { changed = true }));
        routes.V1.MapGet("/admin-probe", () => TypedResults.Ok(new { admin = true })).RequireMfa();
    }
}
