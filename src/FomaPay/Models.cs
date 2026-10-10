using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FomaPay
{
    /// <summary>Devises acceptées.</summary>
    public static class Currencies
    {
        /// <summary>Dollar américain.</summary>
        public const string USD = "USD";

        /// <summary>Franc congolais.</summary>
        public const string CDF = "CDF";
    }

    /// <summary>Modes de paiement proposés au client.</summary>
    public static class PaymentModes
    {
        /// <summary>Portefeuille FomaPay (QR / code marchand). Disponible dès le Niveau 0.</summary>
        public const string FomaPayDirect = "fomapay_direct";

        /// <summary>M-Pesa, Orange Money, Airtel Money. Réservé aux boutiques vérifiées (Niveau 1+).</summary>
        public const string MobileMoney = "mobile_money";
    }

    /// <summary>Opérateurs Mobile Money.</summary>
    public static class Operators
    {
        /// <summary>M-Pesa (Vodacom).</summary>
        public const string MPesa = "mpesa";

        /// <summary>Orange Money.</summary>
        public const string OrangeMoney = "orange_money";

        /// <summary>Airtel Money.</summary>
        public const string AirtelMoney = "airtel_money";
    }

    /// <summary>Statuts d'un paiement.</summary>
    public static class PaymentStatuses
    {
        /// <summary>En attente du client.</summary>
        public const string Pending = "pending";

        /// <summary>En cours chez l'opérateur.</summary>
        public const string Processing = "processing";

        /// <summary>Payé.</summary>
        public const string Success = "success";

        /// <summary>Refusé ou échoué.</summary>
        public const string Failed = "failed";

        /// <summary>Expiré sans paiement.</summary>
        public const string Expired = "expired";
    }

    /// <summary>Client final d'un paiement.</summary>
    public sealed class Customer
    {
        /// <summary>Nom affiché.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>Téléphone au format international (+243…).</summary>
        [JsonPropertyName("phone")]
        public string? Phone { get; set; }

        /// <summary>Adresse e-mail.</summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    /// <summary>Paramètres de création d'un paiement.</summary>
    public sealed class PaymentCreateOptions
    {
        /// <summary>Montant en unités de la devise (12.50 = 12,50 USD).</summary>
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        /// <summary>Devise : <see cref="Currencies.USD"/> ou <see cref="Currencies.CDF"/>.</summary>
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = Currencies.USD;

        /// <summary>Votre identifiant de commande, renvoyé tel quel dans les webhooks.</summary>
        [JsonPropertyName("orderId")]
        public string? OrderId { get; set; }

        /// <summary>Libellé affiché au client.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Client final.</summary>
        [JsonPropertyName("customer")]
        public Customer? Customer { get; set; }

        /// <summary>Modes proposés ; par défaut, tous ceux que le niveau KYC de la boutique autorise.</summary>
        [JsonPropertyName("paymentModes")]
        public IList<string>? PaymentModes { get; set; }

        /// <summary>Page de votre site vers laquelle renvoyer le client une fois le paiement terminé.</summary>
        [JsonPropertyName("callbackUrl")]
        public string? CallbackUrl { get; set; }

        /// <summary>Données libres, renvoyées telles quelles.</summary>
        [JsonPropertyName("metadata")]
        public IDictionary<string, object?>? Metadata { get; set; }

        /// <summary>Ex. l'identifiant de commande : un appel répété renvoie le même paiement.</summary>
        [JsonIgnore]
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>Paramètres d'un retrait du solde de la boutique vers un compte Mobile Money.</summary>
    public sealed class PayoutCreateOptions
    {
        /// <summary>Montant en unités de la devise.</summary>
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        /// <summary>Devise du retrait.</summary>
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = Currencies.USD;

        /// <summary>Opérateur du bénéficiaire (voir <see cref="Operators"/>).</summary>
        [JsonPropertyName("operator")]
        public string Operator { get; set; } = "";

        /// <summary>Numéro bénéficiaire au format international (+243…).</summary>
        [JsonPropertyName("phone")]
        public string Phone { get; set; } = "";

        /// <summary>Libellé du retrait.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>Un paiement, un retrait ou tout autre mouvement.</summary>
    public sealed class Payment
    {
        /// <summary>Identifiant.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Référence lisible (FP-…), acceptée partout à la place de l'identifiant.</summary>
        [JsonPropertyName("reference")]
        public string Reference { get; set; } = "";

        /// <summary>Statut (voir <see cref="PaymentStatuses"/>).</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        /// <summary>Montant payé par le client.</summary>
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        /// <summary>Frais FomaPay.</summary>
        [JsonPropertyName("fee")]
        public decimal Fee { get; set; }

        /// <summary>Montant net crédité à la boutique.</summary>
        [JsonPropertyName("net")]
        public decimal Net { get; set; }

        /// <summary>Devise.</summary>
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "";

        /// <summary>Canal réellement utilisé (fomapay_direct, mpesa…).</summary>
        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        /// <summary>Votre identifiant de commande.</summary>
        [JsonPropertyName("orderId")]
        public string? OrderId { get; set; }

        /// <summary>Libellé.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Client final.</summary>
        [JsonPropertyName("customer")]
        public Customer? Customer { get; set; }

        /// <summary>Données libres transmises à la création.</summary>
        [JsonPropertyName("metadata")]
        public Dictionary<string, JsonElement>? Metadata { get; set; }

        /// <summary>Modes proposés au client.</summary>
        [JsonPropertyName("paymentModes")]
        public List<string> PaymentModes { get; set; } = new List<string>();

        /// <summary>Page de retour du marchand.</summary>
        [JsonPropertyName("callbackUrl")]
        public string? CallbackUrl { get; set; }

        /// <summary>Téléphone du payeur (Mobile Money).</summary>
        [JsonPropertyName("payerPhone")]
        public string? PayerPhone { get; set; }

        /// <summary>Contenu du QR code à afficher au client (FomaPay Direct).</summary>
        [JsonPropertyName("qrCode")]
        public string? QrCode { get; set; }

        /// <summary>Faux en sandbox.</summary>
        [JsonPropertyName("livemode")]
        public bool Livemode { get; set; }

        /// <summary>Boutique bénéficiaire.</summary>
        [JsonPropertyName("storeId")]
        public string? StoreId { get; set; }

        /// <summary>Code de l'échec, le cas échéant.</summary>
        [JsonPropertyName("failureCode")]
        public string? FailureCode { get; set; }

        /// <summary>Raison lisible de l'échec.</summary>
        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }

        /// <summary>Date d'expiration (ISO 8601).</summary>
        [JsonPropertyName("expiresAt")]
        public string? ExpiresAt { get; set; }

        /// <summary>Date de fin (ISO 8601).</summary>
        [JsonPropertyName("completedAt")]
        public string? CompletedAt { get; set; }

        /// <summary>Date de création (ISO 8601).</summary>
        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        /// <summary>Champs que cette version du SDK ne connaît pas encore.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }

        /// <summary>Le paiement est confirmé.</summary>
        [JsonIgnore]
        public bool IsSuccessful => Status == PaymentStatuses.Success;

        /// <summary>Le paiement attend encore le client ou l'opérateur.</summary>
        [JsonIgnore]
        public bool IsPending => Status == PaymentStatuses.Pending || Status == PaymentStatuses.Processing;
    }

    /// <summary>Solde disponible de la boutique dans une devise.</summary>
    public sealed class Balance
    {
        /// <summary>Devise.</summary>
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "";

        /// <summary>Montant disponible.</summary>
        [JsonPropertyName("available")]
        public decimal Available { get; set; }
    }

    /// <summary>Boutique liée à la clé, avec les modes que son niveau KYC autorise.</summary>
    public sealed class StoreInfo
    {
        /// <summary>Identifiant.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Nom commercial.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>Code marchand court.</summary>
        [JsonPropertyName("shortCode")]
        public string ShortCode { get; set; } = "";

        /// <summary>Logo.</summary>
        [JsonPropertyName("logoUrl")]
        public string? LogoUrl { get; set; }

        /// <summary>Faux en sandbox.</summary>
        [JsonPropertyName("livemode")]
        public bool Livemode { get; set; }

        /// <summary>Niveau KYC (0 à 4).</summary>
        [JsonPropertyName("kycLevel")]
        public int KycLevel { get; set; }

        /// <summary>Modes de paiement autorisés.</summary>
        [JsonPropertyName("paymentModes")]
        public List<string> PaymentModes { get; set; } = new List<string>();
    }

    /// <summary>Événement de webhook : <c>payment.succeeded</c>, <c>payment.failed</c>, <c>payout.succeeded</c>…</summary>
    public sealed class WebhookEvent
    {
        /// <summary>Identifiant de l'événement.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Type de l'événement.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        /// <summary>Date d'émission (ISO 8601).</summary>
        [JsonPropertyName("created")]
        public string Created { get; set; } = "";

        /// <summary>Faux en sandbox.</summary>
        [JsonPropertyName("livemode")]
        public bool Livemode { get; set; }

        /// <summary>Le paiement (ou retrait) concerné.</summary>
        [JsonPropertyName("data")]
        public Payment Data { get; set; } = new Payment();
    }
}
