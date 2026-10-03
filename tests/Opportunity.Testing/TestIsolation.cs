namespace Opportunity.Testing;

/// <summary>Unique, lowercase, identifier-safe names for per-test resources.</summary>
public static class TestIsolation
{
    /// <summary>12 lowercase hex characters; valid in PostgreSQL identifiers, index names, vhosts and bucket names.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary><paramref name="prefix"/> followed by a separator and <see cref="NewId"/>.</summary>
    public static string NewName(string prefix, char separator = '_') => $"{prefix}{separator}{NewId()}";
}
