using System;

namespace Plura.Services.Streaming
{
    /// <summary>De dónde salieron las credenciales de una plataforma.</summary>
    public enum CredentialSource
    {
        Manual,
        Account
    }

    /// <summary>Método elegido por el usuario para configurar una plataforma.</summary>
    public enum CredentialMode
    {
        Manual,
        Account
    }

    /// <summary>
    /// Credenciales ya normalizadas que entiende el motor de video:
    /// una URL de servidor RTMP y una clave de transmisión.
    /// </summary>
    public sealed class StreamEndpoint
    {
        public string PlatformId { get; set; } = string.Empty;

        public string ServerUrl { get; set; } = string.Empty;

        public string StreamKey { get; set; } = string.Empty;

        public CredentialSource Source { get; set; } = CredentialSource.Manual;

        public string AccountLabel { get; set; } = string.Empty;

        public bool IsValid
        {
            get
            {
                return !string.IsNullOrWhiteSpace(ServerUrl) &&
                       !string.IsNullOrWhiteSpace(StreamKey);
            }
        }

        public override string ToString()
        {
            return (PlatformId ?? string.Empty) + " · " +
                   (Source == CredentialSource.Account ? "cuenta" : "manual");
        }
    }
}
