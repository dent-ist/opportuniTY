using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Keys;

namespace Opportunity.Security.Keys;

public static class KeyManagementRegistration
{
    /// <summary>
    /// Registers <see cref="ISecretProvider"/> (section <c>Secrets</c>), the key provider of
    /// <c>KeyManagement:Provider</c> (<see cref="IKeyEncryptionKeyProvider"/>, <see cref="ISigningKeyProvider"/>) and
    /// <see cref="WorkspaceKeyService"/> as the workspace key ring and crypto-shredder. Needs an
    /// <see cref="IWorkspaceDataKeyStore"/> (PostgreSQL, <c>Opportunity.Data</c>). Settings are read from
    /// <see cref="IConfiguration"/> when first resolved, and nothing touches the key store until first use, so hosts that
    /// never encrypt need no key directory.
    /// </summary>
    public static IServiceCollection AddOpportunityKeyManagement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => Bind<SecretOptions>(sp, SecretOptions.SectionName));
        services.TryAddSingleton(sp => Bind<KeyManagementOptions>(sp, KeyManagementOptions.SectionName));
        services.TryAddSingleton(sp => CreateSecretProvider(sp.GetRequiredService<SecretOptions>()));
        services.TryAddSingleton<IKeyEncryptionKeyProvider>(sp =>
        {
            var options = sp.GetRequiredService<KeyManagementOptions>();
            return options.Provider == KeyManagementOptions.LocalProvider
                ? new LocalKeyEncryptionKeyProvider(options.Local, sp.GetRequiredService<ISecretProvider>())
                : throw UnknownProvider(options.Provider);
        });
        services.TryAddSingleton<ISigningKeyProvider>(sp =>
        {
            var options = sp.GetRequiredService<KeyManagementOptions>();
            return options.Provider == KeyManagementOptions.LocalProvider
                ? new LocalSigningKeyProvider(options.Local, sp.GetRequiredService<ISecretProvider>())
                : throw UnknownProvider(options.Provider);
        });
        services.TryAddSingleton(sp => new WorkspaceKeyOptions { DataKeyCacheTtl = sp.GetRequiredService<KeyManagementOptions>().DataKeyCacheTtl });
        services.TryAddSingleton<WorkspaceKeyService>();
        services.TryAddSingleton<IWorkspaceDataKeyRing>(sp => sp.GetRequiredService<WorkspaceKeyService>());
        services.TryAddSingleton<IWorkspaceCryptoShredder>(sp => sp.GetRequiredService<WorkspaceKeyService>());
        return services;
    }

    public static ISecretProvider CreateSecretProvider(SecretOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<ISecretProvider> sources = [new FileSecretProvider(options.Directory)];
        if (options.AllowEnvironment)
        {
            sources.Add(new EnvironmentSecretProvider());
        }

        return new CompositeSecretProvider(sources);
    }

    private static T Bind<T>(IServiceProvider services, string section)
        where T : new() =>
        services.GetRequiredService<IConfiguration>().GetSection(section).Get<T>() ?? new T();

    private static InvalidOperationException UnknownProvider(string name) =>
        new($"{KeyManagementOptions.SectionName}:Provider '{name}' is not available in this build. Use '{KeyManagementOptions.LocalProvider}', "
            + "or register an IKeyEncryptionKeyProvider/ISigningKeyProvider adapter (docs/operations/keys-and-secrets.md).");
}
