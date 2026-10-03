namespace Opportunity.Application.Identity;

/// <summary>
/// Shared storage for the ASP.NET Core Data Protection key ring (ADR-015 D10.4), so every API replica can read
/// session tokens and anti-forgery tokens protected by another. Elements are opaque XML strings.
/// </summary>
public interface IDataProtectionKeyStore
{
    IReadOnlyList<string> GetAll();

    void Store(string friendlyName, string xml);
}
