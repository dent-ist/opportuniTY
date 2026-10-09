using Opportunity.Hosting.Operations;
using Opportunity.Hosting.Workers;

// `jobs …` and `keys …` run the operations CLIs (docs/operations) instead of the worker host.
if (await JobOperationsCli.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

if (await KeyOperationsCli.TryRunAsync(args) is { } keysExitCode)
{
    return keysExitCode;
}

// Thin composition root (ADR-019 R5). Workers__Enabled overrides the worker types this process runs.
await OpportunityWorkerHost.RunAsync(args, WorkerTypes.Dispatcher);
return 0;
