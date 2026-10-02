var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;