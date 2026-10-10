using System.Net;
using System.Text;
using System.Text.Json;

namespace FomaPay.Tests;

/// <summary>Faux transport : mémorise les requêtes et délègue la réponse au test.</summary>
internal sealed class FakeHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(string Method, string Url, string? Body, string? Authorization, string? IdempotencyKey)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            body,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null));
        return await respond(Calls.Count, request, cancellationToken);
    }
}

[TestClass]
public sealed class ClientTests
{
    private const string PaymentJson =
        """{"id":"pay_1","reference":"FP-1","status":"pending","amount":25,"currency":"USD","orderId":"CMD-1","champInconnu":1}""";

    private static readonly PayoutCreateOptions Payout =
        new() { Amount = 10, Currency = Currencies.USD, Operator = Operators.MPesa, Phone = "+243810000000" };

    private readonly List<TimeSpan> _waits = [];

    private (FomaPayClient Client, FakeHandler Handler) Make(Func<int, HttpResponseMessage> respond, Action<FomaPayClientOptions>? configure = null) =>
        Make((n, _, _) => Task.FromResult(respond(n)), configure);

    private (FomaPayClient Client, FakeHandler Handler) Make(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, Action<FomaPayClientOptions>? configure = null)
    {
        var handler = new FakeHandler(respond);
        var options = new FomaPayClientOptions { BaseUrl = "https://api.test/", HttpClient = new HttpClient(handler) };
        configure?.Invoke(options);
        var client = new FomaPayClient("sk_test_unit", options)
        {
            Delay = (wait, _) =>
            {
                _waits.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (client, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Failure(int status, string code) =>
        Json((HttpStatusCode)status, """{"error":{"code":"CODE","message":"oups","details":{"champ":"amount"}}}""".Replace("CODE", code));

    private static void AssertJsonEquals(string expected, string? actual) =>
        Assert.AreEqual(JsonSerializer.Serialize(JsonDocument.Parse(expected)), JsonSerializer.Serialize(JsonDocument.Parse(actual!)));

    [TestMethod]
    public void RejectsMalformedSecretKey()
    {
        var error = Assert.ThrowsExactly<FomaPayException>(() => new FomaPayClient("pk_test_abc"));
        Assert.AreEqual("ERROR_INVALID_API_KEY", error.Code);
    }

    [TestMethod]
    public async Task CreateSendsStandardFormatAndIdempotencyKey()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.Created, PaymentJson));

        var payment = await client.Transactions.CreateAsync(new PaymentCreateOptions
        {
            Amount = 25,
            Currency = Currencies.USD,
            OrderId = "CMD-1",
            Customer = new Customer { Name = "Jean Marc", Phone = "+243810000000" },
            PaymentModes = [PaymentModes.FomaPayDirect, PaymentModes.MobileMoney],
            CallbackUrl = "https://marchand.cd/api/callback",
            Metadata = new Dictionary<string, object?> { ["custom_field"] = "valeur" },
        });

        var call = handler.Calls[0];
        Assert.AreEqual("POST https://api.test/v1/payments", $"{call.Method} {call.Url}");
        Assert.AreEqual("Bearer sk_test_unit", call.Authorization);
        Assert.IsTrue(call.IdempotencyKey!.StartsWith("net-", StringComparison.Ordinal));
        AssertJsonEquals(
            """
            {"amount":25,"currency":"USD","orderId":"CMD-1","customer":{"name":"Jean Marc","phone":"+243810000000"},
             "paymentModes":["fomapay_direct","mobile_money"],"callbackUrl":"https://marchand.cd/api/callback",
             "metadata":{"custom_field":"valeur"}}
            """,
            call.Body);
        Assert.AreEqual("CMD-1", payment.OrderId);
        Assert.AreEqual(25m, payment.Amount);
        Assert.IsTrue(payment.IsPending);
        Assert.IsFalse(payment.IsSuccessful);
        Assert.AreEqual(1, payment.Extra!["champInconnu"].GetInt32());
    }

    [TestMethod]
    public async Task RetriesServerErrorsWithTheSameIdempotencyKey()
    {
        var (client, handler) = Make(n => n < 3 ? Failure(503, "ERROR_UNAVAILABLE") : Json(HttpStatusCode.Created, PaymentJson));

        var payment = await client.Payments.CreateAsync(new PaymentCreateOptions { Amount = 25 });

        Assert.AreEqual("pay_1", payment.Id);
        Assert.AreEqual(3, handler.Calls.Count);
        Assert.AreEqual(1, handler.Calls.Select(c => c.IdempotencyKey).Distinct().Count());
        Assert.AreEqual(2, _waits.Count);
        Assert.IsTrue(_waits[0].TotalMilliseconds is >= 250 and <= 500 && _waits[1].TotalMilliseconds is >= 500 and <= 1000);
    }

    [TestMethod]
    public async Task GivesUpAfterMaxRetriesAndDoesNotRetryClientErrors()
    {
        var (down, downHandler) = Make(_ => Failure(500, "ERROR_INTERNAL"), o => o.MaxRetries = 1);
        var internalError = await Assert.ThrowsExactlyAsync<FomaPayApiException>(() => down.Payments.RetrieveAsync("FP-1"));
        Assert.AreEqual(500, internalError.Status);
        Assert.AreEqual(2, downHandler.Calls.Count);

        var (refused, refusedHandler) = Make(_ => Failure(403, "ERROR_LEVEL_0_LIMIT_REACHED"));
        var limit = await Assert.ThrowsExactlyAsync<FomaPayApiException>(() => refused.Payments.CreateAsync(new PaymentCreateOptions { Amount = 900 }));
        Assert.AreEqual("ERROR_LEVEL_0_LIMIT_REACHED", limit.Code);
        Assert.AreEqual("amount", limit.Details["champ"].GetString());
        Assert.AreEqual(1, refusedHandler.Calls.Count);
    }

    [TestMethod]
    public async Task PayoutsAreNeverReplayedExceptOnRateLimit()
    {
        var (broken, brokenHandler) = Make(_ => Failure(502, "ERROR_BAD_GATEWAY"));
        await Assert.ThrowsExactlyAsync<FomaPayApiException>(() => broken.Payouts.CreateAsync(Payout));
        Assert.AreEqual(1, brokenHandler.Calls.Count);
        Assert.IsNull(brokenHandler.Calls[0].IdempotencyKey);
        AssertJsonEquals("""{"amount":10,"currency":"USD","operator":"mpesa","phone":"+243810000000"}""", brokenHandler.Calls[0].Body);

        var (offline, offlineHandler) = Make((_, _, _) => throw new HttpRequestException("coupure"));
        var network = await Assert.ThrowsExactlyAsync<FomaPayNetworkException>(() => offline.Payouts.CreateAsync(Payout));
        Assert.AreEqual("ERROR_NETWORK", network.Code);
        Assert.AreEqual(1, offlineHandler.Calls.Count);

        _waits.Clear();
        var (limited, limitedHandler) = Make(n =>
        {
            if (n > 1)
            {
                return Json(HttpStatusCode.Created, PaymentJson);
            }
            var response = Failure(429, "ERROR_RATE_LIMITED");
            response.Headers.TryAddWithoutValidation("Retry-After", "2");
            return response;
        });
        await limited.Payouts.CreateAsync(Payout);
        Assert.AreEqual(2, limitedHandler.Calls.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(2), _waits.Single());
    }

    [TestMethod]
    public async Task TimeoutIsReportedAndCallerCancellationIsPreserved()
    {
        var (slow, slowHandler) = Make(
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Json(HttpStatusCode.OK, "{}");
            },
            o =>
            {
                o.Timeout = TimeSpan.FromMilliseconds(50);
                o.MaxRetries = 1;
            });

        var timeout = await Assert.ThrowsExactlyAsync<FomaPayNetworkException>(() => slow.Balance.RetrieveAsync());
        Assert.AreEqual("ERROR_TIMEOUT", timeout.Code);
        Assert.AreEqual(2, slowHandler.Calls.Count);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => slow.Store.RetrieveAsync(cancelled.Token));
    }

    [TestMethod]
    public async Task OtherResources()
    {
        var (client, handler) = Make((_, request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/payments" => Json(HttpStatusCode.OK, $$"""{"data":[{{PaymentJson}}]}"""),
            "/v1/balance" => Json(HttpStatusCode.OK, """{"data":[{"currency":"USD","available":12.5}]}"""),
            "/v1/store" => Json(HttpStatusCode.OK, """{"id":"s1","name":"Chez Foma","shortCode":"FOMA","kycLevel":2,"paymentModes":["fomapay_direct"]}"""),
            _ => Json(HttpStatusCode.OK, PaymentJson.Replace("pending", "success")),
        }));

        Assert.AreEqual(1, (await client.Payments.ListAsync(5, PaymentStatuses.Success)).Count);
        Assert.AreEqual(12.5m, (await client.Balance.RetrieveAsync())[0].Available);
        var store = await client.Store.RetrieveAsync();
        Assert.AreEqual("FOMA", store.ShortCode);
        Assert.AreEqual(2, store.KycLevel);
        await client.Payments.PayWithMobileMoneyAsync("FP/1", Operators.AirtelMoney, "+243970000000");
        Assert.IsTrue((await client.Payments.SimulateAsync("pay_1")).IsSuccessful);
        Assert.IsTrue((await client.Payments.WaitForCompletionAsync("pay_1")).IsSuccessful);

        Assert.AreEqual("https://api.test/v1/payments?limit=5&status=success", handler.Calls[0].Url);
        Assert.AreEqual("https://api.test/v1/payments/FP%2F1/mobile-money", handler.Calls[3].Url);
        AssertJsonEquals("""{"outcome":"success"}""", handler.Calls[4].Body);
        Assert.IsFalse(client.Livemode);
    }
}
