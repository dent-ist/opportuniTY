using System.Reflection;

using Opportunity.Api.Conventions;
using Opportunity.Api.Import;
using Opportunity.Api.Jobs;
using Opportunity.Api.Search;
using Opportunity.Api.Workspaces;
using Opportunity.Data.Audit;
using Opportunity.Data.Identity;
using Opportunity.Hosting;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Build-time OpenAPI generation (dotnet-getdocument) starts the host without installation configuration; give it
// inert placeholders so options validation passes. Real hosts must configure Authentication (ValidateOnStart).
if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:PublicOrigin"] = "https://localhost",
        ["Authentication:Oidc:Authority"] = "https://idp.invalid",
        ["Authentication:Oidc:ClientId"] = "openapi-generation",
    });
}

builder.AddOpportunityHostDefaults();
builder.Services.AddApiConventions();
builder.Services.AddQueryValidation();
builder.Services.AddJobEndpoints();
builder.Services.AddWorkspaceEndpoints(builder.Configuration);
builder.Services.AddPostgresIdentityStores();
builder.Services.AddPostgresSecurityState();
builder.Services.AddImportMappingEndpoints();
builder.Services.AddPostgresAuditStore();
builder.Services.AddOpportunityAuthentication();
builder.Services.AddOpportunityAuthorization();

var app = builder.Build();

app.UseApiConventions();
app.MapOpportunityHostDefaults();
app.MapOpenApi().AllowAnonymous();
app.MapApiV1();

app.Run();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
