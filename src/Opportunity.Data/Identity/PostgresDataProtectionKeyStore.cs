using Npgsql;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

/// <summary><see cref="IDataProtectionKeyStore"/> over <c>opportunity.data_protection_key</c> (V0005).</summary>
/// <remarks>Synchronous because ASP.NET Core's key repository contract is; it is read once per key-ring refresh.</remarks>
public sealed class PostgresDataProtectionKeyStore(NpgsqlDataSource dataSource) : IDataProtectionKeyStore
{
    public IReadOnlyList<string> GetAll()
    {
        using var command = dataSource.CreateCommand("SELECT xml FROM opportunity.data_protection_key ORDER BY created_at");
        using var reader = command.ExecuteReader();
        var elements = new List<string>();
        while (reader.Read())
        {
            elements.Add(reader.GetString(0));
        }

        return elements;
    }

    public void Store(string friendlyName, string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(friendlyName);
        ArgumentException.ThrowIfNullOrEmpty(xml);

        using var command = dataSource.CreateCommand(
            "INSERT INTO opportunity.data_protection_key (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT (friendly_name) DO NOTHING");
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", xml);
        command.ExecuteNonQuery();
    }
}
