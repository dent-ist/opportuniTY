using Opportunity.Hosting.Workers;
using Opportunity.Storage;

// Thin composition root (ADR-019 R5). Workers__Enabled overrides the worker types this process runs. Object storage is
// registered here, not in Opportunity.Hosting, which must not reach storage (ADR-015 D12.1).
var builder = OpportunityWorkerHost.CreateBuilder(args, WorkerTypes.Export);
var storage = builder.Configuration.GetSection(ObjectStorageOptions.SectionName);
if (storage.Exists())
{
    var options = new ObjectStorageOptions();
    storage.Bind(options);
    options.Validate();
    builder.Services.AddObjectStorage(options);
}

await OpportunityWorkerHost.Build(builder.AddWorkerModules()).RunAsync();
