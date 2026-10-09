using System.Collections;

using AwesomeAssertions;

using Opportunity.Application.Keys;
using Opportunity.Security.Keys;

namespace Opportunity.UnitTests.Keys;

public sealed class SecretProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opportunity-secrets-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task File_provider_reads_docker_secret_files_without_the_trailing_line_break()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "db-app"), "test-only-value\n", Ct);
        var provider = new FileSecretProvider(_dir);

        using var secret = await provider.GetAsync("db-app", Ct);

        secret!.Reveal().Should().Be("test-only-value");
        (await provider.GetAsync("absent", Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task Names_that_could_escape_the_secrets_directory_are_rejected(string name)
    {
        var provider = new FileSecretProvider(_dir);

        var read = async () => await provider.GetAsync(name, Ct);

        await read.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Environment_provider_prefers_the_file_variable_and_composite_takes_the_first_source()
    {
        var path = Path.Combine(_dir, "from-file");
        await File.WriteAllTextAsync(path, "test-only-file\r\n", Ct);
        var variables = new Dictionary<string, string>
        {
            ["OPPORTUNITY_SECRET_ENVELOPE_HMAC_FILE"] = path,
            ["OPPORTUNITY_SECRET_ENVELOPE_HMAC"] = "test-only-plain",
            ["OPPORTUNITY_SECRET_OTHER"] = "test-only-other",
        };
        var environment = new EnvironmentSecretProvider(v => variables.GetValueOrDefault(v));
        await File.WriteAllTextAsync(Path.Combine(_dir, "other"), "test-only-docker", Ct);
        var composite = new CompositeSecretProvider([new FileSecretProvider(_dir), environment]);

        EnvironmentSecretProvider.VariableName("envelope-hmac").Should().Be("OPPORTUNITY_SECRET_ENVELOPE_HMAC");
        using (var fromFile = await environment.GetAsync("envelope-hmac", Ct))
        {
            fromFile!.Reveal().Should().Be("test-only-file");
        }

        using var first = await composite.GetAsync("other", Ct);
        first!.Reveal().Should().Be("test-only-docker");
    }

    [Fact]
    public void Secret_values_never_print_their_content()
    {
        using var secret = new SecretValue("test-only-value"u8);

        secret.ToString().Should().NotContain("test-only-value");
        $"{secret}".Should().Be("SecretValue(redacted)");
    }

    [Fact]
    public async Task File_convention_maps_section_variables_only_and_names_the_variable_when_the_file_is_missing()
    {
        var path = Path.Combine(_dir, "conn");
        await File.WriteAllTextAsync(path, "Host=db;Password=test-only\n", Ct);

        var resolved = SecretFileConvention.Resolve(new Hashtable
        {
            ["ConnectionStrings__App_FILE"] = path,
            ["SSL_CERT_FILE"] = "/does/not/exist",
            ["ConnectionStrings__Other"] = "untouched",
        });

        resolved.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string?>("ConnectionStrings:App", "Host=db;Password=test-only"));
        var missing = () => SecretFileConvention.Resolve(new Hashtable { ["ObjectStorage__S3__SecretKey_FILE"] = Path.Combine(_dir, "nope") });
        missing.Should().Throw<InvalidOperationException>().WithMessage("*ObjectStorage__S3__SecretKey_FILE*");
    }
}
