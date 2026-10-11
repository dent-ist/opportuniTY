using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Backup;
using Opportunity.Application.Keys;
using Opportunity.Data.Audit;
using Opportunity.Data.Backup;
using Opportunity.Data.Keys;
using Opportunity.Data.Search;
using Opportunity.Hosting.Health;
using Opportunity.Jobs.Backup;
using Opportunity.Search;
using Opportunity.Security.Keys;

namespace Opportunity.Hosting.Operations;

/// <summary>
/// The backup and restore CLI of the worker image (E19-T08; ADR-016, docs/architecture/restore-consistency.md, runbook
/// docs/operations/backup-and-restore.md): <c>backup search-snapshot|search-snapshots|prune</c> and <c>restore plan|
/// search-snapshot|reconcile|record|verify|object</c>, run instead of the worker host when the first argument is
/// <c>backup</c> or <c>restore</c>. It connects with the runtime login (<c>ConnectionStrings__App</c>, RLS applies, one
/// workspace transaction at a time), reads and seals the audit chains with the sealer login
/// (<c>ConnectionStrings__AuditSealer</c>), and reaches OpenSearch (<c>ConnectionStrings__OpenSearch</c>) and, when the
/// host registers it, object storage.
/// </summary>
public static class RestoreOperationsCli
{
    public const string BackupVerb = "backup";
    public const string RestoreVerb = "restore";

    public const int ExitSuccess = 0;

    /// <summary>A gate failed or the plan was refused.</summary>
    public const int ExitRefused = 1;

    public const int ExitUsage = 2;
    public const int ExitFailed = 3;

    private const string Usage =
        """
        Usage: backup <command> | restore <command>   (configuration: ConnectionStrings__App, ConnectionStrings__AuditSealer,
               ConnectionStrings__OpenSearch, OpenSearch__*, Backup__SearchSnapshots__*, KeyManagement__*, ObjectStorage__*)
          backup search-snapshot                 take an OpenSearch snapshot now: workspace anchors first, then the snapshot
          backup search-snapshots                the repository's snapshots with their anchor records
          backup prune                           delete snapshots outside retention (and their anchors)
          restore plan --pgbackrest-info <info.json> [--target-time <iso> | --end-of-wal] [--decided-at <iso>]
                       [--snapshot <name>] [--keep-live-search] [--object-sync-point <iso>] [--key-backup-at <iso>]
                       [--stanza opportunity]
                       R1: checks the recovery target against the archive, snapshots, object replica and key backups;
                       prints the path, the snapshot and the loss window. Exit 1 when the combination is refused.
          restore search-snapshot --snapshot <name>
                       R4: deletes the installation's indices and restores the snapshot (with aliases, no global state)
          restore reconcile --restore-id <id> --path end-of-wal|snapshot|rebuild [--snapshot <name>]
                       [--target-time <iso>] [--backup-label <label>] --operator <name>
                       R6: catch-up above the anchor, redrive, security reset, deletion re-fence, lost-window report.
                       Every writer must be stopped. Re-runnable until `restore record`.
          restore record --restore-id <id>
                       R6d: Integrity.Restored in every audit chain, AfterRestore checkpoints, signed sys/restores record
          restore verify --restore-id <id> [--converge] [--full | --sample <n>] [--acknowledge-lost-window]
                       [--recover-missing-objects] [--public-key <pem>[,<pem>...]]
                       the pre-open gates (exit 0 only when all pass; then start migrator, dispatcher, workers, API);
                       --converge after reopening: watermark caught up and 0 ahead / 0 stale / 0 missing
          restore object --key <logical key> [--version <id>] [--list]
                       recover one object from its bucket version (or list its versions)
        """;

    /// <summary>Runs the CLI when <paramref name="args"/> starts with a verb; null otherwise (start the host).</summary>
    /// <param name="configureServices">Adds what the host composes outside Opportunity.Hosting (object storage).</param>
    public static async Task<int?> TryRunAsync(
        string[] args, Action<IServiceCollection, IConfiguration>? configureServices = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is not [BackupVerb or RestoreVerb, ..])
        {
            return null;
        }

