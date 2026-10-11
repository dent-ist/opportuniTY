using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Hosting.Operations;
using Opportunity.Hosting.Workers;
using Opportunity.Storage;

// `jobs …`, `keys …`, `audit …`, `backup …` and `restore …` run the operations CLIs (docs/operations) instead of the worker host.
if (await JobOperationsCli.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

if (await KeyOperationsCli.TryRunAsync(args) is { } keysExitCode)
{
    return keysExitCode;
}

if (await AuditOperationsCli.TryRunAsync(args) is { } auditExitCode)
{
    return auditExitCode;
}

// `backup …` and `restore …` (E19-T08, docs/operations/backup-and-restore.md) also reach object storage when configured.
if (await RestoreOperationsCli.TryRunAsync(args, AddConfiguredObjectStorage) is { } restoreExitCode)
{
    return restoreExitCode;
}

// Thin composition root (ADR-019 R5). Lite runs this with the default (every worker type); the same image runs a
// single type with Workers__Enabled=<type>. Object storage is registered here, not in Opportunity.Hosting, which must
// not reach storage (ADR-015 D12.1).
var builder = OpportunityWorkerHost.CreateBuilder(args, WorkerTypes.AllKeyword);
AddConfiguredObjectStorage(builder.Services, builder.Configuration);

await OpportunityWorkerHost.Build(builder.AddWorkerModules()).RunAsync();
return 0;

static void AddConfiguredObjectStorage(IServiceCollection services, IConfiguration configuration)
{
    var storage = configuration.GetSection(ObjectStorageOptions.SectionName);
    if (storage.Exists())
    {
        var options = new ObjectStorageOptions();
        storage.Bind(options);
        options.Validate();
        services.AddObjectStorage(options);
    }
}
