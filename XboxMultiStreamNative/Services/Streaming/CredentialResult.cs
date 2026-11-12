using System;

namespace Plura.Services.Streaming
{
    /// <summary>Resultado de resolver credenciales para una plataforma.</summary>
    public sealed class CredentialResult
    {
        private CredentialResult() { }

        public bool Success { get; private set; }

        public StreamEndpoint Endpoint { get; private set; }

        public string Error { get; private set; }

        public static CredentialResult Ok(StreamEndpoint endpoint)
        {
            return new CredentialResult
            {
                Success = endpoint != null && endpoint.IsValid,
                Endpoint = endpoint,
                Error = endpoint != null && endpoint.IsValid ? null : "Las credenciales están incompletas."
            };
        }

        public static CredentialResult Fail(string error)
        {
            return new CredentialResult
            {
                Success = false,
                Endpoint = null,
                Error = string.IsNullOrWhiteSpace(error) ? "No se pudieron obtener las credenciales." : error
            };
        }
    }
}
