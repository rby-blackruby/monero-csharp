namespace Monero.Common;

[Obsolete("Replaced by MoneroRpcError.")]
public class JsonRpcApiException(JsonRpcResponseError error, string message, string? rpcMethod, object? parameters)
    : MoneroRpcError(message, error.Code, rpcMethod, parameters)
{
    public JsonRpcApiException(JsonRpcResponseError error)
        : this(error, error.Message, null, null)
    {
    }

    public JsonRpcResponseError Error { get; set; } = error;

    public override string Message => Error.Message;
}