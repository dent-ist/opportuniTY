using AwesomeAssertions;

using Opportunity.Testing.Images;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>
/// The web image's nginx headers (deploy/docker/web/security-headers.conf) meet ADR-015 D4.4/D4.6: CSP without
/// inline or eval script, <c>frame-ancestors 'none'</c>, HSTS and <c>nosniff</c>. The image itself is exercised by
/// the container-image workflow; this pins the configuration it ships.
/// </summary>
public sealed class WebSecurityHeadersTests
{
    [Fact]
    public void Web_image_sends_the_adr_015_headers()
    {
        var root = Path.GetDirectoryName(VersionsFile.Locate())!;
        var config = File.ReadAllText(Path.Combine(root, "deploy", "docker", "web", "security-headers.conf"));

        var headers = SecurityHeaderAssertions.NginxHeaders(config);

        SecurityHeaderAssertions.AssertCommon(headers);
        var csp = SecurityHeaderAssertions.Directives(headers["Content-Security-Policy"]);
        csp["script-src"].Should().Equal("'self'");
        csp["object-src"].Should().Equal("'none'");
        csp["connect-src"].Should().Equal("'self'");
        csp["form-action"].Should().Equal("'self'");
    }
}
