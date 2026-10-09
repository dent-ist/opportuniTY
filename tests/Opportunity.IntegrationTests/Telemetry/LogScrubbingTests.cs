using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Opportunity.Hosting.Telemetry;

namespace Opportunity.IntegrationTests.Telemetry;

/// <summary>
/// E05-T09 / ADR-015 D10.5: connection strings, tokens and signed URLs that reach a log message by mistake are redacted
/// before any OpenTelemetry exporter sees the record (the capture sits where the OTLP exporter does). Fake values only.
/// </summary>
public sealed partial class LogScrubbingTests : IAsyncLifetime
{
    private const string FakePassword = "test-only-db-password";
    private const string FakeToken = "test-only-bearer-token-value";
    private const string FakeSignature = "test-only-signature";
    private const string FakeAccountKey = "test-only-account-key";

    private readonly TelemetryCapture _capture = new();
    private WebApplication _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(["--Telemetry:Enabled=true"]);
        builder.WebHost.UseTestServer();
        builder.AddOpportunityTelemetry();
        _capture.AddTo(builder.Services);
        _app = builder.Build();
        await _app.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Connection_strings_tokens_and_signed_urls_are_redacted_from_exported_log_records()
    {
        var logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Opportunity.Tests.Leaky");
        var connectionString = $"Host=postgres;Database=opportunity;Username=opportunity_runtime;Password={FakePassword}";

        LeakyLog.Connecting(logger, connectionString);
        LeakyLog.Calling(logger, $"Bearer {FakeToken}", new Uri($"https://store.invalid/b/o?X-Amz-Signature={FakeSignature}&X-Amz-Expires=60"));
        LeakyLog.Failed(logger, new InvalidOperationException($"could not open AccountName=dev;AccountKey={FakeAccountKey};EndpointSuffix=core"));
        LeakyLog.Harmless(logger, 42);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_capture.Logs.Count(l => l.Category == "Opportunity.Tests.Leaky") < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        var records = _capture.Logs.Where(l => l.Category == "Opportunity.Tests.Leaky").ToList();
        records.Should().HaveCount(4);
        var exported = string.Join('\n', records.SelectMany(r => r.Text()));
        exported.Should().NotContainAny([FakePassword, FakeToken, FakeSignature, FakeAccountKey]);
        exported.Should().Contain("Host=postgres", "only the credential is masked, not the diagnostic context");
        exported.Should().Contain("Processed 42 items");
        records.Should().ContainSingle(r => r.Message.StartsWith("Request failed", StringComparison.Ordinal))
            .Which.Exception.Should().Contain("System.InvalidOperationException").And.Contain("AccountName=dev");
    }

    private static partial class LeakyLog
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Connecting with {ConnectionString}")]
        public static partial void Connecting(ILogger logger, string connectionString);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Calling with {Authorization} at {Url}")]
        public static partial void Calling(ILogger logger, string authorization, Uri url);

        [LoggerMessage(Level = LogLevel.Error, Message = "Request failed")]
        public static partial void Failed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Information, Message = "Processed {Count} items")]
        public static partial void Harmless(ILogger logger, int count);
    }
}
