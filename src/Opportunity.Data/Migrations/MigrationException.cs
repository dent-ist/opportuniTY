namespace Opportunity.Data.Migrations;

/// <summary>The migrator refused to run or a script failed; the database is left at the last committed version.</summary>
public class MigrationException : Exception
{
    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An already-applied script was edited after it was applied (forward-only rule violated).</summary>
public sealed class MigrationChecksumMismatchException : MigrationException
{
    public MigrationChecksumMismatchException()
    {
    }

    public MigrationChecksumMismatchException(string message)
        : base(message)
    {
    }

    public MigrationChecksumMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
