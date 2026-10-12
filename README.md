# RemoteMethodCall

Jednoduchá knihovna pro vzdálené volání metod (RPC) v C# přes TCP/IP.

- Klient má jen **rozhraní** služby a za běhu se mu vygeneruje **proxy objekt** (`DispatchProxy`).
- Server si sám najde všechny třídy, které implementují rozhraní označené atributem `[RemoteService]`.
- Klient si u každé proxy zvolí, jestli server použije **singleton**, nebo **novou instanci pro každé volání**.

Cílová platforma: .NET 10, bez externích závislostí (serializace přes `System.Text.Json`).

## Struktura

| Projekt | Popis |
|---|---|
| `src/RemoteMethodCall` | Knihovna: atribut, klient (`RpcClient`, proxy), server (`RpcServer`) |
| `samples/Sample.Contracts` | Sdílená rozhraní a DTO (potřebuje je klient i server) |
| `samples/Sample.Server` | Ukázkový server s implementacemi služeb |
| `samples/Sample.Client` | Ukázkový klient |
| `tests/RemoteMethodCall.Tests` | Integrační testy (skutečné TCP spojení na loopbacku) |

## Příklad použití

### 1. Kontrakt (sdílená knihovna)

```csharp
using RemoteMethodCall;

public sealed record Person(string Name, int Age);

[RemoteService]                    // název služby = plný název rozhraní
public interface ICalculator
{
    int Add(int a, int b);
    double Add(double a, double b); // přetížení jsou podporována
    Task<double> DivideAsync(double a, double b);
}

[RemoteService]
public interface ICounter
{
    int Increment();
}

[RemoteService("greeter")]         // vlastní název služby
public interface IGreeter
{
    string Greet(Person person);
}
```

### 2. Server

Implementace jsou obyčejné třídy. Server je najde podle rozhraní s atributem `[RemoteService]`:

```csharp
public sealed class Calculator : ICalculator
{
    public int Add(int a, int b) => a + b;
    public double Add(double a, double b) => a + b;

    public async Task<double> DivideAsync(double a, double b)
    {
        await Task.Delay(50);
        if (b == 0) throw new DivideByZeroException("Dělení nulou.");
        return a / b;
    }
}

public sealed class Counter : ICounter, IDisposable
{
    private int _value;
    public int Increment() => Interlocked.Increment(ref _value);
    public void Dispose() { /* v režimu PerCall se volá po každém volání */ }
}
```

```csharp
using RemoteMethodCall.Server;

await using var server = new RpcServer(5000) { Log = Console.WriteLine };

server.RegisterAssembly(typeof(Program).Assembly);   // služby z této assembly
server.RegisterAssembliesFromDirectory("Services");  // volitelně pluginy (*.dll)
// server.Register<ICalculator, Calculator>();        // nebo ruční registrace

server.Start();
```

Instance se standardně vytvářejí bezparametrickým konstruktorem. Pro DI kontejner předejte vlastní továrnu:

```csharp
var server = new RpcServer(5000, type => serviceProvider.GetRequiredService(type));
```

### 3. Klient

```csharp
using RemoteMethodCall;
using RemoteMethodCall.Client;

await using var client = await RpcClient.ConnectAsync("localhost", 5000);
client.Timeout = TimeSpan.FromSeconds(10);           // výchozí 30 s

var calculator = client.CreateProxy<ICalculator>();  // výchozí režim PerCall
int sum = calculator.Add(2, 3);                      // 5
double result = await calculator.DivideAsync(10, 4); // 2.5

// Výjimka ze serveru přijde jako RemoteInvocationException
try
{
    await calculator.DivideAsync(1, 0);
}
catch (RemoteInvocationException ex)
{
    Console.WriteLine($"{ex.RemoteExceptionType}: {ex.Message}");
    // System.DivideByZeroException: Dělení nulou.
}

// Režim instance volí klient
var perCall   = client.CreateProxy<ICounter>(InstanceMode.PerCall);
var singleton = client.CreateProxy<ICounter>(InstanceMode.Singleton);

perCall.Increment();   // 1, 1, 1, ... (nová instance pokaždé)
singleton.Increment(); // 1, 2, 3, ... (jedna sdílená instance)
```

### Spuštění ukázky

```bash
dotnet run --project samples/Sample.Server
```

```bash
dotnet run --project samples/Sample.Client
```

Výstup klienta:

