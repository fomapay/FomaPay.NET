namespace FomaPay.Tests;

[TestClass]
public sealed class WebhookTests
{
    private const string Secret = "whsec_unit";
    private const long Now = 1_800_000_000;
    private const string Body =
        """{"id":"evt_1","type":"payment.succeeded","created":"2026-10-10T10:00:00Z","livemode":false,"data":{"id":"pay_1","status":"success","orderId":"CMD-1"}}""";

    private static readonly TimeSpan Tolerance = Webhook.DefaultTolerance;

    private static DateTimeOffset At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static string Sign(string body = Body, string secret = Secret, long timestamp = Now) =>
        $"t={timestamp},v1={Webhook.Sign(secret, $"{timestamp}.{body}")}";

    [TestMethod]
    public void ValidSignatureReturnsTheEvent()
    {
        var parsed = Webhook.ConstructEvent(Body, Sign(), Secret, Tolerance, At(Now));
        Assert.AreEqual("payment.succeeded", parsed.Type);
        Assert.IsTrue(parsed.Data.IsSuccessful);
        Assert.AreEqual("CMD-1", parsed.Data.OrderId);
        Assert.IsNull(Webhook.Check(Body, Sign(), Secret, Tolerance, At(Now + 299)));
    }

    [TestMethod]
    public void KnownVector()
    {
        // Vecteur commun à tous les SDKs FomaPay, calculé avec OpenSSL :
        // printf '1700000000.{"id":"evt_1"}' | openssl dgst -sha256 -hmac whsec_test
        const string digest = "c89214b5b5da833daed6f0b8c5bb6bd58cea9022bd80ccc78230f3942d632925";
        Assert.AreEqual(digest, Webhook.Sign("whsec_test", """1700000000.{"id":"evt_1"}"""));
        Assert.IsNull(Webhook.Check("""{"id":"evt_1"}""", "t=1700000000,v1=" + digest, "whsec_test", Tolerance, At(1_700_000_000)));
        Assert.IsNull(Webhook.Check("""{"id":"evt_1"}""", "t=1700000000, v1=" + digest.ToUpperInvariant(), "whsec_test", Tolerance, At(1_700_000_000)));
    }

    [TestMethod]
    [DataRow(Body + " ", null, 0L, DisplayName = "corps modifié")]
    [DataRow(Body, "autre-secret", 0L, DisplayName = "mauvais secret")]
    [DataRow(Body, null, 301L, DisplayName = "trop ancien")]
    [DataRow(Body, null, -301L, DisplayName = "dans le futur")]
    public void TamperedOrStaleEventsAreRejected(string body, string? signingSecret, long clockOffset)
    {
        var header = Sign(Body, signingSecret ?? Secret);
        Assert.IsNotNull(Webhook.Check(body, header, Secret, Tolerance, At(Now + clockOffset)));
        var error = Assert.ThrowsExactly<FomaPaySignatureException>(() => Webhook.ConstructEvent(body, header, Secret, Tolerance, At(Now + clockOffset)));
        Assert.AreEqual("ERROR_INVALID_SIGNATURE", error.Code);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("t=abc,v1=00")]
    [DataRow("t=1800000000")]
    public void MalformedHeadersAreRejected(string? header)
    {
        Assert.IsFalse(Webhook.Verify(Body, header, Secret));
        Assert.ThrowsExactly<FomaPaySignatureException>(() => Webhook.ConstructEvent(Body, header, Secret));
    }

    [TestMethod]
    public void MissingSecretIsReported()
    {
        var error = Assert.ThrowsExactly<FomaPayException>(() => Webhook.Verify(Body, Sign(), ""));
        Assert.AreEqual("ERROR_WEBHOOK_SECRET_MISSING", error.Code);
    }
}
