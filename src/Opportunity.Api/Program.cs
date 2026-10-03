using Opportunity.Api.Conventions;
using Opportunity.Api.Search;
using Opportunity.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddOpportunityHostDefaults();
builder.Services.AddApiConventions();
builder.Services.AddQueryValidation();

var app = builder.Build();

app.UseApiConventions();
app.MapOpportunityHostDefaults();
app.MapOpenApi();
app.MapApiV1();

app.Run();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
