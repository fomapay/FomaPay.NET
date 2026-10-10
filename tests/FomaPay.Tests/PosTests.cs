using System.Net;
using System.Net.Sockets;
using System.Text;
using FomaPay.Pos;

namespace FomaPay.Tests;

[TestClass]
public sealed class PosTests
{
    private static readonly Payment Paid = new()
    {
        Reference = "FP-ABCDEFGHJK",
        Status = PaymentStatuses.Success,
        Amount = 15000,
        Currency = Currencies.CDF,
        Channel = Operators.AirtelMoney,
        OrderId = "CMD-2026-001",
        Customer = new Customer { Name = "Hélène Mbóyo" },
        Livemode = true,
        CompletedAt = "2026-10-10T10:00:00Z",
    };

    /// <summary>Ligne « libellé … valeur » telle que le ticket doit la contenir.</summary>
    private static string Row(string label, string value, int columns = 32) => label + value.PadLeft(columns - label.Length) + '\n';

    private static string Text(byte[] ticket) => Encoding.ASCII.GetString(ticket);

    private static bool Contains(byte[] haystack, params byte[] needle) =>
        Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));

    [TestMethod]
    public void ReceiptContainsThePaymentAndTheEscPosCommands()
    {
        var ticket = Receipt.ForPayment(Paid, "Chez Foma", new ReceiptOptions { Cashier = "Aline", OpenCashDrawer = true });
        var text = Text(ticket);

        Assert.IsTrue(ticket.AsSpan(0, 2).SequenceEqual(new byte[] { 0x1B, 0x40 }), "le ticket commence par ESC @");
        StringAssert.Contains(text, "Chez Foma");
        StringAssert.Contains(text, "RECU DE PAIEMENT");
        StringAssert.Contains(text, Row("Reference", "FP-ABCDEFGHJK"));
        StringAssert.Contains(text, Row("Client", "Helene Mboyo"));
        StringAssert.Contains(text, Row("Mode", "Airtel Money"));
        StringAssert.Contains(text, Row("TOTAL", "15 000 CDF"));
        StringAssert.Contains(text, "PAYE");
        Assert.IsFalse(text.Contains("MODE TEST"));
        Assert.IsTrue(Contains(ticket, 0x1D, 0x28, 0x6B, 16, 0, 0x31, 0x50, 0x30), "QR code : 13 octets de référence + 3");
        Assert.IsTrue(Contains(ticket, 0x1D, 0x56, 0x42, 0x00), "coupe du papier");
        Assert.IsTrue(Contains(ticket, 0x1B, 0x70, 0x00), "ouverture du tiroir-caisse");
    }

    [TestMethod]
    public void SandboxReceiptIsMarkedAndOptionsAreHonoured()
    {
        var pending = new Payment { Reference = "FP-1", Status = PaymentStatuses.Pending, Amount = 12.5m, Currency = Currencies.USD };
        var ticket = Receipt.ForPayment(pending, "Boutique", new ReceiptOptions { PaperWidth = PaperWidth.Mm80, PrintQrCode = false, Cut = false });
        var text = Text(ticket);

        StringAssert.Contains(text, "MODE TEST");
        StringAssert.Contains(text, "EN ATTENTE");
        StringAssert.Contains(text, Row("TOTAL", "12.50 USD", 48));
        Assert.IsFalse(Contains(ticket, 0x1D, 0x28, 0x6B));
        Assert.IsFalse(Contains(ticket, 0x1D, 0x56));
    }

    [TestMethod]
    public void BuilderWrapsLongRowsAndStripsAccents()
    {
        var builder = new EscPosBuilder();
        var text = Text(builder.Row("Une étiquette vraiment très longue", "valeur").Separator('=').Build());

        Assert.AreEqual(32, builder.Columns);
        StringAssert.Contains(text, "Une etiquette vraiment tres longue\n" + "valeur".PadLeft(32) + "\n");
        StringAssert.Contains(text, new string('=', 32));
        Assert.AreEqual("Recu n? 5 ? oeuf.", EscPosBuilder.ToAscii("Reçu n° 5 — œuf…"));
        Assert.AreEqual("1 234 567.50 USD", Receipt.FormatAmount(1234567.5m, Currencies.USD));
        Assert.AreEqual("500 CDF", Receipt.FormatAmount(500m, Currencies.CDF));
    }

    [TestMethod]
    public async Task PrinterSendsTheTicketOverTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ticket = new EscPosBuilder().Line("Bonjour").Cut().Build();

        var received = Task.Run(async () =>
        {
            using var printer = await listener.AcceptTcpClientAsync();
            using var copy = new MemoryStream();
            await printer.GetStream().CopyToAsync(copy);
            return copy.ToArray();
        });
        await ReceiptPrinter.PrintAsync("127.0.0.1", ticket, port);

        CollectionAssert.AreEqual(ticket, await received);
        listener.Stop();

        var error = await Assert.ThrowsExactlyAsync<FomaPayException>(() => ReceiptPrinter.PrintAsync("127.0.0.1", ticket, port, TimeSpan.FromSeconds(2)));
        Assert.AreEqual("ERROR_PRINTER_UNREACHABLE", error.Code);
    }
}
