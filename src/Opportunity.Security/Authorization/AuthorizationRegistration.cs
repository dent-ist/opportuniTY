using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Security.Authorization;

public static class AuthorizationRegistration
{
    /// <summary>
    /// The PDP (<see cref="IAuthorizationService"/>, scoped: one principal-state read per request or job chunk). Needs
    /// an <see cref="ISecurityStateReader"/> (PostgreSQL in <c>Opportunity.Data</c>) and an <see cref="IAuditEventWriter"/>.
    /// PEP-1 is added to the pipeline by <c>UseOpportunityAuthentication</c>.
    /// </summary>
    public static IServiceCollection AddOpportunityAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuthorizationService, AuthorizationService>();
        return services;
    }
}
