using Monero.Common;

namespace Monero.Daemon.Rpc;

/// <summary>
/// JSON-RPC error codes returned by monerod (<c>core_rpc_server_error_codes.h</c>).
/// </summary>
public enum MoneroDaemonRpcErrorCode
{
    WrongParam = -1,
    TooBigHeight = -2,
    TooBigReserveSize = -3,
    WrongWalletAddress = -4,
    InternalError = -5,
    WrongBlockBlob = -6,
    BlockNotAccepted = -7,
    CoreBusy = -8,
    WrongBlockBlobSize = -9,
    UnsupportedRpc = -10,
    MiningToSubaddress = -11,
    RegtestRequired = -12,
    PaymentRequired = -13,
    InvalidClient = -14,
    PaymentTooLow = -15,
    DuplicatePayment = -16,
    StalePayment = -17,
    Restricted = -18
}

public static class MoneroDaemonRpcErrorCodeExtensions
{
    public static int ToCode(this MoneroDaemonRpcErrorCode errorCode) => (int)errorCode;

    public static bool HasCode(this MoneroError error, MoneroDaemonRpcErrorCode errorCode) =>
        error.GetCode() == (int)errorCode;

    public static MoneroDaemonRpcErrorCode? GetDaemonRpcErrorCode(this MoneroError error) =>
        error.GetCode() is int code && Enum.IsDefined(typeof(MoneroDaemonRpcErrorCode), code)
            ? (MoneroDaemonRpcErrorCode)code
            : null;
}
