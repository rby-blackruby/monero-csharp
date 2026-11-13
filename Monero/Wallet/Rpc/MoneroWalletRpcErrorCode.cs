using Monero.Common;

namespace Monero.Wallet.Rpc;

/// <summary>
/// JSON-RPC error codes returned by monero-wallet-rpc (<c>wallet_rpc_server_error_codes.h</c>).
/// </summary>
public enum MoneroWalletRpcErrorCode
{
    UnknownError = -1,
    WrongAddress = -2,
    DaemonIsBusy = -3,
    GenericTransferError = -4,
    WrongPaymentId = -5,
    TransferType = -6,
    Denied = -7,
    WrongTxId = -8,
    WrongSignature = -9,
    WrongKeyImage = -10,
    WrongUri = -11,
    WrongIndex = -12,
    NotOpen = -13,
    AccountIndexOutOfBounds = -14,
    AddressIndexOutOfBounds = -15,
    TxNotPossible = -16,
    NotEnoughMoney = -17,
    TxTooLarge = -18,
    NotEnoughOutsToMix = -19,
    ZeroDestination = -20,
    WalletAlreadyExists = -21,
    InvalidPassword = -22,
    NoWalletDir = -23,
    NoTxKey = -24,
    WrongKey = -25,
    BadHex = -26,
    BadTxMetadata = -27,
    AlreadyMultisig = -28,
    WatchOnly = -29,
    BadMultisigInfo = -30,
    NotMultisig = -31,
    WrongLr = -32,
    ThresholdNotReached = -33,
    BadMultisigTxData = -34,
    MultisigSignature = -35,
    MultisigSubmission = -36,
    NotEnoughUnlockedMoney = -37,
    NoDaemonConnection = -38,
    BadUnsignedTxData = -39,
    BadSignedTxData = -40,
    SignedSubmission = -41,
    SignUnsigned = -42,
    NonDeterministic = -43,
    InvalidLogLevel = -44,
    AttributeNotFound = -45,
    MaxAddresses = -46,
    InvalidParameter = -47
}

public static class MoneroWalletRpcErrorCodeExtensions
{
    public static int ToCode(this MoneroWalletRpcErrorCode errorCode) => (int)errorCode;

    public static bool HasCode(this MoneroError error, MoneroWalletRpcErrorCode errorCode) =>
        error.GetCode() == (int)errorCode;

    public static MoneroWalletRpcErrorCode? GetWalletRpcErrorCode(this MoneroError error) =>
        error.GetCode() is int code && Enum.IsDefined(typeof(MoneroWalletRpcErrorCode), code)
            ? (MoneroWalletRpcErrorCode)code
            : null;
}
