using AwesomeAssertions;

using Opportunity.Security.Authentication;

namespace Opportunity.UnitTests.Security;

public class AuthenticationOptionsValidatorTests
{
    [Theory]
    [InlineData("https://review.example.com", true)]
    [InlineData("http://localhost", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://review.example.com", false)]
    [InlineData("http://10.0.0.5:8080", false)]
    [InlineData("ftp://review.example.com", false)]
    public void Plain_http_public_origin_is_allowed_for_loopback_hosts_only(string origin, bool valid)
    {
        var options = new OpportunityAuthenticationOptions
        {
            PublicOrigin = new Uri(origin),
            Oidc = { Authority = new Uri("https://idp.example.com/realms/opportunity"), ClientId = "opportunity-web" },
        };

        var result = new OpportunityAuthenticationOptionsValidator().Validate(null, options);

        result.Failures.Should().Match(failures => valid
            ? failures == null || !failures.Any(f => f.Contains("PublicOrigin must use https", StringComparison.Ordinal))
            : failures != null && failures.Any(f => f.Contains("PublicOrigin must use https", StringComparison.Ordinal)));
        new OpportunityAuthenticationOptions { PublicOrigin = new Uri(origin) }.IsLoopbackHttpOrigin
            .Should().Be(valid && origin.StartsWith("http://", StringComparison.Ordinal));
    }
}
