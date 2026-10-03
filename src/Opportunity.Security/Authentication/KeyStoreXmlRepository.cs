using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>
/// Data Protection key ring persisted through <see cref="IDataProtectionKeyStore"/> (PostgreSQL, ADR-015 D10.4). The
/// store is resolved on first use, so building the host (e.g. OpenAPI generation) needs no database.
/// </summary>
internal sealed class KeyStoreXmlRepository(IServiceProvider services) : IXmlRepository
{
    private IDataProtectionKeyStore Store => services.GetRequiredService<IDataProtectionKeyStore>();

    public IReadOnlyCollection<XElement> GetAllElements() => [.. Store.GetAll().Select(XElement.Parse)];

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        Store.Store(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }
}
