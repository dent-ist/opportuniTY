var builder = Host.CreateApplicationBuilder(args);

// Indexing worker: handlers are registered here as the corresponding epics land.

var host = builder.Build();
host.Run();