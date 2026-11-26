using System;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Acepta los dos formatos que Twitch puede servir por HLS: MPEG-TS clásico
    /// y fragmented-MP4/CMAF (el formato actual). Expone una única interfaz.
    /// </summary>
    public sealed class UnifiedDemuxer
    {
        private readonly MpegTsDemuxer _ts = new MpegTsDemuxer();
        private readonly Fmp4Demuxer _fmp4 = new Fmp4Demuxer();
        private bool _fmp4Mode;
        private bool _modeDetected;

        public bool IsFmp4 { get { return _fmp4Mode; } }
        public bool HasInit { get { return _fmp4.Initialized; } }
        public int TsPmtPid { get { return _ts.PmtPid; } }
        public int TsVideoPid { get { return _ts.VideoPid; } }
        public int TsAudioPid { get { return _ts.AudioPid; } }

        public void SetInit(byte[] init)
        {
            if (init == null || init.Length == 0) { return; }
            _fmp4Mode = true;
            _modeDetected = true;
            _fmp4.SetInit(init);
        }

        public void Feed(byte[] data, Action<FlvMessage> emit)
        {
            if (data == null || data.Length == 0 || emit == null) { return; }
            if (!_modeDetected)
            {
                _fmp4Mode = LooksLikeFmp4(data);
                _modeDetected = true;
            }

            if (_fmp4Mode) { _fmp4.Feed(data, emit); }
            else { _ts.Feed(data, emit); }
        }

        public void Reset(bool keepInit)
        {
            _ts.Reset();
            if (keepInit) { _fmp4.Reset(); }
            else
            {
                _fmp4.Clear();
                _modeDetected = false;
                _fmp4Mode = false;
            }
        }

        private static bool LooksLikeFmp4(byte[] data)
        {
            if (data.Length < 8 || data[0] != 0) { return false; }
            int type = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
            return type == 0x73747970 ||  // styp
                   type == 0x6D6F6F66 ||  // moof
                   type == 0x66747970;    // ftyp
        }
    }
}
