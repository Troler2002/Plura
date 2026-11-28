using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Plura.Services
{
    public sealed class ObsSettings
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 4455;
        public string Password { get; set; } = string.Empty;
    }

    public sealed class StreamSettings
    {
        public string Resolution { get; set; } = "1080";
        public int Fps { get; set; } = 60;
        public int Bitrate { get; set; } = 4500;
        public string Preset { get; set; } = "veryfast";
        public bool Audio { get; set; } = true;
    }

    public sealed class PlatformSettings
    {
        public bool Enabled { get; set; }
        public string StreamKey { get; set; } = string.Empty;
        public string ServerUrl { get; set; } = string.Empty;

        public string TargetName { get; set; } = string.Empty;
        public string Mode { get; set; } = "manual";
        public bool Linked { get; set; }
        public string AccountLabel { get; set; } = string.Empty;
    }

    /// <summary>Intención de emisión que se conserva si Xbox suspende la app.</summary>
    public sealed class RelaySessionSettings
    {
        public bool Active { get; set; }
        public string Channel { get; set; } = string.Empty;
        public List<string> Targets { get; } = new List<string>();
    }

    /// <summary>
    /// Guarda los ajustes en LocalSettings de la app (funciona en Xbox).
    /// Si LocalSettings no está disponible, usa memoria para no romper la app.
    /// </summary>
    public sealed class SettingsStore
    {
        private const string ObsHostKey = "plura.obs.host";
        private const string ObsPortKey = "plura.obs.port";
        private const string ObsPasswordKey = "plura.obs.password";
        private const string XboxModelKey = "plura.xbox.model";
        private const string StreamResolutionKey = "plura.stream.resolution";
        private const string StreamFpsKey = "plura.stream.fps";
        private const string StreamBitrateKey = "plura.stream.bitrate";
        private const string StreamPresetKey = "plura.stream.preset";
        private const string StreamAudioKey = "plura.stream.audio";
        private const string BackendUrlKey = "plura.backend.url";
        private const string PrimaryPlatformKey = "plura.primary.platform";
        private const string ConsoleModeKey = "plura.console.mode";
        private const string TwitchChannelKey = "plura.twitch.channel";
        private const string RelayActiveKey = "plura.relay.active";
        private const string RelayChannelKey = "plura.relay.channel";
        private const string RelayTargetsKey = "plura.relay.targets";
        private const string RelayUserStoppedKey = "plura.relay.userstopped";
        private const string AppBuildKey = "plura.app.build";
        private const string CurrentAppBuild = "1.1.0.0";

        private static readonly Dictionary<string, object> Fallback =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private static IPropertySet Values
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values;
                }
                catch
                {
                    return null;
                }
            }
        }

        public ObsSettings LoadObs()
        {
            return new ObsSettings
            {
                // Valores por defecto para este equipo. Se pueden cambiar en la app.
                Host = ReadString(ObsHostKey, "192.168.1.57"),
                Port = ReadInt(ObsPortKey, 4455),
                Password = ReadString(ObsPasswordKey, "plura123")
            };
        }

        public void SaveObs(string host, int port, string password)
        {
            Write(ObsHostKey, host ?? string.Empty);
            Write(ObsPortKey, port);
            Write(ObsPasswordKey, password ?? string.Empty);
        }

        public string LoadXboxModel()
        {
            return ReadString(XboxModelKey, "Xbox Series X");
        }

        public void SaveXboxModel(string model)
        {
            Write(XboxModelKey, model ?? "Xbox Series X");
        }

        public StreamSettings LoadStream()
        {
            return new StreamSettings
            {
                Resolution = ReadString(StreamResolutionKey, "1080"),
                Fps = ReadInt(StreamFpsKey, 60),
                Bitrate = ReadInt(StreamBitrateKey, 4500),
                Preset = ReadString(StreamPresetKey, "veryfast"),
                Audio = ReadBool(StreamAudioKey, true)
            };
        }

        public void SaveStream(StreamSettings settings)
        {
            if (settings == null) { return; }
            Write(StreamResolutionKey, settings.Resolution ?? "1080");
            Write(StreamFpsKey, settings.Fps);
            Write(StreamBitrateKey, settings.Bitrate);
            Write(StreamPresetKey, settings.Preset ?? "veryfast");
            Write(StreamAudioKey, settings.Audio);
        }

        public string LoadBackendUrl()
        {
            return ReadString(BackendUrlKey, "http://192.168.1.98:8787");
        }

        public void SaveBackendUrl(string url)
        {
            Write(BackendUrlKey, (url ?? string.Empty).Trim());
        }

        /// <summary>Id de la plataforma que ocupa el stream principal de OBS.</summary>
        public string LoadPrimaryPlatform()
        {
            return ReadString(PrimaryPlatformKey, string.Empty);
        }

        public void SavePrimaryPlatform(string id)
        {
            Write(PrimaryPlatformKey, id ?? string.Empty);
        }

        /// <summary>true = modo consola (Twitch + relay), false = modo OBS (PC).</summary>
        public bool LoadConsoleMode()
        {
            return ReadBool(ConsoleModeKey, false);
        }

        public void SaveConsoleMode(bool enabled)
        {
            Write(ConsoleModeKey, enabled);
        }

        public string LoadTwitchChannel()
        {
            return ReadString(TwitchChannelKey, string.Empty);
        }

        public void SaveTwitchChannel(string login)
        {
            Write(TwitchChannelKey, (login ?? string.Empty).Trim().ToLowerInvariant());
        }

        /// <summary>
        /// Guarda la intención de emitir (canal y destinos) para que la app pueda
        /// reanudar el relay cuando Xbox la suspende o la reinicia.
        /// </summary>
        public void SaveRelaySession(string channel, IEnumerable<string> targets)
        {
            var login = (channel ?? string.Empty).Trim().ToLowerInvariant();
            var ids = targets == null
                ? new List<string>()
                : targets.Where(t => !string.IsNullOrWhiteSpace(t))
                         .Select(t => t.Trim().ToLowerInvariant())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

            Write(RelayActiveKey, !string.IsNullOrWhiteSpace(login) && ids.Count > 0);
            Write(RelayChannelKey, login);
            Write(RelayTargetsKey, string.Join(",", ids));
        }

        public RelaySessionSettings LoadRelaySession()
        {
            var session = new RelaySessionSettings
            {
                Active = ReadBool(RelayActiveKey, false),
                Channel = ReadString(RelayChannelKey, string.Empty).Trim().ToLowerInvariant()
            };

            var raw = ReadString(RelayTargetsKey, string.Empty);
            foreach (var id in raw.Split(','))
            {
                var value = id.Trim().ToLowerInvariant();
                if (value.Length > 0) { session.Targets.Add(value); }
            }

            if (!session.Active || session.Channel.Length == 0 || session.Targets.Count == 0)
            {
                return new RelaySessionSettings();
            }
            return session;
        }

        public void ClearRelaySession()
        {
            Write(RelayActiveKey, false);
            Write(RelayChannelKey, string.Empty);
            Write(RelayTargetsKey, string.Empty);
        }

        /// <summary>
        /// true cuando el usuario pulsó Detener: evita que el arranque automático
        /// vuelva a encender un relay que se quiso apagar.
        /// </summary>
        public bool LoadRelayUserStopped()
        {
            return ReadBool(RelayUserStoppedKey, false);
        }

        public void SetRelayUserStopped(bool stopped)
        {
            Write(RelayUserStoppedKey, stopped);
        }

        /// <summary>
        /// Después de actualizar la app se descarta el "Detener" de la versión
        /// anterior: el objetivo es que cada nueva build vuelva a emitir sola
        /// si Twitch está en vivo. Los canales, claves y tokens se conservan.
        /// </summary>
        public void ApplyBuildMigration()
        {
            var previous = ReadString(AppBuildKey, string.Empty);
            if (string.Equals(previous, CurrentAppBuild, StringComparison.Ordinal)) { return; }

            Write(AppBuildKey, CurrentAppBuild);
            Write(RelayUserStoppedKey, false);
        }

        public PlatformSettings LoadPlatform(string id)
        {
            return new PlatformSettings
            {
                Enabled = ReadBool(Key(id, "enabled"), false),
                StreamKey = ReadString(Key(id, "key"), string.Empty),
                ServerUrl = ReadString(Key(id, "server"), string.Empty),
                TargetName = ReadString(Key(id, "target"), string.Empty),
                Mode = ReadString(Key(id, "mode"), "manual"),
                Linked = ReadBool(Key(id, "linked"), false),
                AccountLabel = ReadString(Key(id, "account"), string.Empty)
            };
        }

        public void SavePlatform(string id, PlatformSettings settings)
        {
            if (string.IsNullOrWhiteSpace(id) || settings == null) { return; }
            Write(Key(id, "enabled"), settings.Enabled);
            Write(Key(id, "key"), settings.StreamKey ?? string.Empty);
            Write(Key(id, "server"), settings.ServerUrl ?? string.Empty);
            Write(Key(id, "target"), settings.TargetName ?? string.Empty);
            Write(Key(id, "mode"), settings.Mode ?? "manual");
            Write(Key(id, "linked"), settings.Linked);
            Write(Key(id, "account"), settings.AccountLabel ?? string.Empty);
        }

        private static string Key(string id, string suffix)
        {
            return "plura.platform." + id + "." + suffix;
        }

        private static void Write(string key, object value)
        {
            var values = Values;
            if (values != null)
            {
                try
                {
                    values[key] = value;
                    return;
                }
                catch { }
            }

            Fallback[key] = value;
        }

        private static object Read(string key)
        {
            var values = Values;
            if (values != null)
            {
                try
                {
                    if (values.TryGetValue(key, out var value))
                    {
                        return value;
                    }
                }
                catch { }
            }

            return Fallback.TryGetValue(key, out var fallback) ? fallback : null;
        }

        private static string ReadString(string key, string fallback)
        {
            var value = Read(key);
            if (value == null) { return fallback; }
            var text = value as string;
            return text ?? value.ToString();
        }

        private static int ReadInt(string key, int fallback)
        {
            var value = Read(key);
            if (value is int) { return (int)value; }
            if (value is double) { return (int)(double)value; }
            if (value is string && int.TryParse((string)value, out var parsed)) { return parsed; }
            return fallback;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            var value = Read(key);
            if (value is bool) { return (bool)value; }
            if (value is string && bool.TryParse((string)value, out var parsed)) { return parsed; }
            return fallback;
        }
    }
}
