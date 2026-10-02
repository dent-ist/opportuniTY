using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Opportunity.Hosting.Options;

public static class OptionsRegistration
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> to <paramref name="sectionPath"/>, validates data annotations and fails host
    /// start-up (not first use) when the configuration is missing or invalid.
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(this IServiceCollection services, string sectionPath)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        return services.AddOptions<TOptions>()
            .BindConfiguration(sectionPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
