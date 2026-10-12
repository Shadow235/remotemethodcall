using System.Net;
using RemoteMethodCall.Client;
using RemoteMethodCall.Server;

namespace RemoteMethodCall.Tests;

public sealed class RemoteCallTests : IAsyncLifetime
{
    private RpcServer _server = null!;
    private RpcClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = new RpcServer(new IPEndPoint(IPAddress.Loopback, 0));
        _server.RegisterAssembly(typeof(RemoteCallTests).Assembly);
        _server.Start();
        _client = await ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private Task<RpcClient> ConnectAsync() => RpcClient.ConnectAsync("127.0.0.1", _server.LocalEndpoint.Port);

    [Fact]
    public void Scanning_registers_only_implemented_remote_interfaces()
    {
        Assert.Equal(
            new[] { "tracked", typeof(ITestService).FullName! }.Order(),
            _server.Services.Order());
    }

    [Fact]
    public async Task Synchronous_and_asynchronous_methods_return_values()
    {
        var proxy = _client.CreateProxy<ITestService>();

        Assert.Equal(5, proxy.Add(2, 3));
        Assert.Equal("ab", proxy.Add("a", "b"));
        proxy.DoNothing();
        Assert.Equal(7, await proxy.AddAsync(3, 4));
        await proxy.DelayAsync(10);
        Assert.Equal("hi", await proxy.EchoValueTaskAsync("hi"));
        await proxy.NoOpValueTaskAsync();
        Assert.Equal("pong", proxy.Ping());
        Assert.Null(proxy.NullableEcho(null));
    }

    [Fact]
    public void Complex_types_round_trip()
    {
        var proxy = _client.CreateProxy<ITestService>();

        var result = proxy.Transform(new Item("book", 10.5m, ["a"]));

        Assert.Equal("book", result.Name);
        Assert.Equal(21m, result.Price);
        Assert.Equal(["a", "x"], result.Tags);
    }

    [Fact]
    public async Task Server_exceptions_are_rethrown_on_client()
    {
        var proxy = _client.CreateProxy<ITestService>();

        var sync = Assert.Throws<RemoteInvocationException>(() => proxy.Fail("boom"));
        Assert.Equal(typeof(InvalidOperationException).FullName, sync.RemoteExceptionType);
        Assert.Equal("boom", sync.Message);

        var async = await Assert.ThrowsAsync<RemoteInvocationException>(() => proxy.FailAsync("bang"));
        Assert.Equal(typeof(ArgumentException).FullName, async.RemoteExceptionType);

        // The connection stays usable after a failure.
        Assert.Equal(2, proxy.Add(1, 1));
    }

    [Fact]
    public async Task PerCall_creates_and_disposes_an_instance_per_call()
    {
        var proxy = _client.CreateProxy<ITrackedService>(InstanceMode.PerCall);
        int disposedBefore = TrackedService.DisposedCount;

        Assert.Equal(1, proxy.Increment());
        Assert.Equal(1, proxy.Increment());
        Assert.NotEqual(proxy.InstanceId(), proxy.InstanceId());

        await Task.Delay(50);
        Assert.True(TrackedService.DisposedCount - disposedBefore >= 4);
    }

    [Fact]
    public async Task Singleton_is_shared_across_proxies_and_clients()
    {
        await using var otherClient = await ConnectAsync();
        var first = _client.CreateProxy<ITrackedService>(InstanceMode.Singleton);
        var second = otherClient.CreateProxy<ITrackedService>(InstanceMode.Singleton);

        Assert.Equal(first.InstanceId(), second.InstanceId());
        int a = first.Increment();
        int b = second.Increment();
        Assert.Equal(a + 1, b);
    }

    [Fact]
    public async Task Concurrent_calls_on_one_connection_are_matched_correctly()
    {
        var proxy = _client.CreateProxy<ITestService>();

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => proxy.AddAsync(i, i)));

        Assert.Equal(Enumerable.Range(0, 200).Select(i => i * 2), results);
    }

    [Fact]
    public void Calling_unregistered_service_fails_with_remote_error()
    {
        var proxy = _client.CreateProxy<INotImplementedService>();

        var ex = Assert.Throws<RemoteInvocationException>(proxy.Anything);
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Proxy_requires_attributed_interface_and_supported_signatures()
    {
        Assert.Throws<InvalidOperationException>(() => _client.CreateProxy<IPlainInterface>());
        Assert.Throws<NotSupportedException>(() => _client.CreateProxy<IUnsupportedService>());
    }

    [Fact]
    public async Task Pending_calls_fail_when_server_stops()
    {
        var proxy = _client.CreateProxy<ITestService>();
        var call = proxy.DelayAsync(1_000);

        await Task.Delay(100);
        await _server.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => call);
        Assert.False(_client.IsConnected);
    }
}
