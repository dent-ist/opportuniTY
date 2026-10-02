using Opportunity.Api.Conventions;
using Opportunity.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddOpportunityHostDefaults();
builder.Services.AddApiConventions();

var app = builder.Build();

app.UseApiConventions();
app.MapOpportunityHostDefaults();
app.MapOpenApi();
app.MapApiV1();

app.Run();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;