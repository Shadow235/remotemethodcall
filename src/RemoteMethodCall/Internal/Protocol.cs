using System.Buffers.Binary;
using System.Text.Json;

namespace RemoteMethodCall.Internal;

internal sealed class RpcRequest
{
    public long Id { get; set; }

    public string Service { get; set; } = "";

    public string Method { get; set; } = "";

    public InstanceMode Mode { get; set; }

    public JsonElement[] Arguments { get; set; } = [];
}

internal sealed class RpcResponse
{
    public long Id { get; set; }

    public JsonElement? Result { get; set; }

    public RpcError? Error { get; set; }
}

internal sealed class RpcError
{
    public string Type { get; set; } = "";

    public string Message { get; set; } = "";

    public string? StackTrace { get; set; }
}

internal static class RpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}

/// <summary>
/// Messages are framed as a 4-byte little-endian length followed by a UTF-8 JSON payload.
/// </summary>
internal static class MessageFraming
{
    public const int MaxMessageSize = 64 * 1024 * 1024;

    private const int HeaderSize = sizeof(int);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, RpcJson.Options);
        if (payload.Length > MaxMessageSize)
        {
            throw new InvalidOperationException($"Message of {payload.Length} bytes exceeds the {MaxMessageSize} byte limit.");
        }

        byte[] frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, HeaderSize);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message; returns null when the peer closed the connection cleanly.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        byte[] header = new byte[HeaderSize];
        int read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new EndOfStreamException("Connection closed in the middle of a message header.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageSize)
        {
            throw new InvalidDataException($"Invalid message length {length}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(payload, RpcJson.Options)
            ?? throw new InvalidDataException("Received an empty message.");
    }
}
