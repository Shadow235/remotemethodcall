using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using RemoteMethodCall.Internal;

namespace RemoteMethodCall.Server;

/// <summary>
/// Maps service names to implementation types, owns singleton instances and executes requests.
/// </summary>
internal sealed class ServiceRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceEntry> _services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, Lazy<object>> _singletons = new();
    private readonly Func<Type, object> _instanceFactory;

    public ServiceRegistry(Func<Type, object>? instanceFactory)
    {
        _instanceFactory = instanceFactory ?? DefaultFactory;
    }

    public IReadOnlyCollection<string> ServiceNames => _services.Keys.ToArray();

    /// <summary>Registers every concrete class in the assembly that implements a [RemoteService] interface.</summary>
    public int RegisterAssembly(Assembly assembly)
    {
        int count = 0;
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (var interfaceType in type.GetInterfaces().Where(RemoteServiceAttribute.IsRemoteService))
            {
                if (Register(interfaceType, type))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <returns>False when this exact mapping was already registered.</returns>
    public bool Register(Type interfaceType, Type implementationType)
    {
        string name = RemoteServiceAttribute.GetServiceName(interfaceType);
        if (!interfaceType.IsAssignableFrom(implementationType) || implementationType.IsAbstract)
        {
            throw new ArgumentException($"'{implementationType}' is not a concrete implementation of '{interfaceType}'.");
        }

        var methods = RemoteMethodDescriptor.GetInterfaceMethods(interfaceType)
            .Select(RemoteMethodDescriptor.Get)
            .ToDictionary(d => d.Signature, d => new ServiceMethod(d), StringComparer.Ordinal);

        var entry = new ServiceEntry(interfaceType, implementationType, methods);
        var existing = _services.GetOrAdd(name, entry);
        if (ReferenceEquals(existing, entry))
        {
            return true;
        }

        if (existing.ImplementationType == implementationType)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Service '{name}' is implemented by both '{existing.ImplementationType}' and '{implementationType}'.");
    }

    public async Task<RpcResponse> DispatchAsync(RpcRequest request)
    {
        try
        {
            if (!_services.TryGetValue(request.Service, out var service))
            {
                throw new InvalidOperationException($"Service '{request.Service}' is not registered on the server.");
            }

            if (!service.Methods.TryGetValue(request.Method, out var method))
            {
                throw new MissingMethodException($"Service '{request.Service}' has no method '{request.Method}'.");
            }

            var descriptor = method.Descriptor;
            if (request.Arguments.Length != descriptor.ParameterTypes.Length)
            {
                throw new ArgumentException(
                    $"Method '{request.Method}' expects {descriptor.ParameterTypes.Length} arguments, got {request.Arguments.Length}.");
            }

            var arguments = new object?[request.Arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                arguments[i] = request.Arguments[i].Deserialize(descriptor.ParameterTypes[i], RpcJson.Options);
            }

            bool perCall = request.Mode == InstanceMode.PerCall;
            object instance = perCall
                ? _instanceFactory(service.ImplementationType)
                : GetSingleton(service.ImplementationType);
            try
            {
                object? returned = descriptor.Method.Invoke(instance, BindingFlags.DoNotWrapExceptions, null, arguments, null);
                object? result = await method.UnwrapAsync(returned).ConfigureAwait(false);

                return new RpcResponse
                {
                    Id = request.Id,
                    Result = descriptor.ResultType is null
                        ? null
                        : JsonSerializer.SerializeToElement(result, descriptor.ResultType, RpcJson.Options),
                };
            }
            finally
            {
                if (perCall)
                {
                    await DisposeInstanceAsync(instance).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            return new RpcResponse
            {
                Id = request.Id,
                Error = new RpcError { Type = ex.GetType().FullName!, Message = ex.Message, StackTrace = ex.StackTrace },
            };
        }
    }

    private object GetSingleton(Type implementationType)
    {
        var lazy = _singletons.GetOrAdd(implementationType, t => new Lazy<object>(() => _instanceFactory(t)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Don't cache a failed construction; the next call gets another chance.
            _singletons.TryRemove(new KeyValuePair<Type, Lazy<object>>(implementationType, lazy));
            throw;
        }
    }

    private static object DefaultFactory(Type type) =>
        Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create '{type}'.");

    private static async ValueTask DisposeInstanceAsync(object instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _singletons.Values.Where(l => l.IsValueCreated))
        {
            await DisposeInstanceAsync(lazy.Value).ConfigureAwait(false);
        }

        _singletons.Clear();
    }

    private sealed record ServiceEntry(
        Type InterfaceType,
        Type ImplementationType,
        IReadOnlyDictionary<string, ServiceMethod> Methods);

    private sealed class ServiceMethod
    {
        private readonly PropertyInfo? _taskResult;
        private readonly MethodInfo? _valueTaskAsTask;

        public ServiceMethod(RemoteMethodDescriptor descriptor)
        {
            Descriptor = descriptor;
            if (descriptor.ReturnKind is ReturnKind.TaskOfT or ReturnKind.ValueTaskOfT)
            {
                _taskResult = typeof(Task<>).MakeGenericType(descriptor.ResultType!).GetProperty(nameof(Task<int>.Result));
            }

            if (descriptor.ReturnKind == ReturnKind.ValueTaskOfT)
            {
                _valueTaskAsTask = descriptor.Method.ReturnType.GetMethod(nameof(ValueTask<int>.AsTask));
            }
        }

        public RemoteMethodDescriptor Descriptor { get; }

        /// <summary>Awaits the returned task (if any) and extracts the result value.</summary>
        public async Task<object?> UnwrapAsync(object? returned)
        {
            switch (Descriptor.ReturnKind)
            {
                case ReturnKind.Void:
                    return null;
                case ReturnKind.Value:
                    return returned;
                case ReturnKind.Task:
                    await ((Task)returned!).ConfigureAwait(false);
                    return null;
                case ReturnKind.ValueTask:
                    await ((ValueTask)returned!).ConfigureAwait(false);
                    return null;
                case ReturnKind.TaskOfT:
                    var task = (Task)returned!;
                    await task.ConfigureAwait(false);
                    return _taskResult!.GetValue(task);
                case ReturnKind.ValueTaskOfT:
                    var asTask = (Task)_valueTaskAsTask!.Invoke(returned, null)!;
                    await asTask.ConfigureAwait(false);
                    return _taskResult!.GetValue(asTask);
                default:
                    throw new InvalidOperationException($"Unknown return kind {Descriptor.ReturnKind}.");
            }
        }
    }
}
