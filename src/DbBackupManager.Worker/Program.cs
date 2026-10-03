using DbBackupManager.Worker;

var builder = WorkerHost.CreateBuilder(args);
var host = builder.Build();
await host.RunAsync();
