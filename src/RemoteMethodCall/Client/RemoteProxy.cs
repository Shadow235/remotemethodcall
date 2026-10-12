using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using RemoteMethodCall.Internal;

namespace RemoteMethodCall.Client;

/// <summary>
/// Runtime-generated implementation of a [RemoteService] interface that forwards every call to the server.
/// </summary>
public class RemoteProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<(Type, ReturnKind), Func<Task<JsonElement?>, object>> TaskAdapters = new();

    private RpcClient _client = null!;
    private string _serviceName = null!;
    private InstanceMode _mode;

    internal static T Create<T>(RpcClient client, InstanceMode mode)
        where T : class
    {
        string serviceName = RemoteServiceAttribute.GetServiceName(typeof(T));

        // Validate all signatures up front so an unsupported method fails here, not on first call.
        foreach (var method in RemoteMethodDescriptor.GetInterfaceMethods(typeof(T)))
        {
            RemoteMethodDescriptor.Get(method);
        }

        T proxy = Create<T, RemoteProxy>();
        var remoteProxy = (RemoteProxy)(object)proxy;
        remoteProxy._client = client;
        remoteProxy._serviceName = serviceName;
        remoteProxy._mode = mode;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var descriptor = RemoteMethodDescriptor.Get(targetMethod);

        var arguments = new JsonElement[descriptor.ParameterTypes.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = JsonSerializer.SerializeToElement(args![i], descriptor.ParameterTypes[i], RpcJson.Options);
        }

        Task<JsonElement?> call = _client.InvokeAsync(_serviceName, descriptor.Signature, arguments, _mode);

        switch (descriptor.ReturnKind)
        {
            case ReturnKind.Void:
                call.GetAwaiter().GetResult();
                return null;
            case ReturnKind.Value:
                return FromJson(call.GetAwaiter().GetResult(), descriptor.ResultType!);
            case ReturnKind.Task:
                return call;
            case ReturnKind.ValueTask:
                return new ValueTask(call);
            default:
                var adapter = TaskAdapters.GetOrAdd((descriptor.ResultType!, descriptor.ReturnKind), CreateTaskAdapter);
                return adapter(call);
        }
    }

    private static object? FromJson(JsonElement? json, Type type)
    {
        if (json is { } element)
        {
            return element.Deserialize(type, RpcJson.Options);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static Func<Task<JsonElement?>, object> CreateTaskAdapter((Type ResultType, ReturnKind Kind) key)
    {
        string name = key.Kind == ReturnKind.TaskOfT ? nameof(ToTask) : nameof(ToValueTask);
        return typeof(RemoteProxy)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(key.ResultType)
            .CreateDelegate<Func<Task<JsonElement?>, object>>();
    }

    private static object ToTask<T>(Task<JsonElement?> call) => Convert<T>(call);

    private static object ToValueTask<T>(Task<JsonElement?> call) => new ValueTask<T>(Convert<T>(call));

    private static async Task<T> Convert<T>(Task<JsonElement?> call) =>
        (T)FromJson(await call.ConfigureAwait(false), typeof(T))!;
}
