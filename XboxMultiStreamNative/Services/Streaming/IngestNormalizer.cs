using System;

namespace Plura.Services.Streaming
{
    /// <summary>
    /// Convierte lo que escribe el usuario (o lo que devuelve el backend) en
    /// una URL de servidor y una clave limpias. Así el modo manual y el modo
    /// cuenta producen exactamente el mismo formato para OBS.
    /// </summary>
    public static class IngestNormalizer
    {
        public static string NormalizeServer(string raw, string platformId)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return string.Empty; }

            var value = raw.Trim().Trim('"').Trim();
            value = value.Replace(" ", string.Empty);

            if (value.Length == 0) { return string.Empty; }

            if (value.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                value = "rtmp://" + value;
            }

            var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
            var scheme = value.Substring(0, schemeEnd).ToLowerInvariant();
            if (scheme != "rtmp" && scheme != "rtmps" && scheme != "rtmpt" && scheme != "rtmpts")
            {
                return value;
            }

            // La clave se concatena directamente al servidor, así que la URL
            // debe terminar en "/" salvo que ya traiga parámetros.
            if (!value.EndsWith("/", StringComparison.Ordinal) &&
                value.IndexOf('?') < 0)
            {
                value += "/";
            }

            return value;
        }

        public static string NormalizeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return string.Empty; }

            var value = raw.Trim().Trim('"').Trim();
            if (value.Length == 0) { return string.Empty; }

            // Si el usuario pegó la URL completa en el campo de la clave,
            // nos quedamos con el último segmento.
            if (value.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("rtmpt://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("rtmpts://", StringComparison.OrdinalIgnoreCase))
            {
                var query = value.IndexOf('?');
                if (query >= 0) { value = value.Substring(0, query); }

                var slash = value.LastIndexOf('/');
                if (slash >= 0 && slash < value.Length - 1)
                {
                    value = value.Substring(slash + 1);
                }
            }

            return value.Trim();
        }

        public static string DefaultServerFor(string platformId)
        {
            switch ((platformId ?? string.Empty).ToLowerInvariant())
            {
                case "twitch": return "rtmp://live.twitch.tv/app/";
                case "youtube": return "rtmp://a.rtmp.youtube.com/live2/";
                // Kick (Amazon IVS) exige RTMPS: por RTMP plano conecta, pero
                // cierra la sesión cuando recibe publish, aunque la key sea válida.
                case "kick": return "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app/";
                case "facebook": return "rtmps://live-api-s.facebook.com:443/rtmp/";
                default: return string.Empty;
            }
        }
    }
}
