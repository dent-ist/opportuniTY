using System.Buffers.Text;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Keys;

namespace Opportunity.UnitTests.Keys;

/// <summary>ADR-015 D10.5: credentials never leave the process in log text. Every value here is an obvious fake.</summary>
public class SecretRedactorTests
{
    private const string FakePassword = "test-only-not-a-password";

    public static TheoryData<string, string> Leaks => new()
    {
        { $"Host=postgres;Database=opportunity;Username=app;Password={FakePassword};GSS Encryption Mode=Disable", FakePassword },
        { $"Server=db;User Id=sa;Pwd={FakePassword}", FakePassword },
        { $"connecting to amqp://opportunity:{FakePassword}@rabbitmq:5672/", FakePassword },
        { $"DefaultEndpointsProtocol=https;AccountName=dev;AccountKey={FakePassword};EndpointSuffix=core", FakePassword },
        { $"request failed: Authorization: Bearer {FakePassword}", FakePassword },
        { $"token endpoint replied client_secret={FakePassword}&grant_type=code", FakePassword },
        { $"GET https://store.invalid/b/k?X-Amz-Credential=fake&X-Amz-Signature={FakePassword}&X-Amz-Expires=60", FakePassword },
        { $"GET https://blob.invalid/c/k?sv=2024&se=2026-01-01&sig={FakePassword}", FakePassword },
        { $"{{\"password\":\"{FakePassword}\"}}", FakePassword },
        { $"S3 SecretKey: {FakePassword}", FakePassword },
    };

    [Theory]
    [MemberData(nameof(Leaks))]
    public void Credentials_are_masked(string text, string secret)
    {
        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(SecretRedactor.Mask);
        SecretRedactor.ContainsCredential(text).Should().BeTrue();
    }

    [Fact]
    public void Jwts_and_pem_private_keys_are_masked()
    {
        // Built at run time so no token-shaped literal sits in the repository (gitleaks).
        static string Part(string json) => Encoding.ASCII.GetString(Base64Url.EncodeToUtf8(Encoding.UTF8.GetBytes(json)));
        var jwt = $"{Part("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Part("{\"sub\":\"test-only-subject\"}")}.c2lnbmF0dXJl";
        var pem = "-----BEGIN " + "PRIVATE KEY-----\nTUlJQ-test-only\n-----END " + "PRIVATE KEY-----";

        SecretRedactor.Redact($"id_token was {jwt} for the session").Should().NotContain(jwt);
        SecretRedactor.Redact($"loaded {pem} from disk").Should().NotContain("TUlJQ-test-only");
    }

    [Theory]
    [InlineData("Chunk applied with 25 documents")]
    [InlineData("Search returned 12 hits for workspace 3f0c7c0e1b8a4c7e9d2a5b6c7d8e9f00 in 35 ms")]
    [InlineData("GET /api/v1/workspaces/3f0c/documents?cursor=abc")]
    [InlineData("Token count exceeded")]
    public void Ordinary_text_is_left_unchanged(string text)
    {
        SecretRedactor.Redact(text).Should().Be(text);
    }
}
