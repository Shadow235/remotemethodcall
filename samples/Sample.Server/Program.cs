using RemoteMethodCall.Server;

int port = args.Length > 0 ? int.Parse(args[0]) : 5000;

await using var server = new RpcServer(port) { Log = Console.WriteLine };

// Scan this assembly; services can also live in plugin DLLs dropped into ./Services.
server.RegisterAssembly(typeof(Program).Assembly);
string pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Services");
if (Directory.Exists(pluginDirectory))
{
    server.RegisterAssembliesFromDirectory(pluginDirectory);
}

Console.WriteLine($"Services: {string.Join(", ", server.Services)}");
server.Start();

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
Console.WriteLine("Press Ctrl+C to stop.");
await stop.Task;
