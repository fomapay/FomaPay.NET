# FomaPay.NET

SDK .NET officiel de **FomaPay** : encaissez en RDC par **FomaPay Direct** (QR / code marchand, dès le Niveau 0) et par **Mobile Money** (M-Pesa, Orange Money, Airtel Money, dès le Niveau 1).

Pensé pour les **logiciels de caisse (POS)** et les applications Windows :

- `netstandard2.0` (.NET Framework 4.6.2+, WinForms / WPF) et `net8.0` (ASP.NET Core, MAUI, WinUI)
- Tout est `async`, avec `CancellationToken`
- Nouvelles tentatives automatiques et délai d'attente
- Vérification des webhooks (HMAC-SHA256)
- **Reçus ESC/POS** pour imprimantes thermiques 58 mm et 80 mm, QR code et tiroir-caisse compris

> Le SDK utilise votre clé secrète `sk_…`. Sur un poste de caisse, stockez-la chiffrée (DPAPI, coffre du système) et jamais dans le code source.

## Intégration en 3 étapes

### 1. Installer

```bash
dotnet add package FomaPay.NET
```

### 2. Encaisser

```csharp
using FomaPay;

var fomapay = new FomaPayClient("sk_test_…"); // FomaPay Business › Développeurs

var payment = await fomapay.Transactions.CreateAsync(new PaymentCreateOptions
{
    Amount = 1000,
    Currency = Currencies.CDF,
    OrderId = "CMD-2026-001",
    Customer = new Customer { Name = "Jean Marc", Phone = "+243810000000", Email = "jean@example.com" },
    PaymentModes = new[] { PaymentModes.FomaPayDirect, PaymentModes.MobileMoney },
    CallbackUrl = "https://marchand.cd/api/callback",
    Metadata = new Dictionary<string, object?> { ["custom_field"] = "valeur" },
});

// FomaPay Direct : affichez payment.QrCode à l'écran client.
// Mobile Money : envoyez la demande de débit (push USSD) sur son téléphone.
await fomapay.Transactions.PayWithMobileMoneyAsync(payment.Id, Operators.AirtelMoney, "+243970000000");

// En caisse, attendez simplement que le client valide.
payment = await fomapay.Transactions.WaitForCompletionAsync(payment.Id);
```

### 3. Confirmer : imprimer le reçu, ou recevoir le webhook

En caisse :

```csharp
using FomaPay.Pos;

if (payment.IsSuccessful)
{
    var ticket = Receipt.ForPayment(payment, "Chez Foma", new ReceiptOptions { Cashier = "Aline", OpenCashDrawer = true });
    await ReceiptPrinter.PrintAsync("192.168.1.50", ticket); // imprimante réseau, port 9100
}
```

Sur un serveur (ASP.NET Core) :

```csharp
app.MapPost("/webhooks/fomapay", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(); // le corps BRUT, tel que reçu
    try
    {
        var evt = Webhook.ConstructEvent(payload, request.Headers[Webhook.SignatureHeader], "whsec_…");
        if (evt.Type == "payment.succeeded") Livrer(evt.Data.OrderId);
        return Results.Ok();
    }
    catch (FomaPaySignatureException)
    {
        return Results.BadRequest();
    }
});
```

`Webhook.Verify(payload, header, secret)` renvoie simplement un booléen.

## Imprimantes thermiques

`Receipt.ForPayment` produit un ticket complet (en-tête, référence, client, mode, total, QR code de la référence, coupe). Pour un ticket sur mesure, utilisez `EscPosBuilder` :

```csharp
var ticket = new EscPosBuilder(PaperWidth.Mm80)
    .Align(Alignment.Center).DoubleSize().Line("Chez Foma").DoubleSize(false)
    .Align(Alignment.Left).Separator()
    .Row("2 x Pain", "3 000 CDF")
    .Row("1 x Lait", "4 500 CDF")
    .Separator()
    .Bold().Row("TOTAL", Receipt.FormatAmount(7500, Currencies.CDF)).Bold(false)
    .QrCode(payment.Reference)
    .Cut()
    .Build();
```

