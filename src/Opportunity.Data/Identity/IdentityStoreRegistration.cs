using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

public static class IdentityStoreRegistration
{
    /// <summary>Registers the PostgreSQL user, session, key-ring and workspace-policy stores (resolved lazily).</summary>
    public static IServiceCollection AddPostgresIdentityStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IUserDirectory, PostgresUserDirectory>();
        services.TryAddSingleton<ISessionStore, PostgresSessionStore>();
        services.TryAddSingleton<IDataProtectionKeyStore, PostgresDataProtectionKeyStore>();
        services.TryAddSingleton<IWorkspaceAuthenticationPolicy, PostgresWorkspaceAuthenticationPolicy>();
        return services;
    }
}
