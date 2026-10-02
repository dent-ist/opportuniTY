namespace Opportunity.IntegrationTests.Storage;

/// <summary>Image references for the storage contract suite, read from <c>versions.env</c>.</summary>
internal static class StorageImages
{
    /// <summary>SeaweedFS, Apache-2.0 (permissive default per Q-38).</summary>
    public static string ObjectStore => Read("OBJECT_STORE_IMAGE");

    /// <summary>Azurite, MIT.</summary>
    public static string Azurite => Read("AZURITE_IMAGE");

    private static string Read(string key)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "versions.env");
            if (File.Exists(file))
            {
                return File.ReadLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith(key + "=", StringComparison.Ordinal))
                    .Select(l => l[(key.Length + 1)..])
                    .Single();
            }
        }

        throw new InvalidOperationException("versions.env not found above " + AppContext.BaseDirectory);
    }
}
