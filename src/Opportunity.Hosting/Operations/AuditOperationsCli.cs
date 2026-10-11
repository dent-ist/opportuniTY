using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Data.Audit;
using Opportunity.Hosting.Health;
using Opportunity.Security.Keys;

namespace Opportunity.Hosting.Operations;

/// <summary>
/// The audit chain CLI of the worker image (E14-T03; runbook docs/operations/audit-chain.md):
/// <c>audit verify|checkpoints|seal</c>, run instead of the worker host when the first argument is <c>audit</c>, e.g.
/// <c>docker compose run --rm --no-deps worker audit verify --public-key /keys/audit-checkpoint-v1.pem</c>. It reads the
/// chains with the sealer login (<c>ConnectionStrings__AuditSealer</c>, which sees every chain) and records what it did
/// with the runtime login (<c>ConnectionStrings__App</c>): <c>Audit.Verified</c> per chain, <c>Integrity.ChainBroken</c>
/// when a chain fails, <c>Audit.CheckpointCreated</c> for requested checkpoints.
/// </summary>
public static class AuditOperationsCli
{
    public const string Verb = "audit";

    public const int ExitSuccess = 0;

    /// <summary>Verification found tampering (or a checkpoint that cannot be verified).</summary>
    public const int ExitBroken = 1;

    public const int ExitUsage = 2;

    public const int ExitFailed = 3;

