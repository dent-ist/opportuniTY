using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Identity;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Security.Authorization;

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

    /// <summary>Overrides the signed-in user's <c>opp_uid</c>.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Comma-separated IdP groups of the signed-in user.</summary>
    public const string GroupsHeader = "X-Test-Groups";

    /// <summary>Comma-separated <c>amr</c> values of the signed-in user (e.g. to satisfy an MFA requirement).</summary>
    public const string AmrHeader = "X-Test-Amr";

    /// <summary>A sub-group of the workspace routes whose endpoints need membership only (test probe endpoints).</summary>
    public static RouteGroupBuilder MemberOnly(this ApiRouteGroups routes) =>
        routes.Workspace.MapGroup(string.Empty).RequireWorkspaceMember();

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
        services.AddAdminEverywhereSecurityState();
        return services;
    }

    public static IServiceCollection AddAdminEverywhereSecurityState(this IServiceCollection services)
    {
        services.RemoveAll<ISecurityStateReader>();
        services.AddSingleton<ISecurityStateReader, AdminEverywhereSecurityState>();
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

            var userId = Request.Headers.TryGetValue(UserHeader, out var user) ? user.ToString() : UserId;
            var claims = new List<Claim> { new("sub", Subject + ":" + userId), new("opp_uid", userId), new("name", "Test User") };
            if (Request.Headers.TryGetValue(GroupsHeader, out var groups))
            {
                claims.AddRange(groups.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim("groups", g)));
            }

            if (Request.Headers.TryGetValue(AmrHeader, out var amr))
            {
                claims.AddRange(amr.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => new Claim("amr", a)));
            }

            var identity = new ClaimsIdentity(claims, TestAuthentication.Scheme, "name", "groups");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestAuthentication.Scheme)));
        }
    }

    /// <summary>
    /// Stand-in for the PostgreSQL security state in API tests that are not about authorization: every user is a
    /// Workspace Admin of every workspace, and every document exists unrestricted. Authorization tests use the real reader.
    /// </summary>
    private sealed class AdminEverywhereSecurityState : ISecurityStateReader
    {
        public Task<SecurityStateRead> ReadAsync(
            Guid workspaceId, SecurityPrincipal principal, bool includePrincipal, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
        {
            var state = includePrincipal
                ? new PrincipalSecurityState(
                    WorkspaceStatus.Active,
                    new HashSet<WorkspaceRole> { WorkspaceRole.WorkspaceAdmin },
                    new Dictionary<string, IReadOnlySet<WorkspaceRole>>(),
                    new HashSet<Guid>(),
                    BreakGlassExpiresAt: null)
                : null;
            var documents = (documentIds ?? []).Distinct().ToDictionary(id => id, _ => DocumentSecurityAttributes.Unrestricted);
            return Task.FromResult(new SecurityStateRead(state, documents));
        }
    }
}
