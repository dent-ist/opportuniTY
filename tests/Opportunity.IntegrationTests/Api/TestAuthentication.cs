using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Xml.Linq;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Opportunity.Application.Identity;

namespace Opportunity.IntegrationTests.Api;

/// <summary>
/// For API tests that are not about authentication: placeholder OIDC settings (no IdP is contacted) and a test scheme
/// that signs every request in as one user. Header-based, so CSRF (a cookie-session control) does not apply.
/// The real sign-in, session and CSRF behaviour is covered by tests/.../Auth.
/// </summary>
internal static class TestAuthentication
{
    public const string Scheme = "Test";
    public const string Subject = "test-user";
    public const string UserId = "0199a8a0-0000-7000-8000-000000000001";

    /// <summary>Send this header to be treated as anonymous.</summary>
    public const string AnonymousHeader = "X-Test-Anonymous";

    public static IWebHostBuilder UsePlaceholderAuthenticationSettings(this IWebHostBuilder builder)
    {
        builder.UseSetting("Authentication:PublicOrigin", "https://localhost");
        builder.UseSetting("Authentication:Oidc:Authority", "https://idp.invalid");
        builder.UseSetting("Authentication:Oidc:ClientId", "opportunity-test");
        return builder;
    }

    public static IServiceCollection AddTestUserAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestUserHandler>(Scheme, _ => { });
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = Scheme;
            options.DefaultAuthenticateScheme = Scheme;
            options.DefaultChallengeScheme = Scheme;
            options.DefaultForbidScheme = Scheme;
        });
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new InMemoryXmlRepository());
        services.AddSingleton<IWorkspaceAuthenticationPolicy, NoMfaRequired>();
        return services;
    }

    /// <summary>Keeps the key ring off PostgreSQL, so tests that count database activity see only their own.</summary>
    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
            {
                return [.. _elements];
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(element);
            }
        }
    }

    private sealed class NoMfaRequired : IWorkspaceAuthenticationPolicy
    {
        public Task<bool> RequiresMfaAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class TestUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey(AnonymousHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("sub", Subject), new Claim("opp_uid", UserId), new Claim("name", "Test User")], TestAuthentication.Scheme, "name", "groups");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestAuthentication.Scheme)));
        }
    }
}
