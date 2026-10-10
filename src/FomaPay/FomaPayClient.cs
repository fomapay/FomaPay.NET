using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FomaPay
{
    /// <summary>Réglages du client.</summary>
    public sealed class FomaPayClientOptions
    {
        /// <summary>À surcharger pour pointer vers un backend local en développement.</summary>
        public string BaseUrl { get; set; } = FomaPayClient.DefaultBaseUrl;

        /// <summary>Délai maximal d'une requête (30 s par défaut).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Nouvelles tentatives après un échec rejouable (2 par défaut, 0 pour désactiver).</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Client HTTP à réutiliser (IHttpClientFactory, proxy…). Il n'est pas libéré par le SDK.</summary>
        public HttpClient? HttpClient { get; set; }
    }

    /// <summary>
    /// Client serveur FomaPay. Sûr pour un usage concurrent : créez-en un seul et partagez-le.
    /// </summary>
    /// <example>
    /// <code>
    /// var fomapay = new FomaPayClient("sk_test_…");
    /// var payment = await fomapay.Payments.CreateAsync(new PaymentCreateOptions { Amount = 25, Currency = Currencies.USD, OrderId = "CMD-8921" });
    /// </code>
    /// </example>
    public sealed class FomaPayClient : IDisposable
    {
        /// <summary>Version du SDK.</summary>
        public const string Version = "0.1.0";

        /// <summary>Adresse de l'API de production.</summary>
        public const string DefaultBaseUrl = "https://api.fomapay.cd";

        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);
        private static readonly Random Jitter = new Random();

        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _secretKey;
        private readonly string _baseUrl;
        private readonly TimeSpan _timeout;
        private readonly int _maxRetries;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        /// <summary>Attente entre deux tentatives ; remplaçable dans les tests.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        /// <param name="secretKey">Clé secrète de la boutique (sk_test_… ou sk_live_…).</param>
        /// <param name="options">Réglages facultatifs.</param>
        public FomaPayClient(string secretKey, FomaPayClientOptions? options = null)
        {
            if (secretKey == null || !(secretKey.StartsWith("sk_test_", StringComparison.Ordinal) || secretKey.StartsWith("sk_live_", StringComparison.Ordinal)))
            {
                throw new FomaPayException(
                    "ERROR_INVALID_API_KEY",
                    "Clé secrète absente ou mal formée : elle commence par sk_test_ ou sk_live_ (FomaPay Business › Développeurs).");
            }

            options ??= new FomaPayClientOptions();
            _secretKey = secretKey;
            _baseUrl = options.BaseUrl.TrimEnd('/');
            _timeout = options.Timeout;
            _maxRetries = Math.Max(0, options.MaxRetries);
            _ownsHttp = options.HttpClient == null;
            // Le délai est géré requête par requête : celui du HttpClient est neutralisé.
            _http = options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            Payments = new PaymentService(this);
            Payouts = new PayoutService(this);
            Balance = new BalanceService(this);
            Store = new StoreService(this);
        }

        /// <summary>Paiements de la boutique.</summary>
        public PaymentService Payments { get; }

        /// <summary>Alias de <see cref="Payments"/>.</summary>
        public PaymentService Transactions => Payments;

        /// <summary>Retraits vers Mobile Money.</summary>
        public PayoutService Payouts { get; }

        /// <summary>Solde de la boutique.</summary>
        public BalanceService Balance { get; }

        /// <summary>Boutique liée à la clé.</summary>
        public StoreService Store { get; }

        /// <summary>Vrai avec une clé <c>sk_live_…</c>.</summary>
        public bool Livemode => _secretKey.StartsWith("sk_live_", StringComparison.Ordinal);

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }

        /// <summary>Clé unique : rejouer la requête ne crée pas de doublon.</summary>
        internal static string NewIdempotencyKey() => "net-" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Exécute une requête, avec nouvelles tentatives quand elle est rejouable sans risque
        /// (lecture, ou POST accompagné d'une clé d'idempotence).
        /// </summary>
        internal async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken cancellationToken)
        {
            var payload = body == null ? null : JsonSerializer.Serialize(body, body.GetType(), Json);
            var replayable = method == HttpMethod.Get || idempotencyKey != null;

            for (var attempt = 0; ; attempt++)
            {
                FomaPayException error;
                bool retry;
                TimeSpan? wait = null;

                using (var request = new HttpRequestMessage(method, _baseUrl + path))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secretKey);
                    request.Headers.Accept.ParseAdd("application/json");
                    request.Headers.UserAgent.ParseAdd("fomapay-dotnet/" + Version);
                    if (idempotencyKey != null)
                    {
                        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                    }
                    if (payload != null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }
                    timeout.CancelAfter(_timeout);

                    try
                    {
                        using (var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var status = (int)response.StatusCode;
                            if (response.IsSuccessStatusCode)
                            {
                                return Deserialize<T>(text, status);
                            }

                            error = ApiError(status, text);
                            if (status == 429)
                            {
                                // La gateway a refusé la requête avant tout traitement : toujours rejouable.
                                retry = true;
                                var retryAfter = response.Headers.RetryAfter?.Delta;
                                if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
                                {
                                    wait = retryAfter.Value < MaxBackoff ? retryAfter.Value : MaxBackoff;
                                }
                            }
                            else
                            {
                                retry = status >= 500 && replayable;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw; // annulation demandée par l'appelant
                    }
                    catch (OperationCanceledException e)
                    {
                        error = new FomaPayNetworkException("ERROR_TIMEOUT", "FomaPay n'a pas répondu dans le délai imparti.", e);
                        retry = replayable;
                    }
                    catch (HttpRequestException e)
                    {
                        error = new FomaPayNetworkException("ERROR_NETWORK", "Impossible de joindre FomaPay : " + e.Message, e);
                        retry = replayable;
                    }
                }

                if (!retry || attempt >= _maxRetries)
                {
                    throw error;
                }
                await Delay(wait ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>0,5 s, 1 s, 2 s… plafonné à 8 s, avec un aléa pour désynchroniser les clients.</summary>
        private static TimeSpan Backoff(int attempt)
        {
            var delay = Math.Min(500 * Math.Pow(2, attempt), MaxBackoff.TotalMilliseconds);
            double random;
            lock (Jitter)
            {
                random = Jitter.NextDouble();
            }
            return TimeSpan.FromMilliseconds(delay / 2 + random * (delay / 2));
        }

        private static T Deserialize<T>(string text, int status)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(text, Json);
                if (value != null)
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // traité ci-dessous
            }
            throw new FomaPayApiException("ERROR_INVALID_RESPONSE", "Réponse illisible de l'API FomaPay.", status);
        }

        private static FomaPayApiException ApiError(int status, string text)
        {
            string? code = null;
            string? message = null;
            Dictionary<string, JsonElement>? details = null;
            try
            {
                using (var document = JsonDocument.Parse(text))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                        if (error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                        {
                            details = new Dictionary<string, JsonElement>();
                            foreach (var property in d.EnumerateObject())
                            {
                                details[property.Name] = property.Value.Clone();
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // corps non JSON (proxy, page d'erreur…) : on garde le statut HTTP
            }
            return new FomaPayApiException(code ?? "ERROR_UNKNOWN", message ?? "Erreur HTTP " + status, status, details);
        }
    }

    internal sealed class ListEnvelope<T>
    {
        [JsonPropertyName("data")]
        public List<T> Data { get; set; } = new List<T>();
    }

    /// <summary>Paiements de la boutique.</summary>
    public sealed class PaymentService
    {
        private readonly FomaPayClient _client;

        internal PaymentService(FomaPayClient client) => _client = client;

        /// <summary>Crée une demande de paiement. Porte toujours une clé d'idempotence : rejouable sans doublon.</summary>
        public Task<Payment> CreateAsync(PaymentCreateOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            return _client.RequestAsync<Payment>(
                HttpMethod.Post, "/v1/payments", options, options.IdempotencyKey ?? FomaPayClient.NewIdempotencyKey(), cancellationToken);
        }

        /// <summary>Relit un paiement par son identifiant ou sa référence (FP-…).</summary>
        public Task<Payment> RetrieveAsync(string id, CancellationToken cancellationToken = default) =>
            _client.RequestAsync<Payment>(HttpMethod.Get, "/v1/payments/" + Uri.EscapeDataString(id), null, null, cancellationToken);

        /// <summary>Les derniers paiements de la boutique.</summary>
        /// <param name="limit">Nombre maximal de résultats.</param>
        /// <param name="status">Filtre sur le statut (voir <see cref="PaymentStatuses"/>).</param>
        /// <param name="cancellationToken">Annulation.</param>
        public async Task<IReadOnlyList<Payment>> ListAsync(int? limit = null, string? status = null, CancellationToken cancellationToken = default)
        {
            var query = new List<string>();
            if (limit.HasValue)
            {
                query.Add("limit=" + limit.Value);
            }
            if (status != null)
            {
                query.Add("status=" + Uri.EscapeDataString(status));
            }
            var path = "/v1/payments" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
            return (await _client.RequestAsync<ListEnvelope<Payment>>(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false)).Data;
        }

        /// <summary>Envoie la demande de débit (push USSD) sur le téléphone du client.</summary>
        public Task<Payment> PayWithMobileMoneyAsync(string id, string @operator, string phone, CancellationToken cancellationToken = default) =>
            _client.RequestAsync<Payment>(
                HttpMethod.Post,
                "/v1/payments/" + Uri.EscapeDataString(id) + "/mobile-money",
                new Dictionary<string, string> { ["operator"] = @operator, ["phone"] = phone },
                null,
                cancellationToken);

        /// <summary>Force l'issue d'un paiement (<c>success</c> ou <c>failed</c>). Sandbox uniquement.</summary>
        public Task<Payment> SimulateAsync(string id, string outcome = "success", CancellationToken cancellationToken = default) =>
            _client.RequestAsync<Payment>(
                HttpMethod.Post,
                "/v1/payments/" + Uri.EscapeDataString(id) + "/simulate",
                new Dictionary<string, string> { ["outcome"] = outcome },
                null,
                cancellationToken);

        /// <summary>
        /// Attend l'issue d'un paiement (succès, échec ou expiration) en l'interrogeant régulièrement.
        /// Pratique en caisse, où l'écran reste ouvert pendant que le client valide sur son téléphone.
        /// </summary>
        public async Task<Payment> WaitForCompletionAsync(
            string id, TimeSpan? pollInterval = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
            while (true)
            {
                var payment = await RetrieveAsync(id, cancellationToken).ConfigureAwait(false);
                if (!payment.IsPending)
                {
                    return payment;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new FomaPayException("ERROR_PAYMENT_TIMEOUT", "Le paiement n'a pas été confirmé à temps.");
                }
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Retraits vers Mobile Money.</summary>
    public sealed class PayoutService
    {
        private readonly FomaPayClient _client;

        internal PayoutService(FomaPayClient client) => _client = client;

        /// <summary>
        /// Retrait du solde de la boutique vers un compte Mobile Money.
        /// Jamais rejoué automatiquement après une erreur réseau : un retrait ne doit pas partir deux fois.
        /// </summary>
        public Task<Payment> CreateAsync(PayoutCreateOptions options, CancellationToken cancellationToken = default) =>
            _client.RequestAsync<Payment>(HttpMethod.Post, "/v1/payouts", options ?? throw new ArgumentNullException(nameof(options)), null, cancellationToken);
    }

    /// <summary>Solde de la boutique.</summary>
    public sealed class BalanceService
    {
        private readonly FomaPayClient _client;

        internal BalanceService(FomaPayClient client) => _client = client;

        /// <summary>Un solde par devise.</summary>
        public async Task<IReadOnlyList<Balance>> RetrieveAsync(CancellationToken cancellationToken = default) =>
            (await _client.RequestAsync<ListEnvelope<Balance>>(HttpMethod.Get, "/v1/balance", null, null, cancellationToken).ConfigureAwait(false)).Data;
    }

    /// <summary>Boutique liée à la clé.</summary>
    public sealed class StoreService
    {
        private readonly FomaPayClient _client;

        internal StoreService(FomaPayClient client) => _client = client;

        /// <summary>Boutique, niveau KYC et modes de paiement autorisés.</summary>
        public Task<StoreInfo> RetrieveAsync(CancellationToken cancellationToken = default) =>
            _client.RequestAsync<StoreInfo>(HttpMethod.Get, "/v1/store", null, null, cancellationToken);
    }
}
