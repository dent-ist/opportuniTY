using Opportunity.Testing.Images;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>Image references for the storage contract suite, resolved from <c>versions.env</c>.</summary>
internal static class StorageImages
{
    /// <summary>SeaweedFS, Apache-2.0 (permissive default per Q-38).</summary>
    public static string ObjectStore => ContainerImages.ObjectStore;

    /// <summary>Azurite, MIT.</summary>
    public static string Azurite => ContainerImages.Resolve("AZURITE", "mcr.microsoft.com/azure-storage/azurite");
}
