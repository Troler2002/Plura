using System;
using System.Collections.Generic;

namespace Plura.Services.Streaming.OnDevice
{
    public sealed class OnDeviceRelayException : Exception
    {
        public OnDeviceRelayException(string message) : base(message) { }
        public OnDeviceRelayException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Datos del canal leídos directamente desde Twitch.</summary>
    public sealed class TwitchChannelInfo
    {
        public string Login { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string AvatarBase64 { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool Live { get; set; }
        public int Viewers { get; set; }
    }

    /// <summary>Lista de segmentos HLS y, si existe, su initialization segment.</summary>
    public sealed class HlsPlaylist
    {
        public List<Uri> Segments { get; set; } = new List<Uri>();
        public Uri Init { get; set; }
        public int MediaSequence { get; set; }
        public double TargetDuration { get; set; } = 2;
    }
}