```
Add(2, 3)          = 5
Add(1.5, 2.25)     = 3.75
DivideAsync(10, 4) = 2.5
DivideAsync(1, 0)  -> System.DivideByZeroException: Dělení nulou.
PerCall.Increment() = 1, Singleton.Increment() = 1
PerCall.Increment() = 1, Singleton.Increment() = 2
PerCall.Increment() = 1, Singleton.Increment() = 3
Ahoj Jan, je ti 30 let.
Adults: Eva, Karel
```

## Režimy instance

| Režim | Chování na serveru |
|---|---|
| `InstanceMode.PerCall` (výchozí) | Pro každé volání se vytvoří nová instance a po volání se zavolá `Dispose` / `DisposeAsync` (pokud ho třída implementuje). |
| `InstanceMode.Singleton` | Jedna instance na implementační třídu, sdílená **všemi klienty**. Vytvoří se při prvním použití a uvolní se při vypnutí serveru. Implementace musí být thread-safe. |

## Jak to funguje

- **Protokol:** každá zpráva začíná 4bajtovou délkou (little-endian), za ní následuje JSON s požadavkem nebo odpovědí. Požadavek obsahuje Id, název služby, signaturu metody (např. `Add(System.Int32,System.Int32)`), režim instance a argumenty.
- **Klient:** jedno TCP spojení, které může používat víc proxy objektů najednou. Odpovědi se párují s požadavky podle Id, takže víc souběžných volání přes jedno spojení funguje.
- **Server:** požadavky ze stejného spojení zpracovává paralelně, takže pomalá metoda neblokuje ostatní volání.
- **Podporované návratové typy:** `void`, `T`, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`.

### Omezení

- Nepodporuje parametry `ref` / `out` / `in`, generické metody, `CancellationToken` jako parametr ani polymorfní serializaci. Proxy na rozhraní s takovou metodou se odmítne vytvořit už při `CreateProxy`.
- Synchronní metody na proxy čekají blokujícím voláním. V UI aplikacích proto doporučuji asynchronní signatury (`Task<T>`).
- Spojení není šifrované ani ověřené. Pro provoz mimo důvěryhodnou síť je potřeba přidat `SslStream` a autentizaci.

## Výkon

Měřeno v Release na AMD Ryzen 9 9900X (Windows 11, .NET 10). Server i klient běžely v jednom procesu a komunikovaly přes loopback (127.0.0.1). V reálné síti se k časům přičte latence sítě.

| Scénář | Volání/s | Průměr | p50 | p99 | Alokace/volání* |
|---|---:|---:|---:|---:|---:|
| sync `Add(int,int)`, Singleton | 11 000 | 90 µs | 76 µs | 248 µs | 4,6 kB |
| sync `Add(int,int)`, PerCall | 13 300 | 75 µs | 66 µs | 190 µs | 4,6 kB |
| sync `Echo` (10 objektů) | 9 200 | 109 µs | 100 µs | 247 µs | 17,6 kB |
| sync `Echo` (1000 objektů, ~40 kB JSON) | 700 | 1,4 ms | 1,3 ms | 2,3 ms | 1,3 MB |
| `await AddAsync`, jedno po druhém | 18 300 | 55 µs | 50 µs | 133 µs | 4,6 kB |
| `AddAsync`, 8 souběžně, 1 spojení | 44 300 | – | – | – | 5,2 kB |
| `AddAsync`, 64 souběžně, 1 spojení | 49 000 | – | – | – | 5,2 kB |
| `AddAsync`, 256 souběžně, 1 spojení | 48 400 | – | – | – | 5,2 kB |
| `AddAsync`, 256 souběžně, 8 spojení | 118 000 | – | – | – | 5,2 kB |

\* Součet za klienta i server, protože běžely v jednom procesu.

**Shrnutí:**

- Latence jednoho volání je zhruba **50–90 µs**. Rozdíl mezi Singletonem a PerCall je jen šum měření.
- Jedno spojení se zastaví asi na **49 000 volání/s**. Úzkým hrdlem je jeden zápis a flush na každou zprávu pod zámkem. Víc spojení škáluje téměř lineárně.
- U velkých objektů rozhoduje serializace do JSON.

**Možná zlepšení:** jeden zapisovací task přes `Channel` s dávkovým flushem, odstranění `Task.Yield`, `System.IO.Pipelines` se sdílenými buffery (`ArrayPool`) a případně binární serializace (MessagePack) místo JSON.

## Testy

```bash
dotnet test
```
