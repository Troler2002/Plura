using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Plura.Services.Streaming
{
    public sealed class BackendException : Exception
    {
        public BackendException(string message) : base(message) { }
        public BackendException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Cliente del backend de Plura. El backend guarda los secretos OAuth y
    /// los refresh tokens; la app solo maneja un token de sesión de corta vida.
    /// </summary>
    public sealed class PluraBackendClient
    {
        private const string SessionKey = "backend.session";

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly SecureStore _secure;
        private string _sessionToken;

        public PluraBackendClient(SecureStore secure)
        {
            _secure = secure ?? new SecureStore("Plura");
        }

        public string BaseUrl { get; set; } = string.Empty;

        public string SessionToken
        {
            get
            {
                if (_sessionToken == null)
                {
                    _sessionToken = _secure.Get(SessionKey);
                }

                return _sessionToken;
            }
            set
            {
                _sessionToken = value ?? string.Empty;
                if (_sessionToken.Length == 0)
                {
                    _secure.Remove(SessionKey);
                }
                else
                {
                    _secure.Set(SessionKey, _sessionToken);
                }
            }
        }

        public Task<JsonObject> GetAsync(string path, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(path));
            return SendAsync(request, token);
        }

        public Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(path));
            var json = (body ?? new JsonObject()).Stringify();
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return SendAsync(request, token);
        }

        /// <summary>
        /// Crea la sesión de la consola la primera vez. El modo consola la usa
        /// para autenticar el relay; las cuentas vinculadas la reutilizan.
        /// </summary>
        public async Task EnsureSessionAsync(CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(SessionToken)) { return; }

            var data = await PostAsync("/v1/session", new JsonObject(), token);
            var session = GetString(data, "sessionToken", string.Empty);
            if (!string.IsNullOrWhiteSpace(session))
            {
                SessionToken = session;
            }
        }

        private async Task<JsonObject> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                request.Dispose();
                throw new BackendException(
                    "Configura la URL del backend de Plura para conectar cuentas.");
            }

            using (request)
            {
                var session = SessionToken;
                if (!string.IsNullOrEmpty(session))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session);
                }

                HttpResponseMessage response;
                try
                {
                    response = await Http.SendAsync(request, token);
                }
                catch (OperationCanceledException)
                {
                    throw new BackendException("El backend no respondió a tiempo.");
                }
                catch (Exception ex)
                {
                    throw new BackendException(
                        "No se pudo conectar con el backend: " + ex.Message, ex);
                }

                using (response)
                {
                    string text;
                    try
                    {
                        text = await response.Content.ReadAsStringAsync();
                    }
                    catch
                    {
                        text = string.Empty;
                    }

                    JsonObject json = null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        JsonObject.TryParse(text, out json);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var message = json != null ? GetString(json, "error", null) : null;
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            message = "El backend respondió " + (int)response.StatusCode + ".";
                        }

                        throw new BackendException(message);
                    }

                    return json ?? new JsonObject();
                }
            }
        }

        private string BuildUrl(string path)
        {
            var root = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (root.Length == 0)
            {
                throw new BackendException("Configura la URL del backend de Plura.");
            }

            if (root.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                root = "http://" + root;
            }

            if (string.IsNullOrEmpty(path)) { path = "/"; }
            if (!path.StartsWith("/", StringComparison.Ordinal)) { path = "/" + path; }

            return root + path;
        }

        internal static string GetString(JsonObject source, string key, string fallback)
        {
            if (source != null && source.TryGetValue(key, out var value))
            {
                switch (value.ValueType)
                {
                    case JsonValueType.String:
                        return value.GetString();
                    case JsonValueType.Number:
                        return value.GetNumber().ToString();
                    case JsonValueType.Boolean:
                        return value.GetBoolean() ? "true" : "false";
                }
            }

            return fallback;
        }

        internal static double GetNumber(JsonObject source, string key, double fallback)
        {
            if (source != null && source.TryGetValue(key, out var value))
            {
                if (value.ValueType == JsonValueType.Number) { return value.GetNumber(); }
                if (value.ValueType == JsonValueType.String &&
                    double.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }
    }
}
