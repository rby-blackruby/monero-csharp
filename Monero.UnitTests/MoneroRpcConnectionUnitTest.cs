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

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<Dictionary<string, object?>, string>("get_address_index", parameters));

        Assert.That(ex!.GetCode(), Is.EqualTo(-2));
        Assert.That(ex.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongAddress));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongAddress), Is.True);
        Assert.That(ex.Message, Is.EqualTo("Address not in wallet"));
        Assert.That(ex.GetRpcMethod(), Is.EqualTo("get_address_index"));
        Assert.That(ex.GetRpcParams(), Is.SameAs(parameters));
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
        MoneroRpcConnection rpc = CreateRpc(_ => throw new HttpRequestException("Connection refused"));

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await rpc.SendCommandAsync<NoRequestModel, string>("get_version", NoRequestModel.Instance));

        Assert.That(ex!.GetCode(), Is.Null);
        Assert.That(ex.Message, Is.EqualTo("Connection refused"));
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

    // Address-not-in-wallet (-2) must stay a MoneroRpcError rather than being downgraded to plain MoneroError
    [Test]
    public async Task TestGetAddressIndexNotInWalletThrowsMoneroRpcError()
    {
        MoneroRpcConnection rpc = CreateRpc(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":"1","error":{"code":-2,"message":"Address not in wallet"}}""");
        MoneroWalletRpc wallet = new(rpc);

        MoneroRpcError? ex = await Assert.ThrowsAsync<MoneroRpcError>(async () =>
            await wallet.GetAddressIndex("bad_addr"));

        Assert.That(ex!.GetCode(), Is.EqualTo(-2));
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

        Assert.That(ex!.GetCode(), Is.EqualTo(-5));
        Assert.That(ex.GetWalletRpcErrorCode(), Is.EqualTo(MoneroWalletRpcErrorCode.WrongPaymentId));
        Assert.That(ex.HasCode(MoneroWalletRpcErrorCode.WrongPaymentId), Is.True);
        Assert.That(ex.Message, Is.EqualTo("Invalid payment ID: bad_pid"));
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
}
