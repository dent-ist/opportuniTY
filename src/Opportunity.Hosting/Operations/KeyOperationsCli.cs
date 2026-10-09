using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Workspaces;
using Opportunity.Data.Audit;
using Opportunity.Data.Keys;
using Opportunity.Hosting.Health;
using Opportunity.Security.Keys;

namespace Opportunity.Hosting.Operations;

/// <summary>
/// The key management CLI of the worker image (E05-T09; runbook docs/operations/keys-and-secrets.md):
/// <c>keys status|rotate|dedicate|rewrap|rotate-kek|prune-kek|destroy|rotate-signing|public-key</c>, run instead of the
/// worker host when the first argument is <c>keys</c>, e.g.
/// <c>docker compose run --rm --no-deps worker keys rewrap --operator alice</c>. It uses the worker's own configuration
/// (<c>ConnectionStrings__App</c>, <c>KeyManagement__*</c>, <c>Secrets__*</c>, <c>*_FILE</c>) and audits every change as
/// the operator named by <c>--operator</c>. It never prints key material.
/// </summary>
public static class KeyOperationsCli
{
    public const string Verb = "keys";

    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitRefused = 3;

    private const string Usage =
        """
        Usage: keys <command> [options]   (configuration: ConnectionStrings__App, KeyManagement__*, Secrets__*)
          status         [--workspace <id>]                 KEK versions; with --workspace its data keys
          rotate         --workspace <id> --operator <name>  new active data key (same KEK); old objects keep theirs
          dedicate       --workspace <id> --operator <name>  give the workspace its own KEK: new objects use it at once;
                                                            then run `rewrap --workspace` to move its older data keys
          rewrap         [--workspace <id>] --operator <name> the rewrap job: re-wrap data keys with the newest version
                                                            of their workspace's KEK (no data is re-encrypted)
          rotate-kek     --kek <id> --operator <name>        new KEK version (installation, or ws-<workspace id N>);
                                                            then rewrap, then prune-kek
          prune-kek      --kek <id> --operator <name>        destroy KEK versions no data key is wrapped by any more
          destroy        --workspace <id> --operator <name> --confirm <same id>
                                                            crypto-shred: destroy every data key of the workspace and its
                                                            dedicated KEK (irreversible; refused under a legal hold)
          rotate-signing --purpose <name> --operator <name>  new signing key version (e.g. audit-checkpoint)
          public-key     --purpose <name> [--version <n>]    PEM public key of a signing key version
        """;

