namespace RemoteMethodCall;

/// <summary>How the server obtains the service instance for a call. Chosen by the client per proxy.</summary>
public enum InstanceMode
{
    /// <summary>A new instance is created for every call and disposed afterwards.</summary>
    PerCall = 0,

    /// <summary>One shared instance per implementation type, shared by all clients for the server lifetime.</summary>
    Singleton = 1,
}
