using Opportunity.Hosting.Operations;
using Opportunity.Hosting.Workers;
using Opportunity.Storage;

// `jobs …` and `keys …` run the operations CLIs (docs/operations) instead of the worker host.
if (await JobOperationsCli.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

if (await KeyOperationsCli.TryRunAsync(args) is { } keysExitCode)
{
    return keysExitCode;
}

// Thin composition root (ADR-019 R5). Lite runs this with the default (every worker type); the same image runs a
// single type with Workers__Enabled=<type>. Object storage is registered here, not in Opportunity.Hosting, which must
// not reach storage (ADR-015 D12.1).
var builder = OpportunityWorkerHost.CreateBuilder(args, WorkerTypes.AllKeyword);
var storage = builder.Configuration.GetSection(ObjectStorageOptions.SectionName);
if (storage.Exists())
{
    var options = new ObjectStorageOptions();
    storage.Bind(options);
    options.Validate();
    builder.Services.AddObjectStorage(options);
}

await OpportunityWorkerHost.Build(builder.AddWorkerModules()).RunAsync();
return 0;
