using Opportunity.Hosting.Workers;

// Thin composition root (ADR-019 R5). Workers__Enabled overrides the worker types this process runs.
await OpportunityWorkerHost.RunAsync(args, WorkerTypes.Rendering);
