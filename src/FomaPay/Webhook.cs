using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FomaPay
{
    /// <summary>Vérification des webhooks FomaPay (HMAC-SHA256).</summary>
    public static class Webhook
    {
        /// <summary>En-tête HTTP qui porte la signature.</summary>
        public const string SignatureHeader = "X-FomaPay-Signature";

        /// <summary>Âge maximal accepté pour un événement (anti-rejeu).</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

        /// <summary>Indique si la signature est valide pour ce corps brut.</summary>
        /// <param name="payload">Corps BRUT de la requête, tel que reçu.</param>
        /// <param name="header">Valeur de l'en-tête <c>X-FomaPay-Signature</c>.</param>
        /// <param name="secret">Secret de signature de l'endpoint (whsec_…).</param>
        /// <param name="tolerance">Âge maximal de l'événement (5 minutes par défaut).</param>
        public static bool Verify(string payload, string? header, string secret, TimeSpan? tolerance = null) =>
            Check(payload, header, secret, tolerance ?? DefaultTolerance, DateTimeOffset.UtcNow) == null;

        /// <summary>Vérifie la signature puis renvoie l'événement.</summary>
        /// <exception cref="FomaPaySignatureException">Signature invalide ou expirée.</exception>
        public static WebhookEvent ConstructEvent(string payload, string? header, string secret, TimeSpan? tolerance = null) =>
            ConstructEvent(payload, header, secret, tolerance ?? DefaultTolerance, DateTimeOffset.UtcNow);

        internal static WebhookEvent ConstructEvent(string payload, string? header, string secret, TimeSpan tolerance, DateTimeOffset now)
        {
            var failure = Check(payload, header, secret, tolerance, now);
            if (failure != null)
            {
                throw new FomaPaySignatureException("ERROR_INVALID_SIGNATURE", failure);
            }
            try
            {
                var parsed = JsonSerializer.Deserialize<WebhookEvent>(payload, FomaPayClient.Json);
                if (parsed != null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // traité ci-dessous
            }
            throw new FomaPaySignatureException("ERROR_INVALID_PAYLOAD", "Le corps du webhook n'est pas un événement FomaPay valide.");
        }

        /// <summary>Renvoie null si la signature est valide, sinon la raison du refus.</summary>
        internal static string? Check(string payload, string? header, string secret, TimeSpan tolerance, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new FomaPayException("ERROR_WEBHOOK_SECRET_MISSING", "Le secret de signature du webhook (whsec_…) est requis.");
            }

            string timestamp = "", signature = "";
            foreach (var part in (header ?? "").Split(','))
            {
                var pair = part.Trim().Split(new[] { '=' }, 2);
                if (pair.Length == 2 && pair[0] == "t")
                {
                    timestamp = pair[1];
                }
                else if (pair.Length == 2 && pair[0] == "v1")
                {
                    signature = pair[1];
                }
            }
            if (payload == null || signature.Length == 0
                || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return "En-tête de signature absent ou mal formé.";
            }

            if (!FixedTimeEquals(Sign(secret, timestamp + "." + payload), signature.ToLowerInvariant()))
            {
                return "La signature ne correspond pas au contenu reçu.";
            }

            // L'horodatage n'est contrôlé qu'après la signature : il est alors authentique.
            return Math.Abs(now.ToUnixTimeSeconds() - seconds) > tolerance.TotalSeconds ? "Événement trop ancien : rejeu refusé." : null;
        }

        /// <summary>HMAC-SHA256 en hexadécimal minuscule.</summary>
        internal static string Sign(string secret, string message)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
        }

        /// <summary>Comparaison en temps constant (indépendante de la position de la première différence).</summary>
        private static bool FixedTimeEquals(string expected, string given)
        {
            var diff = expected.Length ^ given.Length;
            for (var i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ (i < given.Length ? given[i] : '\0');
            }
            return diff == 0;
        }
    }
}