    private const string Usage =
        """
        Usage: audit <command> [options]   (configuration: ConnectionStrings__AuditSealer, ConnectionStrings__App, KeyManagement__*)
          verify       [--workspace <id> | --system] [--public-key <pem>[,<pem>...]] --operator <name>
                       recompute every chain (all chains by default): modified, deleted or reordered events, purge
                       records, checkpoint hashes, Merkle roots and ES256 signatures. With --public-key the signatures
                       are checked with exported keys (`keys public-key --purpose audit-checkpoint`), otherwise with
                       the installation's key provider. Exit 1 when anything does not verify.
          checkpoints  (--workspace <id> | --system) [--format csv|json]
                       the chain's signed checkpoints with their signed payloads, for declaration exhibits
          seal         [--workspace <id> | --system] [--checkpoint] [--reason manual|before-purge|before-deletion|matter-closed|after-restore]
                       --operator <name>
                       seal now (what the dispatcher does every second); with --checkpoint also take signed checkpoints
                       (required before `audit.drop_expired_partition`)
          verify-export --file <export.jsonl> [--public-key <pem>[,<pem>...]]
                       verify an audit export downloaded from the API offline (no database or configuration needed):
                       event hashes, chain links from the signed anchor, checkpoint heads, Merkle roots and ES256
                       signatures. Without --public-key the keys embedded in the file are used (not independent proof).
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

        if (command == "verify-export")
        {
            return await VerifyExportAsync(options, output, error, cancellationToken).ConfigureAwait(false);
        }

        configuration ??= new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(SecretFileConvention.Resolve(Environment.GetEnvironmentVariables()))
            .Build();
        var sealerConnection = configuration.GetConnectionString(AuditChainDataSource.ConnectionStringName);
        var appConnection = configuration.GetConnectionString(PostgresReadiness.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(sealerConnection) || string.IsNullOrWhiteSpace(appConnection))
        {
            await error.WriteLineAsync(
                $"ConnectionStrings__{AuditChainDataSource.ConnectionStringName} (sealer login) and ConnectionStrings__{PostgresReadiness.ConnectionStringName} are required.\n{Usage}")
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(_ => NpgsqlDataSource.Create(appConnection));
        services.AddPostgresAuditStore();
        services.AddOpportunityKeyManagement();
        services.AddAuditChain(_ => NpgsqlDataSource.Create(sealerConnection));
        var provider = services.BuildServiceProvider();
        await using (provider.ConfigureAwait(false))
        {
            var session = new Session(provider, options, output, error, cancellationToken);
            try
            {
                return command switch
                {
                    "verify" => await session.VerifyAsync().ConfigureAwait(false),
                    "checkpoints" => await session.CheckpointsAsync().ConfigureAwait(false),
                    "seal" => await session.SealAsync().ConfigureAwait(false),
                    _ => await session.UsageAsync().ConfigureAwait(false),
                };
            }
            catch (AuditChainBrokenException ex)
            {
                await error.WriteLineAsync(ex.Message + " Nothing was signed; run `audit verify`.").ConfigureAwait(false);
                return ExitBroken;
            }
            catch (KeyUnavailableException ex)
            {
                await error.WriteLineAsync("Key unavailable: " + ex.Message).ConfigureAwait(false);
                return ExitFailed;
            }
        }
    }

    /// <summary><c>audit verify-export</c>: verifies an export file (E14-T06) without the database; records nothing.</summary>
    private static async Task<int> VerifyExportAsync(
        Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("file", out var file) || options.Keys.Any(k => k is not ("file" or "public-key")))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        if (!File.Exists(file))
        {
            await error.WriteLineAsync($"Export file not found: {file}").ConfigureAwait(false);
            return ExitUsage;
        }

        IAuditCheckpointKeys? keys = null;
        if (options.TryGetValue("public-key", out var files))
        {
            var (loaded, problem) = await LoadKeysAsync(files, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                await error.WriteLineAsync(problem).ConfigureAwait(false);
                return ExitUsage;
            }

            keys = loaded;
        }

        AuditExportVerification result;
        try
        {
            using var reader = new StreamReader(file, System.Text.Encoding.UTF8);
            result = await AuditExportVerifier.VerifyAsync(reader, keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
                                       or ArgumentException or OverflowException)
        {
            await output.WriteLineAsync($"MALFORMED	{ex.Message}").ConfigureAwait(false);
            return ExitBroken;
        }

        var m = result.Manifest;
        var chain = result.Chain;
        await output.WriteLineAsync("WORKSPACE	RESULT	EVENTS	FROM_SEQUENCE	TO_SEQUENCE	LAST_HASH	CHECKPOINTS	PURGED	WITHHELD").ConfigureAwait(false);
        await output.WriteLineAsync(string.Join('	',
            m.WorkspaceId.ToString("D"), chain.Intact ? "INTACT" : "BROKEN", N(chain.Events), N(m.FromSequence), N(m.ToSequence),
            chain.LastEventHash is null ? "-" : AuditChainFormat.Hex(chain.LastEventHash), chain.Checkpoints.ToString(CultureInfo.InvariantCulture),
            N(chain.PurgedEvents), N(result.WithheldEvents))).ConfigureAwait(false);
        foreach (var issue in chain.Issues)
        {
            await output.WriteLineAsync($"  {issue.Kind}	{(issue.Sequence is { } s ? N(s) : "-")}	{issue.Message}").ConfigureAwait(false);
        }

        if (chain.IssueCount > chain.Issues.Count)
        {
            await output.WriteLineAsync($"  ... {N(chain.IssueCount - chain.Issues.Count)} more issue(s)").ConfigureAwait(false);
        }

        if (result.UsedEmbeddedKeys)
        {
            await output.WriteLineAsync(
                "  note	signatures were checked with the public keys embedded in the export; pass --public-key with keys obtained independently "
                + "(`keys public-key --purpose audit-checkpoint`) for proof that does not rely on the file").ConfigureAwait(false);
        }

        if (chain.EventsAfterLastCheckpoint > 0)
        {
            await output.WriteLineAsync(
                $"  note	{N(chain.EventsAfterLastCheckpoint)} event(s) after the last checkpoint in the file are linked but not yet signed").ConfigureAwait(false);
        }

        if (result.WithheldEvents > 0)
        {
            await output.WriteLineAsync(
                $"  note	{N(result.WithheldEvents)} event(s) carry withheld search text: checked through the chain links and Merkle roots only").ConfigureAwait(false);
        }

        if (chain.PurgedEvents > 0)
        {
            await output.WriteLineAsync(
                $"  note	{N(chain.PurgedEvents)} position(s) were removed by the retention purge; the purge records are accepted as exported "
                + "(`audit verify` checks them against the signed Audit.Purged events)").ConfigureAwait(false);
        }

        if (m.UnsealedEvents > 0)
        {
            await output.WriteLineAsync($"  note	{N(m.UnsealedEvents)} event(s) of the period were not sealed yet when exported and are not in the file")
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"  note	the server reported {(m.Verification.Intact ? "INTACT" : "BROKEN")} when it generated the export at "
            + $"{m.GeneratedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)} (audit event {m.AuditEventId?.ToString() ?? "-"})").ConfigureAwait(false);
        return chain.Intact ? ExitSuccess : ExitBroken;
    }

    private static async Task<(IAuditCheckpointKeys? Keys, string Problem)> LoadKeysAsync(string files, CancellationToken cancellationToken)
    {
        var texts = new List<string>();
        foreach (var file in files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(file))
            {
                return (null, $"Public key file not found: {file}");
            }

            texts.Add(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            return (new PemCheckpointKeys(texts), string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return (null, "Not an ES256 public key PEM: " + ex.Message);
        }
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            // Flags without a value: --system, --checkpoint.
            if (args[i] is "--system" or "--checkpoint")
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

    private sealed class Session(
        IServiceProvider services, Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        private PostgresAuditChainVerifier Verifier => services.GetRequiredService<PostgresAuditChainVerifier>();

        public async Task<int> UsageAsync()
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        public async Task<int> VerifyAsync()
        {
            if (Operator() is not { } actor || !TryChain(out var chain))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            IAuditCheckpointKeys keys;
            if (options.TryGetValue("public-key", out var files))
            {
                var (loaded, problem) = await LoadKeysAsync(files, cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    await error.WriteLineAsync(problem).ConfigureAwait(false);
                    return ExitUsage;
                }

                keys = loaded;
            }
            else
            {
                keys = new SigningProviderCheckpointKeys(services.GetRequiredService<ISigningKeyProvider>());
            }

            var chains = chain is { } one ? [one] : await Verifier.ListChainsAsync(cancellationToken).ConfigureAwait(false);
            var broken = 0;
            await output.WriteLineAsync("CHAIN\tRESULT\tEVENTS\tLAST_SEQUENCE\tLAST_HASH\tCHECKPOINTS\tCHECKPOINTED_THROUGH\tPURGED\tUNSEALED").ConfigureAwait(false);
            foreach (var id in chains)
            {
                var report = await Verifier.VerifyAsync(id, keys, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(string.Join('\t',
                    AuditChainFormat.DescribeChain(id), report.Intact ? "INTACT" : "BROKEN", N(report.Events), N(report.LastSequence),
                    report.LastEventHash is null ? "-" : AuditChainFormat.Hex(report.LastEventHash), report.Checkpoints.ToString(CultureInfo.InvariantCulture),
                    N(report.LastCheckpointSequence), N(report.PurgedEvents), N(report.UnsealedEvents))).ConfigureAwait(false);
                foreach (var issue in report.Issues)
                {
                    await output.WriteLineAsync($"  {issue.Kind}\t{(issue.Sequence is { } s ? N(s) : "-")}\t{issue.Message}").ConfigureAwait(false);
                }

                if (report.IssueCount > report.Issues.Count)
                {
                    await output.WriteLineAsync($"  ... {N(report.IssueCount - report.Issues.Count)} more issue(s)").ConfigureAwait(false);
                }

                if (report.EventsAfterLastCheckpoint > 0)
                {
                    await output.WriteLineAsync(
                        $"  note\t{N(report.EventsAfterLastCheckpoint)} event(s) after the last checkpoint: deleting only these would not yet be provable").ConfigureAwait(false);
                }

                if (report.EventsRecordedBeforeSealing > 0)
                {
                    await output.WriteLineAsync(
                        $"  note\t{N(report.EventsRecordedBeforeSealing)} event(s) were recorded before sealing began ({AuditChainFormat.Timestamp(report.FirstSealedAt!.Value)}); "
                        + "they are only as trustworthy as the storage controls of that period (ADR-013 §3.3)").ConfigureAwait(false);
                }

                if (!report.Intact)
                {
                    broken++;
                }

                await RecordAsync(id, report, actor).ConfigureAwait(false);
            }

            await output.WriteLineAsync($"verified {chains.Count} chain(s): {chains.Count - broken} intact, {broken} broken").ConfigureAwait(false);
            return broken == 0 ? ExitSuccess : ExitBroken;
        }

        public async Task<int> CheckpointsAsync()
        {
            if (!TryChain(out var chain) || chain is not { } id)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var json = options.TryGetValue("format", out var format) && format == "json";
            if (format is not (null or "csv" or "json"))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            if (!json)
            {
                await output.WriteLineAsync("chain,from_sequence,sequence,event_count,event_hash,merkle_root,reason,created_at,key_id,algorithm,signature_base64,signed_payload_base64")
                    .ConfigureAwait(false);
            }

            await foreach (var checkpoint in Verifier.CheckpointsAsync(id, cancellationToken).ConfigureAwait(false))
            {
                var payload = AuditChainFormat.CheckpointPayload(checkpoint.Data);
                if (json)
                {
                    // One JSON object per line: the signed payload itself plus the signature.
                    await output.WriteLineAsync(
                        $"{{\"payload\":{System.Text.Encoding.UTF8.GetString(payload)},\"keyId\":\"{checkpoint.KeyId}\",\"algorithm\":\"{checkpoint.Algorithm}\",\"signature\":\"{Convert.ToBase64String(checkpoint.Signature)}\"}}")
                        .ConfigureAwait(false);
                }
                else
                {
                    var d = checkpoint.Data;
                    await output.WriteLineAsync(string.Join(',',
                        AuditChainFormat.DescribeChain(d.ChainId), N(d.FromSequence), N(d.Sequence), N(d.EventCount), AuditChainFormat.Hex(d.EventHash),
                        AuditChainFormat.Hex(d.MerkleRoot), d.Reason, AuditChainFormat.Timestamp(d.CreatedAt), checkpoint.KeyId, checkpoint.Algorithm,
                        Convert.ToBase64String(checkpoint.Signature), Convert.ToBase64String(payload))).ConfigureAwait(false);
                }
            }

            return ExitSuccess;
        }

        public async Task<int> SealAsync()
        {
            if (Operator() is null || !TryChain(out var chain) || !TryReason(out var reason))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var sealer = services.GetRequiredService<IAuditChainSealer>();
            var result = await sealer.SealAsync(chain, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"sealed {N(result.Sealed)} event(s) in {result.Chains} chain(s); {result.Busy} busy, {result.Failed} failed")
                .ConfigureAwait(false);
            if (!options.ContainsKey("checkpoint"))
            {
                return ExitSuccess;
            }

            foreach (var checkpoint in await sealer.CheckpointAsync(reason, chain, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync(
                    $"checkpoint\t{AuditChainFormat.DescribeChain(checkpoint.ChainId)}\t{N(checkpoint.Sequence)}\t{AuditChainFormat.Hex(checkpoint.Data.EventHash)}\t{checkpoint.KeyId}")
                    .ConfigureAwait(false);
            }

            return ExitSuccess;
        }

        private async Task RecordAsync(Guid chain, AuditChainVerification report, OperationsActor actor)
        {
            var writer = services.GetRequiredService<IAuditEventWriter>();
            var workspace = AuditChainFormat.WorkspaceIdOf(chain);
            var details = new Dictionary<string, string?>
            {
                ["Events"] = N(report.Events),
                ["LastSequence"] = N(report.LastSequence),
                ["LastEventHash"] = report.LastEventHash is null ? null : AuditChainFormat.Hex(report.LastEventHash),
                ["Checkpoints"] = report.Checkpoints.ToString(CultureInfo.InvariantCulture),
                ["CheckpointedThrough"] = N(report.LastCheckpointSequence),
                ["Purged"] = N(report.PurgedEvents),
                ["Issues"] = N(report.IssueCount),
            };
            var template = new AuditEvent
            {
                WorkspaceId = workspace,
                OccurredAt = DateTimeOffset.UtcNow,
                Category = AuditTaxonomy.Audit.Category,
                Action = AuditTaxonomy.Audit.Verified,
                ActorType = AuditActorType.Service,
                ActorId = OperationsActor.CliServiceId,
                ActorDisplay = Display(actor),
                ResourceType = workspace is null ? "Installation" : "Workspace",
                ResourceId = workspace?.ToString(),
                Outcome = report.Intact ? AuditOutcome.Success : AuditOutcome.Failure,
                ReasonCode = report.Intact ? null : AuditTaxonomy.Integrity.ChainBroken,
                Details = details,
            };
            if (!report.Intact)
            {
                var first = report.Issues[0];
                await writer.WriteAsync(template with
                {
                    EventId = Guid.CreateVersion7(),
                    Category = AuditTaxonomy.Integrity.Category,
                    Action = AuditTaxonomy.Integrity.ChainBroken,
                    ReasonCode = first.Kind.ToString(),
                    Details = new Dictionary<string, string?>(details)
                    {
                        ["FirstIssue"] = first.Kind.ToString(),
                        ["FirstIssueSequence"] = first.Sequence is { } s ? N(s) : null,
                    },
                }, cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteAsync(template, cancellationToken).ConfigureAwait(false);
        }

        private OperationsActor? Operator() =>
            options.TryGetValue("operator", out var name) && !string.IsNullOrWhiteSpace(name) && name.Length <= 200 ? OperationsActor.Cli(name) : null;

        private static string Display(OperationsActor actor)
        {
            var display = $"Operations CLI ({actor.OperatorName})";
            return display.Length <= AuditEventRules.MaxActorDisplayLength ? display : display[..AuditEventRules.MaxActorDisplayLength];
        }

        /// <summary>--workspace, --system, or neither (all chains).</summary>
        private bool TryChain(out Guid? chain)
        {
            chain = null;
            var system = options.ContainsKey("system");
            if (options.TryGetValue("workspace", out var text))
            {
                if (system || !Guid.TryParse(text, out var ws) || ws == Guid.Empty)
                {
                    return false;
                }

                chain = ws;
            }
            else if (system)
            {
                chain = AuditChainFormat.SystemChainId;
            }

            return true;
        }

        private bool TryReason(out AuditCheckpointReason reason)
        {
            reason = AuditCheckpointReason.Manual;
            if (!options.TryGetValue("reason", out var text))
            {
                return true;
            }

            switch (text)
            {
                case "manual":
                    return true;
                case "before-purge":
                    reason = AuditCheckpointReason.BeforePurge;
                    return true;
                case "before-deletion":
                    reason = AuditCheckpointReason.BeforeDeletion;
                    return true;
                case "matter-closed":
                    reason = AuditCheckpointReason.MatterClosed;
                    return true;
                case "after-restore":
                    reason = AuditCheckpointReason.AfterRestore;
                    return true;
                default:
                    return false;
            }
        }
    }
}
