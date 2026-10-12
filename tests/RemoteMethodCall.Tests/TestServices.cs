namespace RemoteMethodCall.Tests;

public sealed record Item(string Name, decimal Price, List<string> Tags);

public interface IBaseService
{
    string Ping();
}

[RemoteService]
public interface ITestService : IBaseService
{
    int Add(int a, int b);

    string Add(string a, string b);

    void DoNothing();

    Task<int> AddAsync(int a, int b);

    Task DelayAsync(int milliseconds);

    ValueTask<string> EchoValueTaskAsync(string value);

    ValueTask NoOpValueTaskAsync();

    Item Transform(Item item);

    string? NullableEcho(string? value);

    int Fail(string message);

    Task<int> FailAsync(string message);
}

[RemoteService("tracked")]
public interface ITrackedService
{
    int Increment();

    Guid InstanceId();
}

[RemoteService]
public interface INotImplementedService
{
    void Anything();
}

public interface IPlainInterface
{
    void Anything();
}

[RemoteService]
public interface IUnsupportedService
{
    void WithOut(out int value);
}

public sealed class TestService : ITestService
{
    public string Ping() => "pong";

    public int Add(int a, int b) => a + b;

    public string Add(string a, string b) => a + b;

    public void DoNothing()
    {
    }

    public async Task<int> AddAsync(int a, int b)
    {
        await Task.Yield();
        return a + b;
    }

    public Task DelayAsync(int milliseconds) => Task.Delay(milliseconds);

    public async ValueTask<string> EchoValueTaskAsync(string value)
    {
        await Task.Yield();
        return value;
    }

    public ValueTask NoOpValueTaskAsync() => ValueTask.CompletedTask;

    public Item Transform(Item item) => item with { Price = item.Price * 2, Tags = [.. item.Tags, "x"] };

    public string? NullableEcho(string? value) => value;

    public int Fail(string message) => throw new InvalidOperationException(message);

    public async Task<int> FailAsync(string message)
    {
        await Task.Yield();
        throw new ArgumentException(message);
    }
}

public sealed class TrackedService : ITrackedService, IDisposable
{
    private static int _disposed;
    private readonly Guid _id = Guid.NewGuid();
    private int _counter;

    public static int DisposedCount => Volatile.Read(ref _disposed);

    public int Increment() => Interlocked.Increment(ref _counter);

    public Guid InstanceId() => _id;

    public void Dispose() => Interlocked.Increment(ref _disposed);
}
