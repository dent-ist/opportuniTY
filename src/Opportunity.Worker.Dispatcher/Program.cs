using Opportunity.Hosting.Operations;
using Opportunity.Hosting.Workers;

// `jobs …`, `keys …` and `audit …` run the operations CLIs (docs/operations) instead of the worker host.
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

// `backup …` and `restore …` without object storage (the worker image adds it; the object gates then run there).
if (await RestoreOperationsCli.TryRunAsync(args) is { } restoreExitCode)
{
    return restoreExitCode;
}

// Thin composition root (ADR-019 R5). Workers__Enabled overrides the worker types this process runs.
await OpportunityWorkerHost.RunAsync(args, WorkerTypes.Dispatcher);
return 0;
