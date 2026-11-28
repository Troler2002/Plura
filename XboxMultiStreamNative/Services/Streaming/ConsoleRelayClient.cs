using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Plura.Services.Streaming
{
    /// <summary>Un destino RTMP que el relay debe alimentar.</summary>
    public sealed class RelayTarget
    {
        public string Platform { get; set; } = string.Empty;
        public string ServerUrl { get; set; } = string.Empty;
        public string StreamKey { get; set; } = string.Empty;

        /// <summary>Slug/cuenta del destino, usado por Kick para PATCH stream-info.</summary>
        public string AccountLabel { get; set; } = string.Empty;
    }

    public sealed class RelayOutputStatus
    {
        public string Platform { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public int Restarts { get; set; }
        public double BitrateKbps { get; set; }
        public double Fps { get; set; }
        public int DurationSeconds { get; set; }
        public DateTime LastMediaUtc { get; set; } = DateTime.MinValue;

        /// <summary>Media real recibida en los últimos 15 segundos.</summary>
        public bool MediaFresh
        {
            get
            {
                return LastMediaUtc != DateTime.MinValue &&
                       (DateTime.UtcNow - LastMediaUtc).TotalSeconds <= 15;
            }
        }

        public bool IsSending
        {
            get
            {
                return string.Equals(State, "running", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(State, "connecting", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(State, "restarting", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(State, "waiting", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public sealed class RelayStatus
    {
        public bool Running { get; set; }
        public bool Healthy { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Viewers { get; set; }
        public int UptimeSeconds { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<RelayOutputStatus> Outputs { get; } = new List<RelayOutputStatus>();

        public static RelayStatus Parse(JsonObject json)
        {
            var status = new RelayStatus();
            if (json == null) { return status; }

            status.Running = PluraBackendClient.GetString(json, "running", "false") == "true";
            status.Channel = PluraBackendClient.GetString(json, "channel", string.Empty);
            status.DisplayName = PluraBackendClient.GetString(json, "displayName", string.Empty);
            status.Title = PluraBackendClient.GetString(json, "title", string.Empty);
            status.Viewers = (int)PluraBackendClient.GetNumber(json, "viewers", 0);
            status.UptimeSeconds = (int)PluraBackendClient.GetNumber(json, "uptimeSeconds", 0);
            status.Error = PluraBackendClient.GetString(json, "error", string.Empty);

            if (json.TryGetValue("outputs", out var outputs) &&
                outputs.ValueType == JsonValueType.Array)
            {
                foreach (var item in outputs.GetArray())
                {
                    if (item.ValueType != JsonValueType.Object) { continue; }
                    var source = item.GetObject();

                    status.Outputs.Add(new RelayOutputStatus
                    {
                        Platform = PluraBackendClient.GetString(source, "platform", string.Empty),
                        State = PluraBackendClient.GetString(source, "state", string.Empty),
                        Error = PluraBackendClient.GetString(source, "error", string.Empty),
                        Restarts = (int)PluraBackendClient.GetNumber(source, "restarts", 0),
                        BitrateKbps = PluraBackendClient.GetNumber(source, "bitrateKbps", 0),
                        Fps = PluraBackendClient.GetNumber(source, "fps", 0),
                        DurationSeconds = (int)PluraBackendClient.GetNumber(source, "durationSeconds", 0)
                    });
                }
            }

            return status;
        }
    }

    public sealed class RelayChannelInfo
    {
        public string Login { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string AvatarBase64 { get; set; } = string.Empty;
        public string AvatarContentType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool Live { get; set; }
        public int Viewers { get; set; }

        public static RelayChannelInfo Parse(JsonObject json)
        {
            var info = new RelayChannelInfo();
            if (json == null) { return info; }

            info.Login = PluraBackendClient.GetString(json, "login", string.Empty);
            info.DisplayName = PluraBackendClient.GetString(json, "displayName", string.Empty);
            info.AvatarUrl = PluraBackendClient.GetString(json, "avatarUrl", string.Empty);
            info.AvatarBase64 = PluraBackendClient.GetString(json, "avatarBase64", string.Empty);
            info.AvatarContentType = PluraBackendClient.GetString(json, "avatarContentType", string.Empty);
            info.Title = PluraBackendClient.GetString(json, "title", string.Empty);
            info.Live = PluraBackendClient.GetString(json, "live", "false") == "true";
            info.Viewers = (int)PluraBackendClient.GetNumber(json, "viewers", 0);
            return info;
        }
    }

    /// <summary>
    /// Modo consola: la Xbox emite a Twitch con su función nativa y el
    /// backend de Plura recibe esa señal y la reparte al resto de plataformas.
    /// La consola solo manda nombres de plataforma y credenciales de ingesta.
    /// </summary>
    public sealed class ConsoleRelayClient
    {
        private readonly PluraBackendClient _backend;

        public ConsoleRelayClient(PluraBackendClient backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public async Task<RelayStatus> GetStatusAsync(CancellationToken token)
        {
            await _backend.EnsureSessionAsync(token);
            return RelayStatus.Parse(await _backend.GetAsync("/v1/relay/status", token));
        }

        public async Task<RelayStatus> StartAsync(
            string channel, IList<RelayTarget> targets, CancellationToken token)
        {
            await _backend.EnsureSessionAsync(token);

            var body = new JsonObject();
            body.SetNamedValue("channel", JsonValue.CreateStringValue(channel ?? string.Empty));

            var outputs = new JsonArray();
            foreach (var target in targets ?? new List<RelayTarget>())
            {
                var item = new JsonObject();
                item.SetNamedValue("platform", JsonValue.CreateStringValue(target.Platform ?? string.Empty));
                item.SetNamedValue("serverUrl", JsonValue.CreateStringValue(target.ServerUrl ?? string.Empty));
                item.SetNamedValue("streamKey", JsonValue.CreateStringValue(target.StreamKey ?? string.Empty));
                outputs.Add(item);
            }

            body.SetNamedValue("outputs", outputs);

            return RelayStatus.Parse(
                await _backend.PostAsync("/v1/relay/start", body, token));
        }

        public async Task<RelayStatus> StopAsync(CancellationToken token)
        {
            await _backend.EnsureSessionAsync(token);
            return RelayStatus.Parse(
                await _backend.PostAsync("/v1/relay/stop", new JsonObject(), token));
        }

        public async Task<RelayChannelInfo> CheckChannelAsync(string login, CancellationToken token)
        {
            await _backend.EnsureSessionAsync(token);
            var path = "/v1/relay/channel?login=" + Uri.EscapeDataString(login ?? string.Empty);
            return RelayChannelInfo.Parse(await _backend.GetAsync(path, token));
        }

        public async Task<RelayChannelInfo> GetLinkedChannelAsync(CancellationToken token)
        {
            await _backend.EnsureSessionAsync(token);
            return RelayChannelInfo.Parse(await _backend.GetAsync("/v1/twitch/channel", token));
        }
    }
}