| Connexion | Envoi |
| --- | --- |
| Réseau (Ethernet / Wi-Fi) | `ReceiptPrinter.PrintAsync("192.168.1.50", ticket)` |
| Série, USB virtuel, Bluetooth | `ReceiptPrinter.PrintAsync(flux, ticket)` avec le flux de votre port (`SerialPort.BaseStream`, `/dev/usb/lp0`…) |

Les accents sont retirés à l'impression (« Reçu » devient « Recu ») : la page de codes varie d'une imprimante à l'autre, alors que l'ASCII s'imprime correctement partout.

## Référence

| Appel | Rôle |
| --- | --- |
| `Payments.CreateAsync(options)` | Crée une demande de paiement |
| `Payments.RetrieveAsync(idOuReference)` | Relit un paiement |
| `Payments.ListAsync(limit, status)` | Derniers paiements de la boutique |
| `Payments.PayWithMobileMoneyAsync(id, operator, phone)` | Push USSD vers le client |
| `Payments.WaitForCompletionAsync(id, pollInterval, timeout)` | Attend l'issue du paiement |
| `Payments.SimulateAsync(id, "success" \| "failed")` | Force l'issue (sandbox uniquement) |
| `Payouts.CreateAsync(options)` | Retrait vers Mobile Money |
| `Balance.RetrieveAsync()` | Solde par devise |
| `Store.RetrieveAsync()` | Boutique, niveau KYC et modes autorisés |

`Transactions` est un alias de `Payments`. Les montants sont des `decimal` en unités de la devise (`12.50m` = 12,50 USD). Le client est sûr pour un usage concurrent : créez-en un seul.

## Erreurs

```csharp
try
{
    await fomapay.Payments.CreateAsync(options);
}
catch (FomaPayApiException e)       // l'API a répondu avec une erreur
{
    Console.WriteLine($"{e.Code} ({e.Status}) : {e.Message}"); // ex. ERROR_LEVEL_0_LIMIT_REACHED
}
catch (FomaPayNetworkException e)   // ERROR_NETWORK ou ERROR_TIMEOUT : très utile en caisse, quand la connexion saute
{
}
```

Toutes héritent de `FomaPayException`. Une imprimante injoignable lève `FomaPayException` avec le code `ERROR_PRINTER_UNREACHABLE`.

## Fiabilité

```csharp
var fomapay = new FomaPayClient("sk_live_…", new FomaPayClientOptions
{
    Timeout = TimeSpan.FromSeconds(10), // 30 s par défaut
    MaxRetries = 3,                     // 2 par défaut
    HttpClient = httpClientFactory.CreateClient(),
});
```

- **Nouvelles tentatives** avec attente exponentielle (0,5 s, 1 s… plafonnée à 8 s). Ne sont rejouées que les requêtes sans risque : les lectures, et les créations de paiement, qui portent toujours un en-tête `Idempotency-Key`. Un `429` est toujours rejoué en respectant `Retry-After`.
- **Idempotence** : `IdempotencyKey = "CMD-2026-001"` pour qu'un appel répété par votre propre code (double clic du caissier, relance après coupure) renvoie le même paiement.
- **Retraits** : jamais rejoués automatiquement. Après une `FomaPayNetworkException`, vérifiez l'état avant de relancer.

## Sandbox

Avec une clé `sk_test_…`, aucun argent ne circule et le reçu porte la mention « MODE TEST ». Pour un push Mobile Money, le numéro décide de l'issue : terminé par `0001` → refusé, par `0002` → jamais validé, sinon confirmé après ~3 s.

## Tests

```bash
dotnet test
```

Tests d'intégration contre le sandbox (non exécutés si la variable est absente) :

```bash
FOMAPAY_API_URL=http://localhost:8080 FOMAPAY_TEST_SECRET_KEY=sk_test_… dotnet test
```

Licence MIT.
