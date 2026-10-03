using Opportunity.Hosting.Workers;

// Thin composition root (ADR-019 R5). Lite runs this with the default (every worker type); the same image runs a
// single type with Workers__Enabled=<type>.
await OpportunityWorkerHost.RunAsync(args, WorkerTypes.AllKeyword);
