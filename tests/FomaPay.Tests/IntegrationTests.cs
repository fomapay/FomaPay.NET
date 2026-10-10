namespace FomaPay.Tests;

/// <summary>
/// Tests d'intégration contre le sandbox FomaPay. Ils ne s'exécutent que si FOMAPAY_TEST_SECRET_KEY (sk_test_…)
/// est définie ; FOMAPAY_API_URL cible un backend local :
/// <code>FOMAPAY_API_URL=http://localhost:8080 FOMAPAY_TEST_SECRET_KEY=sk_test_… dotnet test</code>
/// </summary>
[TestClass]
public sealed class IntegrationTests
{
    [TestMethod]
    public async Task Sandbox()
    {
        var key = Environment.GetEnvironmentVariable("FOMAPAY_TEST_SECRET_KEY");
        if (string.IsNullOrEmpty(key))
        {
            Assert.Inconclusive("FOMAPAY_TEST_SECRET_KEY non définie");
        }
        var options = new FomaPayClientOptions();
        if (Environment.GetEnvironmentVariable("FOMAPAY_API_URL") is { Length: > 0 } baseUrl)
        {
            options.BaseUrl = baseUrl;
        }
        using var fomapay = new FomaPayClient(key!, options);

        var store = await fomapay.Store.RetrieveAsync();
        Assert.IsFalse(store.Livemode);
        Assert.AreEqual(2, store.PaymentModes.Count);

        // Format standard d'une transaction, dans les deux modes.
        var payment = await fomapay.Payments.CreateAsync(new PaymentCreateOptions
        {
            Amount = 1000,
            Currency = Currencies.CDF,
            OrderId = "CMD-2026-001",
            Customer = new Customer { Name = "Jean Marc", Phone = "+243810000000", Email = "jean@example.com" },
            PaymentModes = [PaymentModes.FomaPayDirect, PaymentModes.MobileMoney],
            CallbackUrl = "https://marchand.cd/api/callback",
            Metadata = new Dictionary<string, object?> { ["custom_field"] = "valeur" },
        });
        Assert.AreEqual(PaymentStatuses.Pending, payment.Status);
        Assert.AreEqual("https://marchand.cd/api/callback", payment.CallbackUrl);
        Assert.AreEqual("valeur", payment.Metadata!["custom_field"].GetString());
        Assert.IsFalse(string.IsNullOrEmpty(payment.QrCode));

        // Mode Mobile Money : l'opérateur simulé confirme après ~3 s.
        var processing = await fomapay.Payments.PayWithMobileMoneyAsync(payment.Id, Operators.AirtelMoney, "+243970000000");
        Assert.AreEqual(PaymentStatuses.Processing, processing.Status);
        var final = await fomapay.Payments.WaitForCompletionAsync(payment.Reference, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.IsTrue(final.IsSuccessful);
        Assert.AreEqual(Operators.AirtelMoney, final.Channel);

        // Mode FomaPay Direct : issue forcée en sandbox.
        var direct = await fomapay.Payments.CreateAsync(
            new PaymentCreateOptions { Amount = 12.5m, Currency = Currencies.USD, PaymentModes = [PaymentModes.FomaPayDirect] });
        Assert.IsTrue((await fomapay.Payments.SimulateAsync(direct.Id)).IsSuccessful);

        // Idempotence : la même clé renvoie le même paiement.
        var same = new PaymentCreateOptions { Amount = 3, Currency = Currencies.USD, IdempotencyKey = "net-it-" + payment.Id };
        Assert.AreEqual((await fomapay.Payments.CreateAsync(same)).Id, (await fomapay.Payments.CreateAsync(same)).Id);

        Assert.IsTrue((await fomapay.Payments.ListAsync(5, PaymentStatuses.Success)).Count > 0);
        Assert.IsTrue((await fomapay.Balance.RetrieveAsync()).Count > 0);

        var invalid = await Assert.ThrowsExactlyAsync<FomaPayApiException>(
            () => fomapay.Payments.CreateAsync(new PaymentCreateOptions { Amount = -1, Currency = Currencies.USD }));
        Assert.AreEqual("ERROR_VALIDATION", invalid.Code);
        var missing = await Assert.ThrowsExactlyAsync<FomaPayApiException>(() => fomapay.Payments.RetrieveAsync("FP-INEXISTANT"));
        Assert.AreEqual("ERROR_NOT_FOUND", missing.Code);
        Assert.AreEqual(404, missing.Status);
    }
}
