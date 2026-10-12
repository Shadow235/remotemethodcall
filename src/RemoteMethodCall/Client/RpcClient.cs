using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using RemoteMethodCall.Internal;

namespace RemoteMethodCall.Client;

/// <summary>
/// One TCP connection to an <see cref="Server.RpcServer"/>. Thread-safe; any number of proxies
/// can share it and calls may run concurrently (responses are matched by request id).
/// </summary>
public sealed class RpcClient : IAsyncDisposable, IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private long _lastRequestId;
    private volatile Exception? _connectionFailure;

    private RpcClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Maximum time to wait for a response to a single call.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConnected => _connectionFailure is null;

    public static async Task<RpcClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        return new RpcClient(tcp);
    }

    /// <summary>Creates a proxy whose method calls are executed on the server.</summary>
    /// <param name="mode">Whether the server uses a shared singleton or a fresh instance per call.</param>
    public T CreateProxy<T>(InstanceMode mode = InstanceMode.PerCall)
        where T : class =>
        RemoteProxy.Create<T>(this, mode);

    internal async Task<JsonElement?> InvokeAsync(string service, string method, JsonElement[] arguments, InstanceMode mode)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        var request = new RpcRequest
        {
            Id = Interlocked.Increment(ref _lastRequestId),
            Service = service,
            Method = method,
            Mode = mode,
            Arguments = arguments,
        };

        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = completion;
        try
        {
            // The receive loop may have died before we registered; it would never complete us.
            if (_connectionFailure is { } failure)
            {
                throw new IOException("Connection to the RPC server was lost.", failure);
            }

            await _writeLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await MessageFraming.WriteAsync(_stream, request, _shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            RpcResponse response;
            try
            {
                response = await completion.Task.WaitAsync(Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Remote call {service}.{method} did not complete within {Timeout}.");
            }

            if (response.Error is { } error)
            {
                throw new RemoteInvocationException(error.Type, error.Message, error.StackTrace);
            }

            return response.Result;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (await MessageFraming.ReadAsync<RpcResponse>(_stream, _shutdown.Token).ConfigureAwait(false) is { } response)
            {
                if (_pending.TryRemove(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Exception failure = _shutdown.IsCancellationRequested
            ? new ObjectDisposedException(nameof(RpcClient))
            : new IOException("Connection to the RPC server was lost.", error);
        _connectionFailure = failure;

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(failure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _tcp.Dispose();
        await _receiveLoop.ConfigureAwait(false);
        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
