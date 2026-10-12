namespace RemoteMethodCall;

/// <summary>
/// Marks an interface as remotely callable. The server registers every class that implements
/// such an interface; the client can create a proxy for it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class RemoteServiceAttribute : Attribute
{
    public RemoteServiceAttribute(string? name = null) => Name = name;

    /// <summary>Name the service is addressed by on the wire. Defaults to the interface full name.</summary>
    public string? Name { get; }

    public static bool IsRemoteService(Type type) =>
        type.IsInterface && type.IsDefined(typeof(RemoteServiceAttribute), inherit: false);

    public static string GetServiceName(Type interfaceType)
    {
        var attribute = interfaceType.GetCustomAttributes(typeof(RemoteServiceAttribute), inherit: false)
            .Cast<RemoteServiceAttribute>()
            .FirstOrDefault();

        if (!interfaceType.IsInterface || attribute is null)
        {
            throw new InvalidOperationException(
                $"Type '{interfaceType}' must be an interface marked with [{nameof(RemoteServiceAttribute)}].");
        }

        return string.IsNullOrWhiteSpace(attribute.Name) ? interfaceType.FullName! : attribute.Name;
    }
}
