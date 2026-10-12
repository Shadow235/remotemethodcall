using RemoteMethodCall;
using RemoteMethodCall.Client;
using Sample.Contracts;

string host = args.Length > 0 ? args[0] : "localhost";
int port = args.Length > 1 ? int.Parse(args[1]) : 5000;

await using var client = await RpcClient.ConnectAsync(host, port);

var calculator = client.CreateProxy<ICalculator>();
Console.WriteLine($"Add(2, 3)          = {calculator.Add(2, 3)}");
Console.WriteLine($"Add(1.5, 2.25)     = {calculator.Add(1.5, 2.25)}");
Console.WriteLine($"DivideAsync(10, 4) = {await calculator.DivideAsync(10, 4)}");
try
{
    await calculator.DivideAsync(1, 0);
}
catch (RemoteInvocationException ex)
{
    Console.WriteLine($"DivideAsync(1, 0)  -> {ex.RemoteExceptionType}: {ex.Message}");
}

var perCall = client.CreateProxy<ICounter>(InstanceMode.PerCall);
var singleton = client.CreateProxy<ICounter>(InstanceMode.Singleton);
for (int i = 0; i < 3; i++)
{
    Console.WriteLine($"PerCall.Increment() = {perCall.Increment()}, Singleton.Increment() = {singleton.Increment()}");
}

var greeter = client.CreateProxy<IGreeter>();
Console.WriteLine(greeter.Greet(new Person("Jan", 30)));
var adults = await greeter.GetAdultsAsync([new("Petr", 12), new("Eva", 25), new("Karel", 40)]);
Console.WriteLine($"Adults: {string.Join(", ", adults.Select(p => p.Name))}");
