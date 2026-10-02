var builder = Host.CreateApplicationBuilder(args);

// Dispatcher worker: handlers are registered here as the corresponding epics land.

var host = builder.Build();
host.Run();
