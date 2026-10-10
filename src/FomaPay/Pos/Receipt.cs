using System;
using System.Globalization;

namespace FomaPay.Pos
{
    /// <summary>Présentation du reçu.</summary>
    public sealed class ReceiptOptions
    {
        /// <summary>Largeur du papier.</summary>
        public PaperWidth PaperWidth { get; set; } = PaperWidth.Mm58;

        /// <summary>Lignes libres sous le nom de la boutique (adresse, téléphone, RCCM…).</summary>
        public string[] HeaderLines { get; set; } = new string[0];

        /// <summary>Message de fin de ticket.</summary>
        public string Footer { get; set; } = "Merci de votre confiance";

        /// <summary>Nom du caissier, s'il doit figurer sur le reçu.</summary>
        public string? Cashier { get; set; }

        /// <summary>Imprime la référence du paiement sous forme de QR code.</summary>
        public bool PrintQrCode { get; set; } = true;

        /// <summary>Coupe le papier à la fin du ticket.</summary>
        public bool Cut { get; set; } = true;

        /// <summary>Ouvre le tiroir-caisse après l'impression.</summary>
        public bool OpenCashDrawer { get; set; }
    }

    /// <summary>Reçu de paiement prêt à imprimer sur une imprimante thermique.</summary>
    public static class Receipt
    {
        /// <summary>Construit le reçu ESC/POS d'un paiement.</summary>
        /// <param name="payment">Le paiement à imprimer.</param>
        /// <param name="storeName">Nom de la boutique, en tête de ticket.</param>
        /// <param name="options">Présentation (papier, pied de page, QR code…).</param>
        /// <returns>Les octets à envoyer à l'imprimante (voir <see cref="ReceiptPrinter"/>).</returns>
        public static byte[] ForPayment(Payment payment, string storeName, ReceiptOptions? options = null)
        {
            if (payment == null)
            {
                throw new ArgumentNullException(nameof(payment));
            }
            options ??= new ReceiptOptions();
            var ticket = new EscPosBuilder(options.PaperWidth);

            ticket.Align(Alignment.Center).DoubleSize().Line(storeName).DoubleSize(false);
            foreach (var line in options.HeaderLines)
            {
                ticket.Line(line);
            }
            ticket.Bold().Line("RECU DE PAIEMENT").Bold(false);
            if (!payment.Livemode)
            {
                ticket.Line("*** MODE TEST - SANS VALEUR ***");
            }

            ticket.Align(Alignment.Left).Separator();
            ticket.Row("Reference", payment.Reference);
            ticket.Row("Date", FormatDate(payment.CompletedAt ?? payment.CreatedAt));
            if (!string.IsNullOrEmpty(payment.OrderId))
            {
                ticket.Row("Commande", payment.OrderId!);
            }
            if (!string.IsNullOrEmpty(payment.Customer?.Name))
            {
                ticket.Row("Client", payment.Customer!.Name!);
            }
            if (!string.IsNullOrEmpty(options.Cashier))
            {
                ticket.Row("Caissier", options.Cashier!);
            }
            ticket.Row("Mode", ChannelLabel(payment.Channel));
            ticket.Row("Statut", StatusLabel(payment.Status));

            ticket.Separator();
            ticket.Bold().Row("TOTAL", FormatAmount(payment.Amount, payment.Currency)).Bold(false);
            ticket.Separator();

            ticket.Align(Alignment.Center);
            if (options.PrintQrCode && !string.IsNullOrEmpty(payment.Reference))
            {
                ticket.Feed().QrCode(payment.Reference).Feed();
            }
            ticket.Line(options.Footer).Line("Paiement securise par FomaPay");

            if (options.Cut)
            {
                ticket.Cut();
            }
            else
            {
                ticket.Feed(3);
            }
            if (options.OpenCashDrawer)
            {
                ticket.OpenCashDrawer();
            }
            return ticket.Build();
        }

        /// <summary>« 1 000 CDF », « 12.50 USD » : séparateur de milliers ASCII, lisible sur toutes les imprimantes.</summary>
        public static string FormatAmount(decimal amount, string currency)
        {
            var format = currency == Currencies.CDF && amount == decimal.Truncate(amount) ? "N0" : "N2";
            return amount.ToString(format, CultureInfo.InvariantCulture).Replace(',', ' ') + " " + currency;
        }

        private static string FormatDate(string? iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        private static string ChannelLabel(string? channel)
        {
            switch (channel)
            {
                case Operators.MPesa: return "M-Pesa";
                case Operators.OrangeMoney: return "Orange Money";
                case Operators.AirtelMoney: return "Airtel Money";
                case PaymentModes.FomaPayDirect: return "FomaPay Direct";
                case null: case "": return "-";
                default: return channel!;
            }
        }

        private static string StatusLabel(string status)
        {
            switch (status)
            {
                case PaymentStatuses.Success: return "PAYE";
                case PaymentStatuses.Failed: return "ECHEC";
                case PaymentStatuses.Expired: return "EXPIRE";
                default: return "EN ATTENTE";
            }
        }
    }
}
