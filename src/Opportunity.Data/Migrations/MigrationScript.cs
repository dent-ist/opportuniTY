using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Opportunity.Data.Migrations;

/// <summary>
/// One versioned, forward-only SQL migration (<c>V{version}__{description}.sql</c>).
/// </summary>
public sealed partial record MigrationScript
{
    /// <summary>Directive line that makes a script run outside a transaction (e.g. <c>CREATE INDEX CONCURRENTLY</c>).</summary>
    public const string NoTransactionDirective = "-- migrator:no-transaction";

    /// <summary>Separator line between statements of a non-transactional script; each part is sent on its own.</summary>
    public const string StatementBreakDirective = "-- migrator:statement-break";

    private MigrationScript(int version, string description, string name, string sql)
    {
        Version = version;
        Description = description;
        Name = name;
        Sql = sql;
        Checksum = ComputeChecksum(sql);
        IsTransactional = !HasDirective(sql, NoTransactionDirective);
    }

    public int Version { get; }

    public string Description { get; }

    public string Name { get; }

    /// <summary>Script text with line endings normalized to LF and any BOM removed.</summary>
    public string Sql { get; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Sql"/>.</summary>
    public string Checksum { get; }

    public bool IsTransactional { get; }

    /// <summary>Creates a script from its file name (<c>V0001__some_description.sql</c>) and content.</summary>
    public static MigrationScript Create(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            throw new MigrationException(
                $"Migration file name '{fileName}' does not match 'V<version>__<description>.sql'.");
        }

        var version = int.Parse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (version <= 0)
        {
            throw new MigrationException($"Migration '{fileName}' must have a positive version.");
        }

        var description = match.Groups["description"].Value.Replace('_', ' ');
        return new MigrationScript(version, description, fileName, Normalize(content));
    }

    /// <summary>Statements to send one by one for a non-transactional script.</summary>
    public IReadOnlyList<string> SplitStatements()
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var line in Sql.Split('\n'))
        {
            if (line.Trim() == StatementBreakDirective)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }

        parts.Add(current.ToString());
        return parts.Where(p => !string.IsNullOrWhiteSpace(StripComments(p))).ToList();
    }

    private static string Normalize(string content) =>
        content.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static bool HasDirective(string sql, string directive) =>
        sql.Split('\n').Any(line => line.Trim() == directive);

    private static string StripComments(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@"^V(?<version>\d+)__(?<description>[A-Za-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
