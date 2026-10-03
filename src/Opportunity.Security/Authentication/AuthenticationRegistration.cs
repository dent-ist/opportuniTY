using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Opportunity.Application.Audit;
using Opportunity.Application.Identity;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Security.Authentication;

public static class AuthenticationRegistration
{
    public const string DataProtectionApplicationName = "opportunity";

    /// <summary>
    /// OIDC sign-in through the BFF session scheme, the authenticated-by-default fallback policy, anti-forgery and the
    /// shared Data Protection key ring. Needs the <see cref="ISessionStore"/>, <see cref="IUserDirectory"/>,
    /// <see cref="IDataProtectionKeyStore"/> and <see cref="IWorkspaceAuthenticationPolicy"/> ports (PostgreSQL in
    /// <c>Opportunity.Data</c>) and an <see cref="IAuditEventWriter"/> (the PostgreSQL audit store, E14-T01): there is
    /// deliberately no no-op fallback, so a host without an audit store cannot resolve sign-in instead of signing in unaudited.
    /// </summary>
    public static IServiceCollection AddOpportunityAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OpportunityAuthenticationOptions>()
            .BindConfiguration(OpportunityAuthenticationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpportunityAuthenticationOptions>, OpportunityAuthenticationOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SessionTokenProtector>();
        services.TryAddSingleton<AuthenticationAudit>();
        services.TryAddSingleton<OidcPrincipalRefresher>();
        services.TryAddSingleton<SignOutService>();
        services.TryAddSingleton<BackChannelLogoutService>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = AuthenticationSchemes.Session;
                options.DefaultChallengeScheme = AuthenticationSchemes.Session;
                options.DefaultForbidScheme = AuthenticationSchemes.Session;
                options.DefaultSignInScheme = AuthenticationSchemes.Session;
                options.DefaultSignOutScheme = AuthenticationSchemes.Session;
            })
            .AddScheme<SessionAuthenticationOptions, SessionAuthenticationHandler>(AuthenticationSchemes.Session, displayName: null, _ => { })
            .AddOpenIdConnect(AuthenticationSchemes.Oidc, _ => { });
        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, OidcConfiguration>();

        // Every endpoint requires a signed-in user unless it opts out with AllowAnonymous (ADR-015 D5.2 step 1).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddAntiforgery(options =>
        {
            options.HeaderName = SessionCookies.AntiforgeryHeader;
            options.Cookie.Name = SessionCookies.Antiforgery;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.SuppressXFrameOptionsHeader = true;
        });

        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
        services.AddOptions<KeyManagementOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                options.XmlRepository = new KeyStoreXmlRepository(provider));

        services.AddHostedService<SessionCleanupService>();
        return services;
    }

    /// <summary>Security headers first, so errors and redirects carry them too.</summary>
    public static IApplicationBuilder UseOpportunitySecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Authentication, then CSRF (needs the principal and the endpoint), then authorization (signed-in user), then
    /// workspace membership and permission (PEP-1, needs <c>AddOpportunityAuthorization</c>). After routing.
    /// </summary>
    public static IApplicationBuilder UseOpportunityAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<WorkspaceAuthorizationMiddleware>();
        return app;
    }
}
