using Opportunity.Hosting.Operations;
using Opportunity.Hosting.Workers;

// `jobs …` runs the operations CLI (docs/operations) instead of the worker host.
if (await JobOperationsCli.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

// Thin composition root (ADR-019 R5). Workers__Enabled overrides the worker types this process runs.
await OpportunityWorkerHost.RunAsync(args, WorkerTypes.Dispatcher);
return 0;
