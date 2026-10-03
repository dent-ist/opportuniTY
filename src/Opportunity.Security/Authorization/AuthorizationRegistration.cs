using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using IAuthorizationService = Opportunity.Application.Authorization.IAuthorizationService;

namespace Opportunity.Security.Authorization;

public static class AuthorizationRegistration
{
    /// <summary>
    /// The PDP (<see cref="IAuthorizationService"/>, scoped: one principal-state read per request or job chunk). Needs
    /// an <see cref="ISecurityStateReader"/> (PostgreSQL in <c>Opportunity.Data</c>) and an <see cref="IAuditEventWriter"/>.
    /// PEP-1 is added to the pipeline by <c>UseOpportunityAuthentication</c>. Also adds the installation permission
    /// policies (<see cref="InstallationPermissions"/>, configuration section <c>Authorization</c>).
    /// </summary>
    public static IServiceCollection AddOpportunityAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuthorizationService, AuthorizationService>();

        services.AddOptions<InstallationAuthorizationOptions>().BindConfiguration(InstallationAuthorizationOptions.SectionName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, InstallationPermissionHandler>());
        services.AddAuthorizationBuilder()
            .AddPolicy(InstallationAuthorizationConventions.PolicyName(InstallationPermissions.ManageWorkspaces), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new InstallationPermissionRequirement(InstallationPermissions.ManageWorkspaces)));
        services.AddAuthorizationBuilder().AddPolicy(OwnProfileAuthorization.PolicyName, OwnProfileAuthorization.Policy);
        return services;
    }
}
