using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Monero.Common;
using Monero.Daemon.Rpc;
using Monero.Wallet;
using Monero.Wallet.Common;
using Monero.Wallet.Rpc;

using NUnit.Framework;

namespace Monero.UnitTests;

public class MoneroRpcConnectionUnitTest
{
    // Snapshot of an outgoing request taken inside the handler before MoneroRpcConnection disposes it
    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        IReadOnlyList<MediaTypeWithQualityHeaderValue> Accept,
        MediaTypeHeaderValue? ContentType,
        string Body);

    // Minimal fake transport that lets each test decide how the server responds without a real network call
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static string RpcErrorJson(int code, string message) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code, message }
        });

    private MoneroWalletRpc CreateWalletWithRpcError(MoneroWalletRpcErrorCode code, string message) =>
        new(CreateRpc(HttpStatusCode.OK, RpcErrorJson(code.ToCode(), message)));

    private static void AssertNormalizedErrorPreservesOriginal(MoneroRpcError error, string originalMessage)
    {
        Assert.That(error.InnerException, Is.InstanceOf<MoneroRpcError>());
        MoneroRpcError original = (MoneroRpcError)error.InnerException!;
        Assert.That(original.Message, Is.EqualTo(originalMessage));
        Assert.That(original.GetCode(), Is.EqualTo(error.GetCode()));
        Assert.That(original.GetRpcMethod(), Is.EqualTo(error.GetRpcMethod()));
        Assert.That(error.GetRpcParams(), Is.Not.Null);
        Assert.That(original.GetRpcParams(), Is.SameAs(error.GetRpcParams()));
        Assert.That(original.StackTrace, Does.Contain("SendCommandAsync"));
    }

    private const string PrimaryAddressJson =
        """{"jsonrpc":"2.0","id":"1","result":{"address":"addr","addresses":[{"address":"addr","address_index":0,"label":"","used":false}]}}""";

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // IsConnectedToDaemon first resolves the primary address via get_address, so answer that call successfully
    // and hand every later request (check_reserve_proof) the response under test. The RPC methods sent are
    // recorded so tests can verify check_reserve_proof was actually reached.
    private MoneroWalletRpc CreateWalletReachingCheckReserve(Func<HttpResponseMessage> checkReserveResponse,
        List<string> methods)
    {
        int calls = 0;
        MoneroRpcConnection rpc = CreateRpc(
            _ => Interlocked.Increment(ref calls) == 1
                ? JsonResponse(HttpStatusCode.OK, PrimaryAddressJson)
                : checkReserveResponse(),
            request =>
            {
                using JsonDocument body = JsonDocument.Parse(request.Body);
                methods.Add(body.RootElement.GetProperty("method").GetString()!);
            });
        return new MoneroWalletRpc(rpc);
    }

    private static readonly string[] CheckReserveMethods = ["get_address", "check_reserve_proof"];


    // MoneroRpcConnection doesn't dispose its HttpClient and CreateRpc doesn't expose it,
    // so track created clients here to dispose them after each test
    private readonly List<HttpClient> _clients = [];

    [TearDown]
    public void TearDown()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private MoneroRpcConnection CreateRpc(HttpStatusCode status, string json,
        Action<CapturedRequest>? capture = null) =>
        CreateRpc(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }, capture);

    private MoneroRpcConnection CreateRpc(Func<CancellationToken, HttpResponseMessage> respond,
        Action<CapturedRequest>? capture = null)
    {
        StubHandler handler = new(async (request, token) =>
        {
            if (capture != null)
            {
                string body = request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(CancellationToken.None);
                
                capture(new CapturedRequest(
                    request.Method,
                    request.RequestUri,
                    request.Headers.Authorization,
                    request.Headers.Accept.ToList(),
                    request.Content?.Headers.ContentType,
                    body));
            }

            return respond(token);
        });

        HttpClient client = new(handler);
        _clients.Add(client);
        return new MoneroRpcConnection(new Uri("http://127.0.0.1:18082/"), "user", "pass", client);
    }

    private MoneroRpcConnection CreateRpcAsync(Func<CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null)
    {
        StubHandler handler = new((_, token) => respond(token));
        HttpClient client = new(handler);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }

        _clients.Add(client);
        return new MoneroRpcConnection(new Uri("http://127.0.0.1:18082/"), "user", "pass", client);
    }

    // The request must be a JSON-RPC 2.0 POST to /json_rpc with basic auth and the method/params in the body
    [Test]
    public async Task TestSendCommandAsyncRequest()
    {
        CapturedRequest? captured = null;
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"1","result":0}""",
            request => captured = request);
        Dictionary<string, object?> parameters = new() { ["address"] = "addr", ["account_index"] = 1 };

        await rpc.SendCommandAsync<Dictionary<string, object?>, int>("get_address_index", parameters);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(captured.RequestUri, Is.EqualTo(new Uri("http://127.0.0.1:18082/json_rpc")));

        Assert.That(captured.Authorization?.Scheme, Is.EqualTo("Basic"));
        Assert.That(captured.Authorization?.Parameter,
            Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"))));

        Assert.That(captured.Accept.Select(a => a.MediaType), Is.EqualTo(new[] { "application/json" }));
        Assert.That(captured.ContentType?.MediaType, Is.EqualTo("application/json"));

        using JsonDocument body = JsonDocument.Parse(captured.Body);
        JsonElement root = body.RootElement;
        Assert.That(root.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
        Assert.That(root.GetProperty("id").GetString(), Is.Not.Empty);
        Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("get_address_index"));
        JsonElement @params = root.GetProperty("params");
        Assert.That(@params.GetProperty("address").GetString(), Is.EqualTo("addr"));
        Assert.That(@params.GetProperty("account_index").GetInt32(), Is.EqualTo(1));
    }

    // Ensures normal JSON-RPC responses still deserialize and return the result payload
    [Test]
    public async Task TestSendCommandAsyncSuccess()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"1","result":"abc"}""");

        string result = await rpc.SendCommandAsync<NoRequestModel, string>("get_info", NoRequestModel.Instance);

        Assert.That(result, Is.EqualTo("abc"));
    }

    // Callers filter on MoneroRpcError and its RPC code, so JSON-RPC errors must preserve code, method, and params
    [Test]
    public async Task TestSendCommandAsyncRpcError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":"1","error":{"code":-2,"message":"Address not in wallet"}}""");
        Dictionary<string, object?> parameters = new() { ["address"] = "bad" };

        MoneroRpcError? ex = await Assert.CatchAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<Dictionary<string, object?>, string>("get_address_index", parameters));

