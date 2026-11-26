using System;

namespace Plura.Services.Streaming
{
    /// <summary>Valida que un endpoint se pueda entregar al plugin de OBS.</summary>
    public static class StreamEndpointValidator
    {
        public static string Validate(string serverUrl, string streamKey)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return "Falta la URL del servidor RTMP.";
            }

            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            {
                return "La URL del servidor no es válida.";
            }

            var scheme = (uri.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "rtmp" && scheme != "rtmps" && scheme != "rtmpt" && scheme != "rtmpts")
            {
                return "La URL debe empezar por rtmp:// o rtmps://.";
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                return "La URL del servidor no tiene host.";
            }

            if (string.IsNullOrWhiteSpace(streamKey))
            {
                return "Falta la clave de la plataforma.";
            }

            if (streamKey.Trim().Length < 4)
            {
                return "La clave de la plataforma parece demasiado corta.";
            }

            return null;
        }
    }
}