    /// <summary>Runs the CLI when <paramref name="args"/> starts with <see cref="Verb"/>; null otherwise (start the host).</summary>
    public static async Task<int?> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is not [Verb, ..])
        {
            return null;
        }

        return await RunAsync(args[1..], Console.Out, Console.Error, null, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="args">The command and its options (without the leading <see cref="Verb"/>).</param>
    /// <param name="configuration">The host configuration; null reads the environment (with the <c>*_FILE</c> convention).</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, IConfiguration? configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [var command, .. var rest] || !TryParseOptions(rest, out var options))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        configuration ??= new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(SecretFileConvention.Resolve(Environment.GetEnvironmentVariables()))
            .Build();
        var connectionString = configuration.GetConnectionString(PostgresReadiness.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await error.WriteLineAsync($"ConnectionStrings__{PostgresReadiness.ConnectionStringName} is required.\n{Usage}").ConfigureAwait(false);
            return ExitUsage;
        }

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddPostgresAuditStore();
        services.AddPostgresWorkspaceDataKeys();
        services.AddOpportunityKeyManagement();
        var provider = services.BuildServiceProvider();
        await using (provider.ConfigureAwait(false))
        {
            var session = new Session(provider, options, output, error, cancellationToken);
            try
            {
                return command switch
                {
                    "status" => await session.StatusAsync().ConfigureAwait(false),
                    "rotate" => await session.RotateAsync().ConfigureAwait(false),
                    "dedicate" => await session.DedicateAsync().ConfigureAwait(false),
                    "rewrap" => await session.RewrapAsync().ConfigureAwait(false),
                    "rotate-kek" => await session.RotateKekAsync().ConfigureAwait(false),
                    "prune-kek" => await session.PruneKekAsync().ConfigureAwait(false),
                    "destroy" => await session.DestroyAsync().ConfigureAwait(false),
                    "rotate-signing" => await session.RotateSigningAsync().ConfigureAwait(false),
                    "public-key" => await session.PublicKeyAsync().ConfigureAwait(false),
                    _ => await session.UsageAsync().ConfigureAwait(false),
                };
            }
            catch (PreservationLockedException)
            {
                await error.WriteLineAsync("Refused: the workspace is under a preservation lock (legal hold).").ConfigureAwait(false);
                return ExitRefused;
            }
            catch (KeyUnavailableException ex)
            {
                await error.WriteLineAsync("Key unavailable: " + ex.Message).ConfigureAwait(false);
                return ExitFailed;
            }
        }
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return false;
            }

            options[args[i][2..]] = args[i + 1];
        }

        return true;
    }

    private sealed class Session(
        IServiceProvider services, Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        private WorkspaceKeyService Keys => services.GetRequiredService<WorkspaceKeyService>();

        private IKeyEncryptionKeyProvider Keks => services.GetRequiredService<IKeyEncryptionKeyProvider>();

        public async Task<int> UsageAsync()
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        public async Task<int> StatusAsync()
        {
            var installation = await Keks.DescribeAsync(KeyEncryptionKeyIds.Installation, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"provider\t{Keks.Name}").ConfigureAwait(false);
            await output.WriteLineAsync($"kek {KeyEncryptionKeyIds.Installation}\t{Versions(installation)}").ConfigureAwait(false);
            if (!options.ContainsKey("workspace"))
            {
                return ExitSuccess;
            }

            if (!TryGuid("workspace", out var ws))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var dedicated = await Keks.DescribeAsync(KeyEncryptionKeyIds.ForWorkspace(ws), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"kek {KeyEncryptionKeyIds.ForWorkspace(ws)}\t{Versions(dedicated)}").ConfigureAwait(false);
            await output.WriteLineAsync("KEY_ID\tSTATE\tKEK\tKEK_VERSION\tCREATED\tREWRAPPED\tDESTROYED").ConfigureAwait(false);
            foreach (var row in await Keys.ListAsync(ws, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync(string.Join('\t',
                    WorkspaceDataKey.FormatKeyId(row.Version), row.State, row.KekId, row.KekVersion.ToString(CultureInfo.InvariantCulture),
                    Time(row.CreatedAt), Time(row.RewrappedAt), Time(row.DestroyedAt))).ConfigureAwait(false);
            }

            return ExitSuccess;
        }

        public async Task<int> RotateAsync()
        {
            if (!TryGuid("workspace", out var ws) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var row = await Keys.RotateAsync(ws, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"active {WorkspaceDataKey.FormatKeyId(row.Version)} ({row.KekId} v{row.KekVersion})").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> DedicateAsync()
        {
            if (!TryGuid("workspace", out var ws) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var row = await Keys.UseDedicatedKeyAsync(ws, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"active {WorkspaceDataKey.FormatKeyId(row.Version)} ({row.KekId} v{row.KekVersion}); run `keys rewrap --workspace {ws}` to move older data keys")
                .ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> RewrapAsync()
        {
            Guid? workspace = null;
            if (options.ContainsKey("workspace"))
            {
                if (!TryGuid("workspace", out var ws))
                {
                    return await UsageAsync().ConfigureAwait(false);
                }

                workspace = ws;
            }

            if (Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var result = await Keys.RewrapAsync(workspace, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"workspaces {result.Workspaces}\texamined {result.Examined}\trewrapped {result.Rewrapped}\tfailed {result.Failed.Count}").ConfigureAwait(false);
            foreach (var failed in result.Failed)
            {
                await error.WriteLineAsync($"failed\t{failed}").ConfigureAwait(false);
            }

            return result.Failed.Count == 0 ? ExitSuccess : ExitFailed;
        }

        public async Task<int> RotateKekAsync()
        {
            if (!options.TryGetValue("kek", out var kek) || !KeyEncryptionKeyIds.IsValid(kek) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var info = await Keys.RotateKekAsync(kek, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"kek {kek}\tcurrent v{info.CurrentVersion}; now run `keys rewrap`").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> PruneKekAsync()
        {
            if (!options.TryGetValue("kek", out var kek) || !KeyEncryptionKeyIds.IsValid(kek) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var destroyed = await Keys.PruneKekAsync(kek, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"kek {kek}\tdestroyed versions: {(destroyed.Count == 0 ? "none" : string.Join(',', destroyed))}").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> DestroyAsync()
        {
            if (!TryGuid("workspace", out var ws) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            if (!TryGuid("confirm", out var confirm) || confirm != ws)
            {
                await error.WriteLineAsync("Crypto-shredding is irreversible: repeat the workspace id with --confirm.").ConfigureAwait(false);
                return ExitUsage;
            }

            var result = await Keys.DestroyWorkspaceKeysAsync(ws, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"destroyed {result.DataKeysDestroyed} data key(s); dedicated KEK {(result.DedicatedKekDestroyed ? "destroyed" : "none")}").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> RotateSigningAsync()
        {
            if (!options.TryGetValue("purpose", out var purpose) || !KeyEncryptionKeyIds.IsValid(purpose) || Operator() is null)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var version = await services.GetRequiredService<ISigningKeyProvider>().RotateAsync(purpose, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"signing {purpose}\tcurrent v{version}").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> PublicKeyAsync()
        {
            if (!options.TryGetValue("purpose", out var purpose) || !KeyEncryptionKeyIds.IsValid(purpose))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            int? version = null;
            if (options.TryGetValue("version", out var text))
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 1)
                {
                    return await UsageAsync().ConfigureAwait(false);
                }

                version = v;
            }

            var key = await services.GetRequiredService<ISigningKeyProvider>().GetPublicKeyAsync(purpose, version, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"# {key.KeyId} {key.Algorithm}").ConfigureAwait(false);
            await output.WriteLineAsync(new string(System.Security.Cryptography.PemEncoding.Write("PUBLIC KEY", key.SubjectPublicKeyInfo))).ConfigureAwait(false);
            return ExitSuccess;
        }

        private OperationsActor? Operator() =>
            options.TryGetValue("operator", out var name) && !string.IsNullOrWhiteSpace(name) && name.Length <= 200 ? OperationsActor.Cli(name) : null;

        private bool TryGuid(string name, out Guid value)
        {
            value = default;
            return options.TryGetValue(name, out var text) && Guid.TryParse(text, out value) && value != Guid.Empty;
        }

        private static string Versions(KeyEncryptionKeyInfo? info) =>
            info is null ? "absent" : "versions " + string.Join(',', info.Versions) + " (current v" + info.CurrentVersion.ToString(CultureInfo.InvariantCulture) + ")";

        private static string Time(DateTimeOffset? value) =>
            value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-";
    }
}
