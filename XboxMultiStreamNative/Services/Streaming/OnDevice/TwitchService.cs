using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Cliente directo de Twitch (sin backend). Consulta el canal y resuelve la
    /// URL HLS de la emisión que la propia consola está enviando a Twitch.
    /// </summary>
    public sealed class TwitchService
    {
        internal const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

        private const string GqlUrl = "https://gql.twitch.tv/gql";
        private const string UsherUrl = "https://usher.ttvnw.net/api/channel/hls/";

        private const string UserQuery = @"query PluraChannel($login: String!) {
  user(login: $login) {
    login
    displayName
    profileImageURL(width: 300)
    stream { id title viewersCount type }
  }
}";

        private const string TokenQuery = @"query PlaybackAccessToken($login: String!, $isLive: Boolean!, $playerType: String!) {
  streamPlaybackAccessToken(channelName: $login, params: {platform: ""web"", playerBackend: ""mediaplayer"", playerType: $playerType}) @include(if: $isLive) {
    value
    signature
  }
}";

        private static readonly HttpClient Http = CreateClient();

        private readonly object _avatarGate = new object();
        private readonly Dictionary<string, string> _avatarCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Client-ID", ClientId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Plura/1.0");
            return client;
        }

        public static string NormalizeLogin(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) { return string.Empty; }

            var marker = text.IndexOf("twitch.tv", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                text = text.Substring(marker + "twitch.tv".Length).TrimStart('/');
                var slash = text.IndexOfAny(new[] { '/', '?', '#' });
                if (slash >= 0) { text = text.Substring(0, slash); }
            }

            text = text.Trim().TrimStart('@', '#');
            return text.ToLowerInvariant();
        }

        public static bool IsValidLogin(string login)
        {
            if (string.IsNullOrEmpty(login) || login.Length < 3 || login.Length > 25) { return false; }
            foreach (var c in login)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) { return false; }
            }
            return true;
        }

        public Task<TwitchChannelInfo> GetChannelAsync(string login, CancellationToken token)
        {
            return GetChannelAsync(login, token, true);
        }

        public async Task<TwitchChannelInfo> GetChannelAsync(
            string login, CancellationToken token, bool downloadAvatar)
        {
            var clean = NormalizeLogin(login);
            if (!IsValidLogin(clean))
            {
                throw new OnDeviceRelayException("El nombre del canal de Twitch no es válido.");
            }

            var body = new JsonObject();
            body.SetNamedValue("query", JsonValue.CreateStringValue(UserQuery));
            var variables = new JsonObject();
            variables.SetNamedValue("login", JsonValue.CreateStringValue(clean));
            body.SetNamedValue("variables", variables);

            var json = await PostGqlAsync(body, token);
            var data = GetObject(json, "data");
            var user = data == null ? null : GetObject(data, "user");
            if (user == null)
            {
                throw new OnDeviceRelayException("El canal " + clean + " no existe en Twitch.");
            }

            var info = new TwitchChannelInfo();
            info.Login = ReadString(user, "login", clean);
            info.DisplayName = ReadString(user, "displayName", info.Login);
            info.AvatarUrl = ReadString(user, "profileImageURL", string.Empty);

            var stream = GetObject(user, "stream");
            info.Live = stream != null &&
                        string.Equals(ReadString(stream, "type", string.Empty), "live", StringComparison.Ordinal);
            info.Title = stream == null ? string.Empty : ReadString(stream, "title", string.Empty);
            info.Viewers = stream == null ? 0 : (int)ReadNumber(stream, "viewersCount", 0);

            if (downloadAvatar && info.AvatarUrl.Length > 0)
            {
                info.AvatarBase64 = await GetAvatarAsync(info.AvatarUrl, token);
            }

            return info;
        }

        /// <summary>Devuelve la URL del playlist maestro con un token nuevo.</summary>
        public async Task<string> GetMasterPlaylistUrlAsync(string login, CancellationToken token)
        {
            var clean = NormalizeLogin(login);
            if (!IsValidLogin(clean))
            {
                throw new OnDeviceRelayException("El nombre del canal de Twitch no es válido.");
            }

            var body = new JsonObject();
            body.SetNamedValue("query", JsonValue.CreateStringValue(TokenQuery));
            var variables = new JsonObject();
            variables.SetNamedValue("login", JsonValue.CreateStringValue(clean));
            variables.SetNamedValue("isLive", JsonValue.CreateBooleanValue(true));
            variables.SetNamedValue("playerType", JsonValue.CreateStringValue("site"));
            body.SetNamedValue("variables", variables);

            var json = await PostGqlAsync(body, token);
            var data = GetObject(json, "data");
            var access = data == null ? null : GetObject(data, "streamPlaybackAccessToken");
            if (access == null)
            {
                throw new OnDeviceRelayException("El canal no está en vivo o Twitch no entregó el token de reproducción.");
            }

            var value = ReadString(access, "value", string.Empty);
            var signature = ReadString(access, "signature", string.Empty);
            if (value.Length == 0 || signature.Length == 0)
            {
                throw new OnDeviceRelayException("Twitch devolvió un token de reproducción vacío.");
            }

            return UsherUrl + Uri.EscapeDataString(clean) + ".m3u8" +
                   "?allow_source=true&allow_audio_only=true&fast_bread=true" +
                   "&player_backend=mediaplayer&playlist_include_framerate=true" +
                   "&reassignments_supported=true&supported_codecs=avc1" +
                   "&sig=" + Uri.EscapeDataString(signature) +
                   "&token=" + Uri.EscapeDataString(value) +
                   "&transcode_mode=catalyst_5.7.0";
        }

        /// <summary>Elige la variante AVC de mayor bitrate del playlist maestro.</summary>
        public async Task<string> GetMediaPlaylistUrlAsync(string masterUrl, CancellationToken token)
        {
            var text = await GetTextAsync(masterUrl, token);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0)
                            .ToList();

            Uri best = null;
            long bestBandwidth = -1;
            string pendingAttributes = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    pendingAttributes = line.Substring("#EXT-X-STREAM-INF:".Length);
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal)) { continue; }
                if (pendingAttributes == null) { continue; }

                if (pendingAttributes.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    long bandwidth = 0;
                    foreach (var part in pendingAttributes.Split(','))
                    {
                        var kv = part.Split(new[] { '=' }, 2);
                        if (kv.Length == 2 &&
                            string.Equals(kv[0].Trim(), "BANDWIDTH", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(kv[1].Trim(), out bandwidth);
                        }
                    }

                    if (bandwidth > bestBandwidth && Uri.TryCreate(line, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        best = Resolve(masterUrl, uri);
                        bestBandwidth = bandwidth;
                    }
                }

                pendingAttributes = null;
            }

            if (best != null) { return best.AbsoluteUri; }

            // Algunos masters solo tienen una variante sin atributo CODECS.
            var firstUri = lines.FirstOrDefault(l => !l.StartsWith("#", StringComparison.Ordinal));
            if (firstUri != null && Uri.TryCreate(firstUri, UriKind.RelativeOrAbsolute, out var fallback))
            {
                return Resolve(masterUrl, fallback).AbsoluteUri;
            }

            throw new OnDeviceRelayException("El playlist de Twitch no contiene variantes de vídeo.");
        }

        /// <summary>Descarga la lista de segmentos vigente del playlist de media.</summary>
        public async Task<HlsPlaylist> GetPlaylistAsync(string mediaPlaylistUrl, CancellationToken token)
        {
            var text = await GetTextAsync(mediaPlaylistUrl, token);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0)
                            .ToList();

            var result = new HlsPlaylist();
            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var uriText = ReadAttribute(line, "URI");
                    if (uriText.Length > 0 && Uri.TryCreate(uriText, UriKind.RelativeOrAbsolute, out var init))
                    {
                        result.Init = Resolve(mediaPlaylistUrl, init);
                    }
                    continue;
                }

                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    int sequence;
                    if (int.TryParse(line.Substring("#EXT-X-MEDIA-SEQUENCE:".Length).Trim(), out sequence))
                    {
                        result.MediaSequence = sequence;
                    }
                    continue;
                }

                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    double value;
                    if (double.TryParse(line.Substring("#EXT-X-TARGETDURATION:".Length).Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value))
                    {
                        result.TargetDuration = value;
                    }
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal)) { continue; }
                if (Uri.TryCreate(line, UriKind.RelativeOrAbsolute, out var uri))
                {
                    result.Segments.Add(Resolve(mediaPlaylistUrl, uri));
                }
            }

            if (result.Segments.Count == 0)
            {
                throw new OnDeviceRelayException("El playlist de Twitch no tiene segmentos.");
            }

            return result;
        }

        public async Task<List<Uri>> GetSegmentsAsync(string mediaPlaylistUrl, CancellationToken token)
        {
            return (await GetPlaylistAsync(mediaPlaylistUrl, token)).Segments;
        }

        private static string ReadAttribute(string line, string name)
        {
            var marker = name + "=";
            int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) { return string.Empty; }
            start += marker.Length;
            if (start >= line.Length) { return string.Empty; }
            if (line[start] != '"')
            {
                int end = line.IndexOf(',', start);
                return (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
            }
            start++;
            int quote = line.IndexOf('"', start);
            return quote < 0 ? line.Substring(start) : line.Substring(start, quote - start);
        }

        public async Task<byte[]> GetSegmentAsync(Uri uri, CancellationToken token)
        {
            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
        }

        private async Task<string> GetAvatarAsync(string url, CancellationToken token)
        {
            lock (_avatarGate)
            {
                if (_avatarCache.TryGetValue(url, out var cached)) { return cached; }
            }

            try
            {
                using (var response = await Http.GetAsync(url, token))
                {
                    if (!response.IsSuccessStatusCode) { return string.Empty; }
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    var base64 = Convert.ToBase64String(bytes);
                    lock (_avatarGate)
                    {
                        if (_avatarCache.Count > 32) { _avatarCache.Clear(); }
                        _avatarCache[url] = base64;
                    }
                    return base64;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<JsonObject> PostGqlAsync(JsonObject body, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, GqlUrl))
            {
                request.Content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, token))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new OnDeviceRelayException("Twitch respondió " + (int)response.StatusCode + ".");
                    }

                    if (!JsonObject.TryParse(text, out var json) || json == null)
                    {
                        throw new OnDeviceRelayException("Twitch devolvió una respuesta no válida.");
                    }

                    if (json.TryGetValue("errors", out var errors) && errors.ValueType == JsonValueType.Array &&
                        errors.GetArray().Count > 0)
                    {
                        var first = errors.GetArray()[0];
                        var message = first.ValueType == JsonValueType.Object
                            ? ReadString(first.GetObject(), "message", "Error de Twitch")
                            : "Error de Twitch";
                        throw new OnDeviceRelayException("Twitch: " + message);
                    }

                    return json;
                }
            }
        }

        private static async Task<string> GetTextAsync(string url, CancellationToken token)
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static Uri Resolve(string baseUrl, Uri uri)
        {
            if (uri.IsAbsoluteUri) { return uri; }
            return new Uri(new Uri(baseUrl), uri);
        }

        private static JsonObject GetObject(JsonObject source, string key)
        {
            if (source != null && source.TryGetValue(key, out var value) &&
                value.ValueType == JsonValueType.Object)
            {
                return value.GetObject();
            }
            return null;
        }

        private static string ReadString(JsonObject source, string key, string fallback)
        {
            if (source == null || !source.TryGetValue(key, out var value)) { return fallback; }
            if (value.ValueType == JsonValueType.String) { return value.GetString(); }
            if (value.ValueType == JsonValueType.Number) { return value.GetNumber().ToString(); }
            return fallback;
        }

        private static double ReadNumber(JsonObject source, string key, double fallback)
        {
            if (source == null || !source.TryGetValue(key, out var value)) { return fallback; }
            if (value.ValueType == JsonValueType.Number) { return value.GetNumber(); }
            if (value.ValueType == JsonValueType.String && double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
            return fallback;
        }
    }
}