#pragma warning disable CS0618
        Assert.That(ex, Is.InstanceOf<JsonRpcApiException>());
        JsonRpcApiException jsonEx = (JsonRpcApiException)ex!;
        Assert.That(jsonEx.Error, Is.Not.Null);
        Assert.That(jsonEx.Error.Code, Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress.ToCode()));
        Assert.That(jsonEx.Error.Message, Is.EqualTo("Address not in wallet"));
#pragma warning restore CS0618
        Assert.That(ex!.GetCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress.ToCode()));
        Assert.That(ex.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongAddress), Is.True);
        Assert.That(ex.Message, Is.EqualTo("Address not in wallet"));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_address_index"));
        Assert.That(ex.GetRpcParams(), Is.SameAs(parameters));
    }

    [Test]
    public void TestJsonRpcApiExceptionMessageTracksError()
    {
#pragma warning disable CS0618
        JsonRpcResponseError original = new(-2, "Original message", null!);
        JsonRpcApiException ex = new(original);
        Assert.That(ex.Message, Is.EqualTo("Original message"));

        original.Message = "Updated message";
        Assert.That(ex.Message, Is.EqualTo("Updated message"));

        ex.Error = new JsonRpcResponseError(-3, "Replacement message", null!);
        Assert.That(ex.Message, Is.EqualTo("Replacement message"));
#pragma warning restore CS0618
    }

    [Test]
    public void TestJsonRpcApiExceptionMessageTracksEmptyError()
    {
#pragma warning disable CS0618
        JsonRpcApiException ex = new(new JsonRpcResponseError(-2, "", null!),
            "Fallback message", "get_info", null);
        Assert.That(ex.Message, Is.Empty);

        ex.Error = new JsonRpcResponseError(-3, "Replacement message", null!);
        Assert.That(ex.Message, Is.EqualTo("Replacement message"));

        ex.Error = new JsonRpcResponseError(-4, "", null!);
        Assert.That(ex.Message, Is.Empty);
#pragma warning restore CS0618
    }

    // A null JSON body must throw MoneroRpcInvalidResponseError rather than a NullReferenceException or fake 500
    [Test]
    public async Task TestSendCommandAsyncNullResponse()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, "null");

        MoneroRpcInvalidResponseError? ex = await Assert.ThrowsAsync<MoneroRpcInvalidResponseError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_version", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.EqualTo(-1));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_version"));
    }

    // Malformed JSON must be wrapped in MoneroRpcError so callers don't have to catch raw JsonException
    [Test]
    public async Task TestSendCommandAsyncInvalidJson()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, "{invalid");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_balance", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.Null);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_balance"));
        Assert.That(ex.InnerException, Is.InstanceOf<JsonException>());
        Assert.That(ex.InnerException!.StackTrace, Is.Not.Empty);
    }

    // Non-2xx HTTP responses must throw MoneroRpcError with the HTTP status code and body instead of HttpRequestException
    [Test]
    public async Task TestSendCommandAsyncHttpError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.BadGateway, "upstream down");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("refresh", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.EqualTo(502));
        Assert.That(ex.Message, Does.Contain("upstream down"));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("refresh"));
    }

    // Transport failures must wrap into MoneroRpcError so all RPC failures share one exception hierarchy
    [Test]
    public async Task TestSendCommandAsyncTransportError()
    {
        HttpRequestException original = new("Connection refused", new IOException("Socket failure"));
        MoneroRpcConnection rpc = CreateRpc(_ => throw original);

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_version", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.Null);
        Assert.That(ex.Message, Is.EqualTo("Connection refused"));
        Assert.That(ex.InnerException, Is.SameAs(original));
        Assert.That(ex.InnerException!.InnerException, Is.SameAs(original.InnerException));
        Assert.That(ex.InnerException.StackTrace, Is.Not.Empty);
        Assert.That(ex.GetRpcParams(), Is.SameAs(NoRequestModel.Instance));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_version"));
    }

    // Caller cancellation must still propagate OperationCanceledException rather than being masked as MoneroRpc error
    [Test]
    public async Task TestSendCommandAsyncCallerCancellation()
    {
        using CancellationTokenSource cts = new();
        MoneroRpcConnection rpc = CreateRpc(token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.CatchAsync<OperationCanceledException>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_version", NoRequestModel.Instance, cts.Token));
    }

    // HttpClient.Timeout throws TaskCanceledException without cancelling the caller's token, so it must wrap as MoneroRpcError
    [Test]
    public async Task TestSendCommandAsyncTimeoutThrowsMoneroRpcError()
    {
        MoneroRpcConnection rpc = CreateRpcAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeout: TimeSpan.FromMilliseconds(50));

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_version", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.Null);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_version"));
        Assert.That(ex.Message, Is.Not.Empty);
        Assert.That(ex.InnerException, Is.InstanceOf<OperationCanceledException>());
    }

    // Address-not-in-wallet (-2) must stay a MoneroRpcError rather than being downgraded to plain MoneroError
    [Test]
    public async Task TestGetAddressIndexNotInWalletThrowsMoneroRpcError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":"1","error":{"code":-2,"message":"Address not in wallet"}}""");
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcError? ex = await Assert.CatchAsync<MoneroRpcError>(async () =>
            await wallet.GetAddressIndex("bad_addr"));

        Assert.That(ex!.GetCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress.ToCode()));
        Assert.That(ex.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongAddress), Is.True);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_address_index"));
    }

    // Invalid payment ID must throw MoneroRpcError with code -5 and preserve RPC method metadata
    [Test]
    public async Task TestGetIntegratedAddressInvalidPaymentIdThrowsMoneroRpcError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":"1","error":{"code":-5,"message":"Invalid payment ID"}}""");
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await wallet.GetIntegratedAddress("std_addr", "bad_pid"));

        Assert.That(ex!.GetCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongPaymentId.ToCode()));
        Assert.That(ex.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongPaymentId));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongPaymentId), Is.True);
        Assert.That(ex.Message, Is.EqualTo("Invalid payment ID: bad_pid"));
        AssertNormalizedErrorPreservesOriginal(ex, "Invalid payment ID");
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("make_integrated_address"));
    }

    // Unrecognized errors rethrown by wallet helpers must keep their exact subclass type and original stack trace
    [Test]
    public async Task TestWalletErrorHelpersPreserveOriginalExceptionAndStackTrace()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, "null");
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcInvalidResponseError? txEx = await Assert.ThrowsAsync<MoneroRpcInvalidResponseError>(async () =>
            await wallet.GetTxKey("abc"));
        Assert.That(txEx!.StackTrace, Does.Contain("SendCommandAsync"));

        MoneroRpcInvalidResponseError? createEx = await Assert.ThrowsAsync<MoneroRpcInvalidResponseError>(async () =>
            await wallet.CreateWallet(new MoneroWalletConfig().SetPath("w").SetPassword("p")));
        Assert.That(createEx!.StackTrace, Does.Contain("SendCommandAsync"));
    }

    // Wallet and daemon RPC error code enums and extension helpers map known RPC codes and return null otherwise
    [Test]
    public void TestRpcErrorCodeExtensions()
    {
        Assert.That(MoneroWalletRpcErrorCode.WalletAlreadyExists.ToCode(), Is.EqualTo(-21));
        Assert.That(MoneroDaemonRpcErrorCode.BlockNotAccepted.ToCode(), Is.EqualTo(-7));

        MoneroRpcError walletError = new("exists", -21, "create_wallet");
        Assert.That(walletError.HasCode(MoneroWalletRpcErrorCode.WalletAlreadyExists), Is.True);
        Assert.That(walletError.HasCode(MoneroWalletRpcErrorCode.WrongAddress), Is.False);
        Assert.That(walletError.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WalletAlreadyExists));

        MoneroRpcError daemonError = new("rejected", -7, "submit_block");
        Assert.That(daemonError.HasCode(MoneroDaemonRpcErrorCode.BlockNotAccepted), Is.True);
        Assert.That(daemonError.HasCode(MoneroDaemonRpcErrorCode.CoreBusy), Is.False);
        Assert.That(daemonError.GetDaemonRpcErrorCode(), Is.EqualTo(MoneroDaemonRpcErrorCode.BlockNotAccepted));

        MoneroRpcError httpError = new("502 Bad Gateway", 502, "get_info");
        Assert.That(httpError.GetWalletRpcErrorCode(), Is.Null);
        Assert.That(httpError.GetDaemonRpcErrorCode(), Is.Null);

        MoneroRpcError noCodeError = new("transport", null, "get_info");
        Assert.That(noCodeError.GetWalletRpcErrorCode(), Is.Null);
        Assert.That(noCodeError.GetDaemonRpcErrorCode(), Is.Null);
        Assert.That(noCodeError.HasCode(MoneroWalletRpcErrorCode.UnknownError), Is.False);
        Assert.That(noCodeError.HasCode(MoneroDaemonRpcErrorCode.WrongParam), Is.False);
    }

    // Sync must release its semaphore when the RPC call fails, otherwise every later Sync() hangs forever
    [TestCase("no connection to daemon")]
    [TestCase("No connection to daemon")]
    public async Task TestSyncReleasesLockOnRpcError(string rpcMessage)
    {
        MoneroWalletRpc wallet =
            CreateWalletWithRpcError(MoneroWalletRpcErrorCode.NoDaemonConnection, rpcMessage);

        MoneroError? first = await Assert.CatchAsync<MoneroError>(async () => await wallet.Sync(null, null));
        Assert.That(first!.Message, Is.EqualTo("Wallet is not connected to daemon"));

        Task second = Task.Run(async () =>
        {
            try { await wallet.Sync(null, null); }
            catch (MoneroError) { /* expected, we only care that it returns */ }
        });
        Task finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.That(finished, Is.SameAs(second), "Sync() deadlocked: _syncSem was not released after an RPC error");
    }

    // Watch-only error (-29) means the wallet is view-only, unknown error (-1) means offline but not view-only
    [TestCase(MoneroWalletRpcErrorCode.WatchOnly, "wallet is watch-only and has no seed", true)]
    [TestCase(MoneroWalletRpcErrorCode.UnknownError, "wallet is offline", false)]
    public async Task TestIsViewOnlyHandlesRpcErrors(MoneroWalletRpcErrorCode code, string message, bool expected)
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(code, message);

        bool isViewOnly = await wallet.IsViewOnly();

        Assert.That(isViewOnly, Is.EqualTo(expected));
    }

    // Invalid responses use code -1 too, but must not be mistaken for the wallet's UnknownError response
    [Test]
    public async Task TestIsViewOnlyRethrowsInvalidResponse()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK, "null");
        MoneroWalletRpc wallet = new(rpc);

        await Assert.ThrowsAsync<MoneroRpcInvalidResponseError>(async () => await wallet.IsViewOnly());
    }

    // Transport errors say nothing about whether the wallet is view-only and must be rethrown
    [Test]
    public async Task TestIsViewOnlyRethrowsTransportError()
    {
        MoneroRpcConnection rpc = CreateRpc(_ => throw new HttpRequestException("Connection refused"));
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () => await wallet.IsViewOnly());

        Assert.That(ex!.GetCode(), Is.Null);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("query_key"));
    }

    // HTTP errors say nothing about whether the wallet is view-only and must be rethrown
    [Test]
    public async Task TestIsViewOnlyRethrowsHttpError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.BadGateway, "upstream down");
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () => await wallet.IsViewOnly());

        Assert.That(ex!.GetCode(), Is.EqualTo(502));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("query_key"));
    }

    // Wallet RPC errors other than WatchOnly (-29) and UnknownError (-1) must be rethrown
    [TestCase(MoneroWalletRpcErrorCode.NotOpen, "No wallet file")]
    [TestCase(MoneroWalletRpcErrorCode.NonDeterministic, "The wallet is non-deterministic. Cannot display seed.")]
    public async Task TestIsViewOnlyRethrowsOtherRpcErrors(MoneroWalletRpcErrorCode code, string message)
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(code, message);

        MoneroRpcError? ex = await Assert.CatchAsync<MoneroRpcError>(async () => await wallet.IsViewOnly());

        Assert.That(ex!.GetWalletRpcErrorCode(), Is.EqualTo(code));
        Assert.That(ex!.GetRpcMethod(), Is.EqualTo("query_key"));
    }

    // Transport failures say nothing about the wallet's daemon connection and must not be converted to true
    [Test]
    public async Task TestIsConnectedToDaemonRethrowsTransportError()
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => throw new HttpRequestException("Connection refused"), methods);

        await Assert.ThrowsAsync<MoneroRpcError>(async () => await wallet.IsConnectedToDaemon());
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // HTTP errors are not wallet RPC error responses and must not be converted to true
    [Test]
    public async Task TestIsConnectedToDaemonRethrowsHttpError()
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => JsonResponse(HttpStatusCode.BadGateway, "upstream down"), methods);

        await Assert.ThrowsAsync<MoneroRpcError>(async () => await wallet.IsConnectedToDaemon());
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // Invalid responses also use code -1, but are not evidence of a successful daemon connection
    [Test]
    public async Task TestIsConnectedToDaemonRethrowsInvalidResponse()
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => JsonResponse(HttpStatusCode.OK, "null"), methods);

        await Assert.ThrowsAsync<MoneroRpcInvalidResponseError>(async () => await wallet.IsConnectedToDaemon());
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // Genuine wallet RPC error responses retain the existing daemon-connectivity interpretation
    [TestCase(MoneroWalletRpcErrorCode.NoDaemonConnection, "Failed to connect to daemon", false)]
    [TestCase(MoneroWalletRpcErrorCode.WrongAddress, "Address not in wallet", true)]
    public async Task TestIsConnectedToDaemonHandlesRpcErrors(MoneroWalletRpcErrorCode code, string message,
        bool expected)
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => JsonResponse(HttpStatusCode.OK, RpcErrorJson(code.ToCode(), message)), methods);

        bool isConnected = await wallet.IsConnectedToDaemon();

        Assert.That(isConnected, Is.EqualTo(expected));
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // check_reserve_proof with an empty signature is expected to fail; a synthetic success must throw
    [Test]
    public async Task TestIsConnectedToDaemonThrowsOnUnexpectedSuccess()
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => JsonResponse(HttpStatusCode.OK,
                """{"jsonrpc":"2.0","id":"1","result":{"good":true,"total":0,"spent":0}}"""),
            methods);

        Exception? ex = await Assert.ThrowsAsync<Exception>(async () => await wallet.IsConnectedToDaemon());

        Assert.That(ex!.Message, Is.EqualTo("check reserve expected to fail"));
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // Non-negative RPC codes are not wallet RPC errors and must be rethrown rather than treated as connected
    [TestCase(0)]
    [TestCase(1)]
    public async Task TestIsConnectedToDaemonRethrowsNonNegativeRpcErrorCode(int code)
    {
        List<string> methods = [];
        MoneroWalletRpc wallet = CreateWalletReachingCheckReserve(
            () => JsonResponse(HttpStatusCode.OK, RpcErrorJson(code, "unexpected non-negative code")), methods);

        MoneroRpcError? ex = await Assert.CatchAsync<MoneroRpcError>(async () =>
            await wallet.IsConnectedToDaemon());

        Assert.That(ex!.GetCode(), Is.EqualTo(code));
        Assert.That(methods, Is.EqualTo(CheckReserveMethods));
    }

    // "No wallet file" (-13 / NotOpen) must report the wallet as closed instead of falling through to the catch-all
    [Test]
    public async Task TestIsClosedReturnsTrueWhenNoWalletFile()
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(MoneroWalletRpcErrorCode.NotOpen, "No wallet file");

        bool isClosed = await wallet.IsClosed();

        Assert.That(isClosed, Is.True);
    }

    // Other wallet RPC error codes or messages must report false rather than matching NotOpen + "No wallet file"
    [TestCase(MoneroWalletRpcErrorCode.WrongTxId, "No wallet file")]
    [TestCase(MoneroWalletRpcErrorCode.NotOpen, "Some other error")]
    [TestCase(MoneroWalletRpcErrorCode.UnknownError, "Unknown error")]
    public async Task TestIsClosedReturnsFalseOnOtherMoneroRpcErrors(MoneroWalletRpcErrorCode code, string message)
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(code, message);

        bool isClosed = await wallet.IsClosed();

        Assert.That(isClosed, Is.False);
    }

    // Transport, HTTP, and invalid-response errors do not prove the wallet is closed and must return false
    [Test]
    public async Task TestIsClosedReturnsFalseOnHttpAndTransportErrors()
    {
        MoneroWalletRpc transportWallet =
            new(CreateRpc(_ => throw new HttpRequestException("Connection refused")));
        Assert.That(await transportWallet.IsClosed(), Is.False);

        MoneroWalletRpc httpWallet = new(CreateRpc(HttpStatusCode.BadGateway, "upstream down"));
        Assert.That(await httpWallet.IsClosed(), Is.False);

        MoneroWalletRpc invalidWallet = new(CreateRpc(HttpStatusCode.OK, "null"));
        Assert.That(await invalidWallet.IsClosed(), Is.False);
    }

    // Non-MoneroRpcError exceptions (such as plain MoneroError when primary address is missing) hit catch-all and return false
    [Test]
    public async Task TestIsClosedReturnsFalseOnNonRpcException()
    {
        const string emptyAddressesJson = """{"jsonrpc":"2.0","id":"1","result":{"address":"","addresses":[]}}""";

        MoneroWalletRpc probeWallet = new(CreateRpc(HttpStatusCode.OK, emptyAddressesJson));
        await Assert.ThrowsAsync<MoneroError>(async () => await probeWallet.GetPrimaryAddress());

        MoneroWalletRpc wallet = new(CreateRpc(HttpStatusCode.OK, emptyAddressesJson));
        Assert.That(await wallet.IsClosed(), Is.False);
    }

    // A wrong address (-2) while verifying must return a "not good" response instead of throwing
    [Test]
    public async Task TestVerifyMessageWrongAddressReturnsNotGood()
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(MoneroWalletRpcErrorCode.WrongAddress, "Invalid address");

        MoneroMessageSignatureResponse response = await wallet.VerifyMessage("msg", "bad_addr", "sig");

        Assert.That(response.IsGood, Is.False);
    }

    // Invalid tx id (-8) error message is normalized to "TX hash has invalid format"
    [Test]
    public async Task TestGetTxKeyInvalidTxIdNormalizesMessage()
    {
        MoneroWalletRpc wallet =
            CreateWalletWithRpcError(MoneroWalletRpcErrorCode.WrongTxId, "TX ID has invalid format");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () => await wallet.GetTxKey("bad_txid"));

        Assert.That(ex!.Message, Is.EqualTo("TX hash has invalid format"));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongTxId), Is.True);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_tx_key"));
        AssertNormalizedErrorPreservesOriginal(ex, "TX ID has invalid format");
    }

    // Missing signature ("basic_string", -1) error message is normalized to a readable message
    [Test]
    public async Task TestCheckTxProofMissingSignatureNormalizesMessage()
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(MoneroWalletRpcErrorCode.UnknownError, "basic_string");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await wallet.CheckTxProof("txid", "addr", "msg", ""));

        Assert.That(ex!.Message, Is.EqualTo("Must provide signature to check tx proof"));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.UnknownError), Is.True);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("check_tx_proof"));
        AssertNormalizedErrorPreservesOriginal(ex, "basic_string");
        var parameters = (Dictionary<string, object?>)ex.GetRpcParams()!;
        Assert.That(parameters["txid"], Is.EqualTo("txid"));
        Assert.That(parameters["address"], Is.EqualTo("addr"));
        Assert.That(parameters["message"], Is.EqualTo("msg"));
        Assert.That(parameters["signature"], Is.EqualTo(""));
    }

    // Attribute not found (-45) returns null instead of throwing
    [Test]
    public async Task TestGetAttributeNotFoundReturnsNull()
    {
        MoneroWalletRpc wallet =
            CreateWalletWithRpcError(MoneroWalletRpcErrorCode.AttributeNotFound, "Attribute not found.");

        string? value = await wallet.GetAttribute("missing_key");

        Assert.That(value, Is.Null);
    }

    // Wallet already exists error is mapped to a MoneroRpcError that keeps code and RPC method
    [Test]
    public async Task TestCreateWalletAlreadyExistsMapsError()
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(MoneroWalletRpcErrorCode.UnknownError,
            "Cannot create wallet. Already exists.");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await wallet.CreateWallet(new MoneroWalletConfig().SetPath("w").SetPassword("p")));

        Assert.That(ex!.Message, Is.EqualTo("Wallet already exists: w"));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.UnknownError), Is.True);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("create_wallet"));
        AssertNormalizedErrorPreservesOriginal(ex, "Cannot create wallet. Already exists.");
    }

    // Invalid seed error is mapped to "Invalid mnemonic" and keeps code and RPC method
    [Test]
    public async Task TestCreateWalletFromSeedInvalidMnemonicMapsError()
    {
        MoneroWalletRpc wallet = CreateWalletWithRpcError(MoneroWalletRpcErrorCode.UnknownError,
            "Electrum-style word list failed verification");

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await wallet.CreateWallet(new MoneroWalletConfig().SetPath("w").SetPassword("p").SetSeed("bad seed")));

        Assert.That(ex!.Message, Is.EqualTo("Invalid mnemonic"));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.UnknownError), Is.True);
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("restore_deterministic_wallet"));
        AssertNormalizedErrorPreservesOriginal(ex, "Electrum-style word list failed verification");
    }
}
