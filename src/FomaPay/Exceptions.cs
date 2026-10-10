using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FomaPay
{
    /// <summary>
    /// Erreur de base du SDK. <see cref="Code"/> est stable et exploitable par le programme
    /// (ex. <c>ERROR_LEVEL_0_LIMIT_REACHED</c>) ; <see cref="Exception.Message"/> est lisible par un humain.
    /// </summary>
    public class FomaPayException : Exception
    {
        private static readonly IReadOnlyDictionary<string, JsonElement> NoDetails = new Dictionary<string, JsonElement>();

        /// <summary>Crée l'erreur.</summary>
        public FomaPayException(
            string code, string message, int status = 0, IReadOnlyDictionary<string, JsonElement>? details = null, Exception? inner = null)
            : base(code + ": " + message, inner)
        {
            Code = code;
            Status = status;
            Details = details ?? NoDetails;
        }

        /// <summary>Code d'erreur stable.</summary>
        public string Code { get; }

        /// <summary>Statut HTTP (0 quand il n'y a pas eu de réponse).</summary>
        public int Status { get; }

        /// <summary>Précisions fournies par l'API.</summary>
        public IReadOnlyDictionary<string, JsonElement> Details { get; }
    }

    /// <summary>L'API a répondu avec une erreur.</summary>
    public class FomaPayApiException : FomaPayException
    {
        /// <summary>Crée l'erreur.</summary>
        public FomaPayApiException(string code, string message, int status, IReadOnlyDictionary<string, JsonElement>? details = null)
            : base(code, message, status, details)
        {
        }
    }

    /// <summary>Aucune réponse : connexion impossible (<c>ERROR_NETWORK</c>) ou délai dépassé (<c>ERROR_TIMEOUT</c>).</summary>
    public class FomaPayNetworkException : FomaPayException
    {
        /// <summary>Crée l'erreur.</summary>
        public FomaPayNetworkException(string code, string message, Exception? inner = null)
            : base(code, message, 0, null, inner)
        {
        }
    }

    /// <summary>Signature de webhook invalide ou expirée.</summary>
    public class FomaPaySignatureException : FomaPayException
    {
        /// <summary>Crée l'erreur.</summary>
        public FomaPaySignatureException(string code, string message)
            : base(code, message, 400)
        {
        }
    }
}
