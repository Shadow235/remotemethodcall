namespace RemoteMethodCall;

/// <summary>Thrown on the client when the remote method (or the server dispatch) failed.</summary>
public sealed class RemoteInvocationException : Exception
{
    public RemoteInvocationException(string remoteExceptionType, string message, string? remoteStackTrace)
        : base(message)
    {
        RemoteExceptionType = remoteExceptionType;
        RemoteStackTrace = remoteStackTrace;
    }

    /// <summary>Full type name of the exception thrown on the server.</summary>
    public string RemoteExceptionType { get; }

    public string? RemoteStackTrace { get; }

    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}--- Remote {RemoteExceptionType} ---{Environment.NewLine}{RemoteStackTrace}";
}
