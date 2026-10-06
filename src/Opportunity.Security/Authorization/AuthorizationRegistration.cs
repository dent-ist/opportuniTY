using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Security;
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

        // Field-level restrictions (E05-T06): the stored policy wherever the security store exists. It replaces the
        // unrestricted default that endpoint and worker modules register, whichever ran first; a filter registered
        // explicitly (tests) is kept.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IFieldAccessFilter) && services[i].ImplementationType == typeof(UnrestrictedFieldAccess))
            {
                services.RemoveAt(i);
            }
        }

        services.TryAddSingleton<IFieldAccessFilter>(sp => sp.GetService<IDocumentSecurityStore>() is { } store
            ? new StoredFieldAccess(store)
            : new UnrestrictedFieldAccess());

        services.AddOptions<InstallationAuthorizationOptions>().BindConfiguration(InstallationAuthorizationOptions.SectionName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, InstallationPermissionHandler>());
        // Core authorization only: the policies are plain options, so non-web hosts (workers, test harnesses) can use the
        // PDP without ASP.NET routing. The API's full authorization services come from AddOpportunityAuthentication.
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(InstallationAuthorizationConventions.PolicyName(InstallationPermissions.ManageWorkspaces), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new InstallationPermissionRequirement(InstallationPermissions.ManageWorkspaces)));
            options.AddPolicy(OwnProfileAuthorization.PolicyName, OwnProfileAuthorization.Policy);
        });
        return services;
    }
}
