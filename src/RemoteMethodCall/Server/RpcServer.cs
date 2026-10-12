using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using RemoteMethodCall.Internal;

namespace RemoteMethodCall.Server;

/// <summary>
/// TCP server that hosts every class implementing a [RemoteService] interface found in the registered assemblies.
/// </summary>
public sealed class RpcServer : IAsyncDisposable
{
    private readonly ServiceRegistry _registry;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _connections = new();
    private Task? _acceptLoop;

    public RpcServer(int port, Func<Type, object>? instanceFactory = null)
        : this(new IPEndPoint(IPAddress.Any, port), instanceFactory)
    {
    }

    /// <param name="instanceFactory">
    /// Creates service instances (e.g. from a DI container). Defaults to the public parameterless constructor.
    /// </param>
    public RpcServer(IPEndPoint endpoint, Func<Type, object>? instanceFactory = null)
    {
        _listener = new TcpListener(endpoint);
        _registry = new ServiceRegistry(instanceFactory);
    }

    public Action<string>? Log { get; set; }

    /// <summary>Actual bound endpoint; useful when started on port 0.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public IReadOnlyCollection<string> Services => _registry.ServiceNames;

    public RpcServer RegisterAssembly(Assembly assembly)
    {
        int count = _registry.RegisterAssembly(assembly);
        Log?.Invoke($"Registered {count} service(s) from {assembly.GetName().Name}.");
        return this;
    }

    /// <summary>Loads every assembly in the directory (plugins) and registers its services.</summary>
    public RpcServer RegisterAssembliesFromDirectory(string directory, string searchPattern = "*.dll")
    {
        foreach (string path in Directory.EnumerateFiles(directory, searchPattern))
        {
            var name = AssemblyName.GetAssemblyName(path);

            // Reuse an already loaded copy (typically the shared contracts) so interface types stay identical.
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));

            RegisterAssembly(assembly);
        }

        return this;
    }

    public RpcServer Register<TInterface, TImplementation>()
        where TImplementation : class, TInterface
    {
        _registry.Register(typeof(TInterface), typeof(TImplementation));
        return this;
    }

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("Server is already running.");
        }

        _listener.Start();
        Log?.Invoke($"Listening on {LocalEndpoint}.");
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                var connection = HandleConnectionAsync(tcp);
                _connections[tcp] = connection;
                _ = connection.ContinueWith(_ => _connections.TryRemove(tcp, out Task? _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp)
    {
        await Task.Yield();
        var remote = tcp.Client.RemoteEndPoint;
        Log?.Invoke($"Client {remote} connected.");

        using var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new ConcurrentDictionary<long, Task>();
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            while (await MessageFraming.ReadAsync<RpcRequest>(stream, _shutdown.Token).ConfigureAwait(false) is { } request)
            {
                // Requests are processed concurrently so a slow method does not block the connection.
                var processing = ProcessAsync(request);
                inFlight[request.Id] = processing;
                _ = processing.ContinueWith(_ => inFlight.TryRemove(request.Id, out Task? _), TaskScheduler.Default);
            }

            async Task ProcessAsync(RpcRequest request)
            {
                await Task.Yield();
                try
                {
                    var response = await _registry.DispatchAsync(request).ConfigureAwait(false);
                    await writeLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        await MessageFraming.WriteAsync(stream, response, _shutdown.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // Client went away or server is stopping; nobody to answer.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"Client {remote} error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
            tcp.Dispose();
            Log?.Invoke($"Client {remote} disconnected.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        foreach (var tcp in _connections.Keys)
        {
            tcp.Dispose();
        }

        await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        await _registry.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
