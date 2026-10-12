using System.Collections.Concurrent;
using System.Reflection;

namespace RemoteMethodCall.Internal;

internal enum ReturnKind
{
    Void,
    Value,
    Task,
    TaskOfT,
    ValueTask,
    ValueTaskOfT,
}

/// <summary>
/// Describes an interface method in a way both sides agree on: the wire signature,
/// parameter types and how the return value is (a)synchronously produced.
/// </summary>
internal sealed class RemoteMethodDescriptor
{
    private static readonly ConcurrentDictionary<MethodInfo, RemoteMethodDescriptor> Cache = new();

    private RemoteMethodDescriptor(MethodInfo method)
    {
        if (method.IsGenericMethodDefinition)
        {
            throw new NotSupportedException($"Generic remote method '{method}' is not supported.");
        }

        var parameters = method.GetParameters();
        if (parameters.Any(p => p.ParameterType.IsByRef))
        {
            throw new NotSupportedException($"Remote method '{method}' must not have ref/out/in parameters.");
        }

        Method = method;
        ParameterTypes = parameters.Select(p => p.ParameterType).ToArray();
        Signature = CreateSignature(method);
        (ReturnKind, ResultType) = Classify(method.ReturnType);
    }

    public MethodInfo Method { get; }

    /// <summary>Identifies the method on the wire, e.g. <c>Add(System.Int32,System.Int32)</c>; distinguishes overloads.</summary>
    public string Signature { get; }

    public Type[] ParameterTypes { get; }

    public ReturnKind ReturnKind { get; }

    /// <summary>Type of the value sent back to the client; null when nothing is returned.</summary>
    public Type? ResultType { get; }

    public static RemoteMethodDescriptor Get(MethodInfo method) =>
        Cache.GetOrAdd(method, static m => new RemoteMethodDescriptor(m));

    /// <summary>All instance methods callable through the interface, including inherited interfaces.</summary>
    public static IEnumerable<MethodInfo> GetInterfaceMethods(Type interfaceType) =>
        new[] { interfaceType }
            .Concat(interfaceType.GetInterfaces())
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance));

    private static string CreateSignature(MethodInfo method) =>
        $"{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.ToString()))})";

    private static (ReturnKind Kind, Type? ResultType) Classify(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return (ReturnKind.Void, null);
        }

        if (returnType == typeof(Task))
        {
            return (ReturnKind.Task, null);
        }

        if (returnType == typeof(ValueTask))
        {
            return (ReturnKind.ValueTask, null);
        }

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>))
            {
                return (ReturnKind.TaskOfT, returnType.GetGenericArguments()[0]);
            }

            if (definition == typeof(ValueTask<>))
            {
                return (ReturnKind.ValueTaskOfT, returnType.GetGenericArguments()[0]);
            }
        }

        return (ReturnKind.Value, returnType);
    }
}
