using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Plura.Services
{
    /// <summary>Datos de un destino (target) del plugin Multiple RTMP de OBS.</summary>
    public sealed class ObsTargetInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Streaming { get; set; }
        public double BitrateBps { get; set; }
        public double Fps { get; set; }
        public string Duration { get; set; }
    }

    /// <summary>Estado de la transmisión principal (stream) de OBS.</summary>
    public sealed class ObsStreamStatus
    {
        public bool Active { get; set; }
        public bool Reconnecting { get; set; }
        public double BitrateBps { get; set; }
        public double Fps { get; set; }
        public string Duration { get; set; } = "—";
    }

    public sealed class ObsException : Exception
    {
        public ObsException(string message) : base(message) { }
        public ObsException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Cliente nativo de OBS WebSocket v5. Habla el protocolo directamente
    /// (Hello / Identify / Request / RequestResponse) y expone el vendor
    /// "romestylez.multi_rtmp" del plugin Multiple RTMP Output (edición WebSocket).
    /// </summary>
    public sealed class ObsClient : IDisposable
    {
        private const int RequestTimeoutMs = 15000;
        private const int HandshakeTimeoutMs = 9000;
        private const string VendorName = "romestylez.multi_rtmp";

        private readonly object _sync = new object();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<JsonObject>>();

        private ClientWebSocket _socket;
        private CancellationTokenSource _receiveCts;
        private Task _receiveTask;
        private TaskCompletionSource<JsonObject> _helloTcs;
        private TaskCompletionSource<JsonObject> _identifiedTcs;
        private int _requestCounter;

        public bool IsConnected
        {
            get
            {
                var socket = _socket;
                return socket != null && socket.State == WebSocketState.Open;
            }
        }

        public string ConnectedHost { get; private set; }

        public int ConnectedPort { get; private set; }

        public async Task ConnectAsync(string host, int port, string password)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ObsException("Escribe la IP del PC donde corre OBS.");
            }

            if (IsConnected)
            {
                return;
            }

            CleanupSocket();

            var uri = BuildUri(host, port);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            _socket = socket;
            _helloTcs = new TaskCompletionSource<JsonObject>();
            _identifiedTcs = new TaskCompletionSource<JsonObject>();
            _receiveCts = new CancellationTokenSource();

            try
            {
                using (var timeout = new CancellationTokenSource(HandshakeTimeoutMs))
                {
                    await socket.ConnectAsync(uri, timeout.Token);
                }
            }
            catch (Exception ex)
            {
                CleanupSocket();
                throw new ObsException(
                    "No se pudo conectar con OBS en " + uri.Host + ":" + uri.Port + ". " + ex.Message, ex);
            }

            _receiveTask = ReceiveLoopAsync(socket, _receiveCts.Token);

            JsonObject hello;
            try
            {
                hello = await WithTimeout(_helloTcs.Task, HandshakeTimeoutMs, "OBS no respondió con Hello.");
            }
            catch (Exception ex)
            {
                CleanupSocket();
                throw new ObsException("OBS no respondió. Verifica el host, el puerto y el WebSocket.", ex);
            }

            var identify = new JsonObject();
            identify.SetNamedValue("rpcVersion", JsonValue.CreateNumberValue(1));
            identify.SetNamedValue("eventSubscriptions", JsonValue.CreateNumberValue(0));

            var authentication = AsObject(hello, "authentication");
            if (authentication != null)
            {
                var challenge = GetString(authentication, "challenge", string.Empty);
                var salt = GetString(authentication, "salt", string.Empty);
                identify.SetNamedValue(
                    "authentication",
                    JsonValue.CreateStringValue(ComputeAuthentication(password ?? string.Empty, salt, challenge)));
            }

            var identifyMessage = new JsonObject();
            identifyMessage.SetNamedValue("op", JsonValue.CreateNumberValue(1));
            identifyMessage.SetNamedValue("d", identify);

            try
            {
                await SendTextAsync(socket, identifyMessage.Stringify(), _receiveCts.Token);
                await WithTimeout(_identifiedTcs.Task, HandshakeTimeoutMs, "OBS no confirmó la conexión.");
            }
            catch (Exception ex)
            {
                CleanupSocket();
                throw new ObsException("OBS rechazó la conexión. Revisa la contraseña del WebSocket.", ex);
            }

            ConnectedHost = uri.Host;
            ConnectedPort = uri.Port;
        }

        public async Task DisconnectAsync()
        {
            var socket = _socket;
            _socket = null;

            try
            {
                if (_receiveCts != null) { _receiveCts.Cancel(); }
            }
            catch { }

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        var close = socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        await Task.WhenAny(close, Task.Delay(1500));
                    }
                }
                catch { }

                try { socket.Dispose(); } catch { }
            }

            FailPending("Conexión con OBS cerrada.");
        }

        public void Dispose()
        {
            try
            {
                if (_receiveCts != null) { _receiveCts.Cancel(); }
            }
            catch { }

            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try { socket.Dispose(); } catch { }
            }

            FailPending("Conexión con OBS cerrada.");
        }

        public async Task<List<ObsTargetInfo>> ListTargetsAsync()
        {
            var data = await CallVendorAsync("ListTargets", null);
            var targets = new List<ObsTargetInfo>();
            if (data == null)
            {
                return targets;
            }

            if (!data.TryGetValue("targets", out var value) || value.ValueType != JsonValueType.Array)
            {
                return targets;
            }

            foreach (var item in value.GetArray())
            {
                if (item.ValueType != JsonValueType.Object) { continue; }
                var obj = item.GetObject();
                var state = GetString(obj, "state", string.Empty);
                targets.Add(new ObsTargetInfo
                {
                    Id = GetString(obj, "id", string.Empty),
                    Name = GetString(obj, "name", string.Empty),
                    Streaming = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
                    Duration = "—"
                });
            }

            // El fork nunca asigna isRunning_, así que ListTargets.state siempre
            // dice "stopped". La única fuente fiable son las estadísticas: un
            // destino está en vivo si reporta bitrate, fps o duración > 0.
            foreach (var target in targets)
            {
                try
                {
                    var stats = await GetTargetStatsAsync(target.Id, target.Name);
                    if (stats == null) { continue; }

                    target.BitrateBps = stats.BitrateBps;
                    target.Fps = stats.Fps;
                    target.Duration = stats.Duration;
                    target.Streaming = IsStatsLive(stats);
                }
                catch
                {
                    // las estadísticas son opcionales; no rompemos el refresco
                }
            }

            return targets;
        }

        private static bool IsStatsLive(ObsTargetInfo stats)
        {
            if (stats == null) { return false; }
            if (stats.Streaming || stats.BitrateBps > 0 || stats.Fps > 0) { return true; }

            var duration = stats.Duration;
            return !string.IsNullOrWhiteSpace(duration) &&
                   duration != "—" &&
                   duration != "00:00:00" &&
                   duration != "00:00:00.000";
        }

        public async Task<ObsTargetInfo> GetTargetStatsAsync(string targetId, string targetName)
        {
            var data = await CallVendorAsync("GetTargetStats", BuildTargetRef(targetId, targetName));
            return ParseTarget(data);
        }

        public async Task StartTargetAsync(string targetId, string targetName)
        {
            await CallVendorAsync("StartTarget", BuildTargetRef(targetId, targetName));
        }

        public async Task StopTargetAsync(string targetId, string targetName)
        {
            await CallVendorAsync("StopTarget", BuildTargetRef(targetId, targetName));
        }

        // ------------------------------------------------- Stream principal (OBS)

        /// <summary>
        /// Configura el servicio RTMP personalizado de la transmisión principal
        /// de OBS. El plugin Multiple RTMP reutiliza el encoder principal, así
        /// que esta transmisión debe estar activa antes de añadir destinos.
        /// </summary>
        public async Task SetStreamServiceAsync(string serverUrl, string streamKey)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                throw new ObsException("La URL del servidor está vacía.");
            }

            var settings = new JsonObject();
            settings.SetNamedValue("server", JsonValue.CreateStringValue(NormalizeServer(serverUrl)));
            settings.SetNamedValue("key", JsonValue.CreateStringValue(streamKey ?? string.Empty));
            settings.SetNamedValue("use_auth", JsonValue.CreateBooleanValue(false));

            var data = new JsonObject();
            data.SetNamedValue("streamServiceType", JsonValue.CreateStringValue("rtmp_custom"));
            data.SetNamedValue("streamServiceSettings", settings);

            await SendRequestAsync("SetStreamServiceSettings", data);
        }

        public async Task StartStreamAsync()
        {
            await SendRequestAsync("StartStream", null);
        }

        public async Task StopStreamAsync()
        {
            await SendRequestAsync("StopStream", null);
        }

        public async Task<ObsStreamStatus> GetStreamStatusAsync()
        {
            var response = await SendRequestAsync("GetStreamStatus", null);
            var data = AsObject(response, "responseData") ?? response;

            var durationMs = GetNumber(data, "outputDuration", 0);
            var seconds = durationMs / 1000.0;
            var bytes = GetNumber(data, "outputBytes", 0);
            var frames = GetNumber(data, "outputTotalFrames", 0);

            var status = new ObsStreamStatus
            {
                Active = GetBool(data, "outputActive", false),
                Reconnecting = GetBool(data, "outputReconnecting", false),
                BitrateBps = seconds > 0 ? bytes * 8.0 / seconds : 0,
                Fps = seconds > 0 ? frames / seconds : 0
            };

            if (durationMs > 0)
            {
                var span = TimeSpan.FromMilliseconds(durationMs);
                status.Duration = span.ToString(@"hh\:mm\:ss");
            }

            return status;
        }

        /// <summary>Espera hasta que OBS confirme que la transmisión principal está activa.</summary>
        public async Task<bool> WaitForStreamActiveAsync(int timeoutMs)
        {
            var started = Environment.TickCount;
            while (Environment.TickCount - started < timeoutMs)
            {
                try
                {
                    var status = await GetStreamStatusAsync();
                    if (status != null && status.Active) { return true; }
                }
                catch
                {
                    // seguimos esperando
                }

                await Task.Delay(500);
            }

            return false;
        }

        private static string NormalizeServer(string serverUrl)
        {
            var server = (serverUrl ?? string.Empty).Trim();
            if (server.Length > 0 && !server.EndsWith("/", StringComparison.Ordinal))
            {
                server += "/";
            }

            return server;
        }

        /// <summary>
        /// Busca el destino en OBS; si no existe lo crea, le configura el servidor
        /// y la stream key, y lo devuelve listo para iniciar.
        /// </summary>
        public async Task<ObsTargetInfo> EnsureTargetAsync(
            string targetName, string serverUrl, string streamKey)
        {
            var name = (targetName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ObsException("El destino no tiene nombre.");
            }

            var target = FindTarget(await ListTargetsAsync(), name);
            if (target == null)
            {
                var add = new JsonObject();
                add.SetNamedValue("name", JsonValue.CreateStringValue(name));
                add.SetNamedValue("protocol", JsonValue.CreateStringValue("rtmp"));
                await CallVendorAsync("AddTarget", add);
                target = FindTarget(await ListTargetsAsync(), name);
                if (target == null)
                {
                    throw new ObsException("OBS no pudo crear el destino \"" + name + "\".");
                }
            }

            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                var service = new JsonObject();
                service.SetNamedValue("targetId", JsonValue.CreateStringValue(target.Id));
                service.SetNamedValue("key", JsonValue.CreateStringValue("server"));
                service.SetNamedValue("value", JsonValue.CreateStringValue(serverUrl.Trim()));
                await CallVendorAsync("UpdateServiceParam", service);
            }

            if (!string.IsNullOrWhiteSpace(streamKey))
            {
                var key = new JsonObject();
                key.SetNamedValue("targetId", JsonValue.CreateStringValue(target.Id));
                key.SetNamedValue("streamKey", JsonValue.CreateStringValue(streamKey.Trim()));
                await CallVendorAsync("UpdateStreamKey", key);
            }

            return target;
        }

        public async Task CallVendorAsync(string requestType)
        {
            await CallVendorAsync(requestType, null);
        }

        public async Task<JsonObject> CallVendorAsync(string requestType, JsonObject requestData)
        {
            var vendor = new JsonObject();
            vendor.SetNamedValue("vendorName", JsonValue.CreateStringValue(VendorName));
            vendor.SetNamedValue("requestType", JsonValue.CreateStringValue(requestType));
            vendor.SetNamedValue("requestData", requestData ?? new JsonObject());

            var response = await SendRequestAsync("CallVendorRequest", vendor);

            var outer = AsObject(response, "responseData");
            if (outer == null)
            {
                return new JsonObject();
            }

            var nested = AsObject(outer, "responseData");
            var result = nested ?? outer;

            // El vendor devuelve success=false + error en lugar de fallar la petición.
            if (result.TryGetValue("success", out var successValue) &&
                successValue.ValueType == JsonValueType.Boolean &&
                !successValue.GetBoolean())
            {
                var error = GetString(result, "error", "El plugin Multiple RTMP rechazó la petición.");
                throw new ObsException(error);
            }

            return result;
        }

        private async Task<JsonObject> SendRequestAsync(string requestType, JsonObject requestData)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new ObsException("OBS no está conectado.");
            }

            var id = Interlocked.Increment(ref _requestCounter).ToString();
            var tcs = new TaskCompletionSource<JsonObject>();
            _pending[id] = tcs;

            var message = new JsonObject();
            message.SetNamedValue("op", JsonValue.CreateNumberValue(6));

            var data = new JsonObject();
            data.SetNamedValue("requestType", JsonValue.CreateStringValue(requestType));
            data.SetNamedValue("requestId", JsonValue.CreateStringValue(id));
            data.SetNamedValue("requestData", requestData ?? new JsonObject());
            message.SetNamedValue("d", data);

            try
            {
                await SendTextAsync(socket, message.Stringify(), _receiveCts != null ? _receiveCts.Token : CancellationToken.None);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                throw new ObsException("No se pudo enviar la petición a OBS. " + ex.Message, ex);
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeoutMs));
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new ObsException("OBS no respondió a \"" + requestType + "\" a tiempo.");
            }

            var response = await tcs.Task;
            var status = AsObject(response, "requestStatus");
            if (status != null && !GetBool(status, "result", false))
            {
                var comment = GetString(status, "comment", "Error de OBS");
                var code = (int)GetNumber(status, "code", 0);
                throw new ObsException(code > 0 ? comment + " (código " + code + ")" : comment);
            }

            return response;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16384];
            var message = new MemoryStream();

            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    HandleMessage(text);
                }
            }
            finally
            {
                try { message.Dispose(); } catch { }

                var closed = new ObsException("La conexión con OBS se cerró.");
                _helloTcs?.TrySetException(closed);
                _identifiedTcs?.TrySetException(closed);
                FailPending("La conexión con OBS se cerró.");
            }
        }

        private void HandleMessage(string text)
        {
            JsonObject message;
            if (!JsonObject.TryParse(text, out message) || message == null)
            {
                return;
            }

            var op = (int)GetNumber(message, "op", -1);
            var data = AsObject(message, "d");
            if (data == null)
            {
                return;
            }

            if (op == 0)
            {
                _helloTcs?.TrySetResult(data);
                return;
            }

            if (op == 2)
            {
                _identifiedTcs?.TrySetResult(data);
                return;
            }

            if (op == 7)
            {
                var requestId = GetString(data, "requestId", null);
                if (requestId != null && _pending.TryRemove(requestId, out var tcs))
                {
                    tcs.TrySetResult(data);
                }
            }
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int milliseconds, string message)
        {
            var completed = await Task.WhenAny(task, Task.Delay(milliseconds));
            if (completed != task)
            {
                throw new ObsException(message);
            }

            return await task;
        }

        private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private static Uri BuildUri(string host, int port)
        {
            var raw = host.Trim();
            if (raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(5);
            }
            else if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(6);
            }

            var slash = raw.IndexOf('/');
            if (slash >= 0)
            {
                raw = raw.Substring(0, slash);
            }

            if (!raw.Contains(":"))
            {
                raw = raw + ":" + port;
            }

            return new Uri("ws://" + raw);
        }

        private static string ComputeAuthentication(string password, string salt, string challenge)
        {
            var secret = Convert.ToBase64String(
                SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(
                SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        private static JsonObject BuildTargetRef(string targetId, string targetName)
        {
            var data = new JsonObject();
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                data.SetNamedValue("id", JsonValue.CreateStringValue(targetId));
            }
            else
            {
                data.SetNamedValue("name", JsonValue.CreateStringValue((targetName ?? string.Empty).Trim()));
            }

            return data;
        }

        private static ObsTargetInfo FindTarget(List<ObsTargetInfo> targets, string name)
        {
            if (targets == null)
            {
                return null;
            }

            foreach (var target in targets)
            {
                if (target.Name != null && string.Equals(target.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            foreach (var target in targets)
            {
                if (target.Name != null && target.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return target;
                }
            }

            return null;
        }

        private static ObsTargetInfo ParseTarget(JsonObject data)
        {
            if (data == null)
            {
                return null;
            }

            return new ObsTargetInfo
            {
                Id = GetString(data, "id", string.Empty),
                Name = GetString(data, "name", string.Empty),
                Streaming = GetBool(data, "isRunning", false) ||
                            string.Equals(GetString(data, "state", string.Empty), "running", StringComparison.OrdinalIgnoreCase),
                BitrateBps = GetNumber(data, "bitrateValue", 0),
                Fps = GetNumber(data, "fpsValue", 0),
                Duration = GetString(data, "duration", "—")
            };
        }

        private void CleanupSocket()
        {
            try
            {
                if (_receiveCts != null) { _receiveCts.Cancel(); }
            }
            catch { }

            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try { socket.Dispose(); } catch { }
            }

            FailPending("Conexión con OBS cerrada.");
        }

        private void FailPending(string message)
        {
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var tcs))
                {
                    tcs.TrySetException(new ObsException(message));
                }
            }
        }

        private static JsonObject AsObject(JsonObject source, string key)
        {
            if (source != null && source.TryGetValue(key, out var value) && value.ValueType == JsonValueType.Object)
            {
                return value.GetObject();
            }

            return null;
        }

        private static string GetString(JsonObject source, string key, string fallback)
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

        private static bool GetBool(JsonObject source, string key, bool fallback)
        {
            if (source != null && source.TryGetValue(key, out var value))
            {
                if (value.ValueType == JsonValueType.Boolean) { return value.GetBoolean(); }
                if (value.ValueType == JsonValueType.String)
                {
                    return string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return fallback;
        }

        private static double GetNumber(JsonObject source, string key, double fallback)
        {
            if (source != null && source.TryGetValue(key, out var value))
            {
                if (value.ValueType == JsonValueType.Number) { return value.GetNumber(); }
                if (value.ValueType == JsonValueType.String)
                {
                    if (double.TryParse(value.GetString(), out var parsed)) { return parsed; }
                }
            }

            return fallback;
        }
    }
}
