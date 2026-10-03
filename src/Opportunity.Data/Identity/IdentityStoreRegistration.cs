using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Identity;
using Opportunity.Data.Security;

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

    /// <summary>Registers the PostgreSQL reader of the PDP's security state (E05-T02).</summary>
    public static IServiceCollection AddPostgresSecurityState(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISecurityStateReader, PostgresSecurityStateReader>();
        return services;
    }
}