        return await RunAsync(args, Console.Out, Console.Error, null, configureServices, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="args">The verb, the command and its options.</param>
    /// <param name="configuration">The host configuration; null reads the environment (with the <c>*_FILE</c> convention).</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, IConfiguration? configuration,
        Action<IServiceCollection, IConfiguration>? configureServices = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [var verb and (BackupVerb or RestoreVerb), var command, .. var rest] || !TryParseOptions(rest, out var options))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        configuration ??= new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(SecretFileConvention.Resolve(Environment.GetEnvironmentVariables()))
            .Build();
        var appConnection = configuration.GetConnectionString(PostgresReadiness.ConnectionStringName);
        var sealerConnection = configuration.GetConnectionString(AuditChainDataSource.ConnectionStringName);
        var openSearch = OpenSearchOptions.Bind(configuration);
        if (string.IsNullOrWhiteSpace(appConnection) || openSearch.Endpoint is null
            || (verb == RestoreVerb && command is "record" or "verify" && string.IsNullOrWhiteSpace(sealerConnection)))
        {
            await error.WriteLineAsync(
                $"ConnectionStrings__{PostgresReadiness.ConnectionStringName}, ConnectionStrings__OpenSearch and (for record and verify) ConnectionStrings__{AuditChainDataSource.ConnectionStringName} are required.\n{Usage}")
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var snapshotOptions = BindSnapshotOptions(configuration);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton(_ => NpgsqlDataSource.Create(appConnection));
        services.AddPostgresAuditStore();
        services.AddPostgresWorkspaceDataKeys();
        services.AddOpportunityKeyManagement();
        services.AddAuditChain(_ => NpgsqlDataSource.Create(
            string.IsNullOrWhiteSpace(sealerConnection) ? appConnection : sealerConnection));
        services.AddPostgresIndexPlacementStore();
        services.AddPostgresReindexStore();
        services.AddPostgresBackupStores();
        services.AddOpenSearchBackup(openSearch, snapshotOptions);
        services.AddSearchSnapshotJobCore(snapshotOptions);
        services.AddRestoreTooling();
        configureServices?.Invoke(services, configuration);
        var provider = services.BuildServiceProvider();
        await using (provider.ConfigureAwait(false))
        {
            var session = new RestoreCliSession(provider, options, output, error, cancellationToken);
            try
            {
                return (verb, command) switch
                {
                    (BackupVerb, "search-snapshot") => await session.SnapshotAsync().ConfigureAwait(false),
                    (BackupVerb, "search-snapshots") => await session.SnapshotsAsync().ConfigureAwait(false),
                    (BackupVerb, "prune") => await session.PruneAsync().ConfigureAwait(false),
                    (RestoreVerb, "plan") => await session.PlanAsync().ConfigureAwait(false),
                    (RestoreVerb, "search-snapshot") => await session.RestoreSnapshotAsync().ConfigureAwait(false),
                    (RestoreVerb, "reconcile") => await session.ReconcileAsync().ConfigureAwait(false),
                    (RestoreVerb, "record") => await session.RecordAsync().ConfigureAwait(false),
                    (RestoreVerb, "verify") => await session.VerifyAsync().ConfigureAwait(false),
                    (RestoreVerb, "object") => await session.ObjectAsync().ConfigureAwait(false),
                    _ => await session.UsageAsync().ConfigureAwait(false),
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyUnavailableException or AuditChainBrokenException)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitFailed;
            }
        }
    }

    /// <summary>Binds <c>Backup:SearchSnapshots</c> (the dispatcher host and the CLI share it).</summary>
    public static SearchSnapshotOptions BindSnapshotOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SearchSnapshotOptions();
        configuration.GetSection(SearchSnapshotOptions.SectionName).Bind(options);
        return options;
    }

    internal static string UsageText => Usage;

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            if (args[i] is "--end-of-wal" or "--keep-live-search" or "--converge" or "--full" or "--acknowledge-lost-window"
                or "--recover-missing-objects" or "--list")
            {
                options[args[i][2..]] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                return false;
            }

            options[args[i][2..]] = args[i + 1];
            i++;
        }

        return true;
    }

    internal static string Time(DateTimeOffset? value) =>
        value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-";
}
