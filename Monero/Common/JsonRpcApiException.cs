namespace Monero.Common;

[Obsolete("Replaced by MoneroRpcError.")]
public class JsonRpcApiException(JsonRpcResponseError error) : MoneroRpcError(error.Message, error.Code)
{
    public JsonRpcResponseError Error { get; set; } = error;

    public override string Message => Error.Message;
}