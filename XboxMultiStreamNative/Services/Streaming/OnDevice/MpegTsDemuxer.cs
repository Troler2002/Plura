using System;
using System.Collections.Generic;
using System.Linq;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>Mensaje de audio/vídeo listo para enviar por RTMP ( cuerpo de tag FLV ).</summary>
    public sealed class FlvMessage
    {
        public byte MessageType { get; set; }   // 8 = audio, 9 = vídeo
        public uint Timestamp { get; set; }
        public byte[] Payload { get; set; }
        public bool IsKeyframe { get; set; }
    }

    /// <summary>
    /// Convierte los segmentos MPEG-TS de HLS en mensajes FLV (H.264 AVCC + AAC RAW),
    /// sin decodificar ni recodificar. Es el equivalente en C# de `ffmpeg -c copy -f flv`.
    /// </summary>
    public sealed class MpegTsDemuxer
    {
        private const int PmtPidDefault = -1;

        private int _pmtPid = PmtPidDefault;
        private int _videoPid = -1;
        private int _audioPid = -1;

        private readonly List<byte> _videoPes = new List<byte>();
        private readonly List<byte> _audioPes = new List<byte>();
        private ulong _videoPts;
        private ulong _audioPts;
        private ulong _clockPts;
        private long _clockMs;
        private bool _hasClock;
        private bool _clockFromVideo;

        private byte[] _sps;
        private byte[] _pps;
        private byte[] _audioConfig;
        private byte[] _adtsRemainder = new byte[0];
        private bool _videoSequenceSent;
        private bool _audioSequenceSent;

        private readonly Dictionary<int, int> _continuity = new Dictionary<int, int>();

        public bool HasVideo { get { return _videoPid >= 0; } }
        public bool HasAudio { get { return _audioPid >= 0 && _audioConfig != null; } }
        public int PmtPid { get { return _pmtPid; } }
        public int VideoPid { get { return _videoPid; } }
        public int AudioPid { get { return _audioPid; } }

        public void Reset()
        {
            _pmtPid = PmtPidDefault;
            _videoPid = -1;
            _audioPid = -1;
            _videoPes.Clear();
            _audioPes.Clear();
            _videoPts = 0;
            _audioPts = 0;
            _clockPts = 0;
            _clockMs = 0;
            _hasClock = false;
            _clockFromVideo = false;
            _sps = null;
            _pps = null;
            _audioConfig = null;
            _adtsRemainder = new byte[0];
            _videoSequenceSent = false;
            _audioSequenceSent = false;
            _continuity.Clear();
        }

        /// <summary>Procesa un segmento TS y emite los mensajes FLV resultantes.</summary>
        public void Feed(byte[] data, Action<FlvMessage> emit)
        {
            if (data == null || data.Length == 0 || emit == null) { return; }

            int packetSize = DetectPacketSize(data);
            int start = packetSize == 192 ? 4 : 0;

            for (int offset = start; offset + packetSize <= data.Length; offset += packetSize)
            {
                int header = offset;
                if (data[header] != 0x47) { continue; }

                int pid = ((data[header + 1] & 0x1F) << 8) | data[header + 2];
                bool pusi = (data[header + 1] & 0x40) != 0;
                int afc = (data[header + 3] >> 4) & 0x03;
                int cc = data[header + 3] & 0x0F;

                int payloadStart = header + 4;
                if (afc == 0) { continue; }               // sin payload
                if (afc == 2 || afc == 3)
                {
                    int adaptationLength = data[payloadStart];
                    payloadStart += 1 + adaptationLength;
                }

                if (payloadStart >= header + packetSize) { continue; }

                // Un salto de continuidad invalida el PES que se estaba acumulando.
                if (_continuity.TryGetValue(pid, out var lastCc) &&
                    ((lastCc + 1) & 0x0F) != cc && pid != 0x1FFF)
                {
                    if (pid == _videoPid) { _videoPes.Clear(); }
                    if (pid == _audioPid) { _audioPes.Clear(); }
                }
                _continuity[pid] = cc;

                int payloadLength = header + packetSize - payloadStart;
                if (payloadLength <= 0) { continue; }

                if (pid == 0)
                {
                    ParsePat(data, payloadStart, payloadLength, pusi);
                }
                else if (pid == _pmtPid)
                {
                    ParsePmt(data, payloadStart, payloadLength, pusi);
                }
                else if (pid == _videoPid)
                {
                    int bodyStart = payloadStart;
                    if (pusi)
                    {
                        if (_videoPes.Count > 0) { FlushVideo(emit); }
                        _videoPes.Clear();
                        if (data[bodyStart] == 0 && data[bodyStart + 1] == 0 && data[bodyStart + 2] == 1)
                        {
                            _videoPts = ReadPesPts(data, bodyStart, payloadLength);
                            bodyStart = FindPesPayload(data, bodyStart, payloadLength);
                        }
                    }
                    if (bodyStart < header + packetSize)
                    {
                        for (int i = bodyStart; i < header + packetSize; i++) { _videoPes.Add(data[i]); }
                    }
                }
                else if (pid == _audioPid)
                {
                    int bodyStart = payloadStart;
                    if (pusi)
                    {
                        if (_audioPes.Count > 0) { FlushAudio(emit); }
                        _audioPes.Clear();
                        if (data[bodyStart] == 0 && data[bodyStart + 1] == 0 && data[bodyStart + 2] == 1)
                        {
                            _audioPts = ReadPesPts(data, bodyStart, payloadLength);
                            bodyStart = FindPesPayload(data, bodyStart, payloadLength);
                        }
                    }
                    if (bodyStart < header + packetSize)
                    {
                        for (int i = bodyStart; i < header + packetSize; i++) { _audioPes.Add(data[i]); }
                    }
                }
            }

            // Los segmentos terminan justo después del último PES: lo emitimos.
            FlushVideo(emit);
            FlushAudio(emit);
        }

        public void Flush(Action<FlvMessage> emit)
        {
            FlushVideo(emit);
            FlushAudio(emit);
        }

        private static int DetectPacketSize(byte[] data)
        {
            // En M2TS los paquetes de 188 bytes llevan 4 bytes de timestamp delante.
            // En TS normal el byte 188 es simply el inicio del siguiente paquete,
            // así que comprobar solo ese byte daría un falso positivo de 192.
            if (data.Length >= 200 && data[0] != 0x47 && data[4] == 0x47 &&
                (data.Length < 400 || data[196] == 0x47))
            {
                return 192;
            }
            return 188;
        }

        private void ParsePat(byte[] data, int start, int length, bool pusi)
        {
            if (!pusi || length < 9) { return; }

            // Cuando payload_unit_start_indicator está activo, el primer byte del
            // payload es un pointer_field. Las tablas PSI lo llevan siempre; el
            // Analizador anterior lo confundía con el table_id y nunca encontraba
            // el PID de la PMT, por lo que no había vídeo ni audio.
            int p = start;
            if (!(data[p] == 0x00 && p + 3 < start + length && (data[p + 1] & 0x80) != 0))
            {
                // Con pointer_field el table_id está desplazado; sin él (algún
                // generador) el payload empieza directamente con la tabla.
                p = start + 1 + data[start];
            }
            if (p + 8 > start + length) { return; }
            if (data[p] != 0x00) { return; }                  // table_id PAT
            if ((data[p + 1] & 0x80) == 0) { return; }

            int sectionLength = ((data[p + 1] & 0x0F) << 8) | data[p + 2];
            int sectionEnd = Math.Min(start + length, p + 3 + sectionLength);
            p += 8;                                           // cabecera PAT antes de las entradas

            while (p + 4 <= sectionEnd)
            {
                int program = (data[p] << 8) | data[p + 1];
                int pid = ((data[p + 2] & 0x1F) << 8) | data[p + 3];
                if (program != 0 && pid != 0x1FFF)
                {
                    if (pid != _pmtPid)
                    {
                        _pmtPid = pid;
                        _videoPid = -1;
                        _audioPid = -1;
                    }
                    return;
                }
                p += 4;
            }
        }

        private void ParsePmt(byte[] data, int start, int length, bool pusi)
        {
            if (!pusi || length < 13) { return; }

            int p = data[start] == 0x02 ? start : start + 1 + data[start];
            if (p + 12 > start + length) { return; }
            if (data[p] != 0x02) { return; }                  // table_id PMT
            if ((data[p + 1] & 0x80) == 0) { return; }

            int sectionLength = ((data[p + 1] & 0x0F) << 8) | data[p + 2];
            int sectionEnd = Math.Min(start + length, p + 3 + sectionLength);
            int programInfoLength = ((data[p + 10] & 0x0F) << 8) | data[p + 11];
            p += 12 + programInfoLength;

            while (p + 5 <= sectionEnd)
            {
                int streamType = data[p];
                int pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
                int infoLength = ((data[p + 3] & 0x0F) << 8) | data[p + 4];

                if (streamType == 0x1B || streamType == 0x1F) { _videoPid = pid; }
                else if (streamType == 0x0F || streamType == 0x11) { _audioPid = pid; }

                p += 5 + infoLength;
            }
        }

        private static ulong ReadPesPts(byte[] data, int start, int length)
        {
            if (length < 14 || (data[start + 7] & 0x80) == 0) { return 0; }
            int o = start + 9;
            uint a = (uint)((data[o] >> 1) & 0x07);
            uint b = data[o + 1];
            uint c = (uint)((data[o + 2] >> 1) & 0x07);
            uint d = data[o + 3];
            uint e = (uint)((data[o + 4] >> 1) & 0x07);
            return ((ulong)a << 30) |
                   ((ulong)b << 22) |
                   ((ulong)c << 15) |
                   ((ulong)d << 7) |
                   e;
        }

        private static int FindPesPayload(byte[] data, int start, int length)
        {
            if (length < 9) { return start + length; }
            int headerLength = data[start + 8];
            int result = start + 9 + headerLength;
            return result <= start + length ? result : start + length;
        }

        private uint AdvanceClock(ulong pts, bool fromVideo)
        {
            if (pts == 0) { return (uint)(_clockMs & 0xFFFFFFFF); }

            // El reloj lo marca el vídeo cuando existe; el audio solo lo usa
            // (o lo marca si el stream no tiene vídeo) para no contar dos veces.
            if (!fromVideo && _clockFromVideo) { return (uint)(_clockMs & 0xFFFFFFFF); }

            if (!_hasClock || _clockFromVideo != fromVideo)
            {
                _hasClock = true;
                _clockFromVideo = fromVideo;
                _clockPts = pts;
                return (uint)(_clockMs & 0xFFFFFFFF);
            }

            ulong delta = (pts - _clockPts) & 0x1FFFFFFFFUL;   // PTS de 33 bits
            if (delta < 90UL * 60 * 10)                        // máximo 10 minutos
            {
                _clockMs += (long)(delta / 90UL);
            }
            _clockPts = pts;
            return (uint)(_clockMs & 0xFFFFFFFF);
        }

        private void FlushVideo(Action<FlvMessage> emit)
        {
            if (_videoPes.Count == 0) { return; }
            var pes = _videoPes.ToArray();
            _videoPes.Clear();

            var nalUnits = SplitAnnexB(pes);
            if (nalUnits.Count == 0) { return; }

            foreach (var nal in nalUnits)
            {
                if (nal.Length == 0) { continue; }
                int type = nal[0] & 0x1F;
                if (type == 7) { _sps = nal; }
                else if (type == 8) { _pps = nal; }
            }

            if (_sps == null || _pps == null) { return; }

            uint timestamp = AdvanceClock(_videoPts, true);
            bool keyframe = nalUnits.Any(n => n.Length > 0 && (n[0] & 0x1F) == 5);

            if (!_videoSequenceSent)
            {
                var config = BuildAvcSequenceHeader(_sps, _pps);
                var header = new byte[5 + config.Length];
                header[0] = 0x17;                              // keyframe + AVC
                header[1] = 0x00;                              // AVC sequence header
                Array.Copy(config, 0, header, 5, config.Length);
                emit(new FlvMessage
                {
                    MessageType = 9,
                    Timestamp = timestamp,
                    Payload = header,
                    IsKeyframe = true
                });
                _videoSequenceSent = true;
            }

            using (var stream = new System.IO.MemoryStream())
            {
                foreach (var nal in nalUnits)
                {
                    int type = nal.Length == 0 ? 0 : nal[0] & 0x1F;
                    if (type == 7 || type == 8 || type == 9 || type == 6) { continue; }
                    WriteFourBytes(stream, nal.Length);
                    stream.Write(nal, 0, nal.Length);
                }

                var avcc = stream.ToArray();
                if (avcc.Length == 0) { return; }

                var payload = new byte[5 + avcc.Length];
                payload[0] = (byte)(keyframe ? 0x17 : 0x27);
                payload[1] = 0x01;                             // AVC NALU
                Array.Copy(avcc, 0, payload, 5, avcc.Length);
                emit(new FlvMessage
                {
                    MessageType = 9,
                    Timestamp = timestamp,
                    Payload = payload,
                    IsKeyframe = keyframe
                });
            }
        }

        private void FlushAudio(Action<FlvMessage> emit)
        {
            if (_audioPes.Count > 0)
            {
                var pes = _audioPes.ToArray();
                _audioPes.Clear();
                var combined = new byte[_adtsRemainder.Length + pes.Length];
                Array.Copy(_adtsRemainder, combined, _adtsRemainder.Length);
                Array.Copy(pes, 0, combined, _adtsRemainder.Length, pes.Length);
                _adtsRemainder = combined;
            }

            uint baseTimestamp = AdvanceClock(_audioPts, false);
            uint timestamp = baseTimestamp;
            long elapsedMs = 0;
            int consumed = 0;

            while (consumed + 7 <= _adtsRemainder.Length)
            {
                if (_adtsRemainder[consumed] != 0xFF || (_adtsRemainder[consumed + 1] & 0xF0) != 0xF0)
                {
                    consumed++;
                    continue;
                }

                bool hasCrc = (_adtsRemainder[consumed + 1] & 0x01) == 0;
                int headerLength = hasCrc ? 9 : 7;
                if (consumed + headerLength > _adtsRemainder.Length) { break; }

                int frameLength =
                    ((_adtsRemainder[consumed + 3] & 0x03) << 11) |
                    (_adtsRemainder[consumed + 4] << 3) |
                    (_adtsRemainder[consumed + 5] >> 5);
                if (frameLength <= headerLength || consumed + frameLength > _adtsRemainder.Length) { break; }

                int profile = (_adtsRemainder[consumed + 2] >> 6) & 0x03;
                int frequencyIndex = (_adtsRemainder[consumed + 2] >> 2) & 0x0F;
                int channelConfig = ((_adtsRemainder[consumed + 2] & 0x01) << 2) |
                                    ((_adtsRemainder[consumed + 3] >> 6) & 0x03);
                var config = BuildAudioSpecificConfig(profile, frequencyIndex, channelConfig);
                if (config != null && !config.SequenceEqual(_audioConfig ?? new byte[0]))
                {
                    _audioConfig = config;
                    _audioSequenceSent = false;
                }

                if (_audioConfig != null && !_audioSequenceSent)
                {
                    var header = new byte[2 + _audioConfig.Length];
                    header[0] = 0xAF;                          // AAC, 44,1 kHz, 16 bit, estéreo
                    header[1] = 0x00;                          // AAC sequence header
                    Array.Copy(_audioConfig, 0, header, 2, _audioConfig.Length);
                    emit(new FlvMessage
                    {
                        MessageType = 8,
                        Timestamp = baseTimestamp,
                        Payload = header
                    });
                    _audioSequenceSent = true;
                }

                int dataLength = frameLength - headerLength;
                if (_audioConfig != null && dataLength > 0)
                {
                    var payload = new byte[2 + dataLength];
                    payload[0] = 0xAF;
                    payload[1] = 0x01;                         // AAC raw data
                    Array.Copy(_adtsRemainder, consumed + headerLength, payload, 2, dataLength);
                    emit(new FlvMessage
                    {
                        MessageType = 8,
                        Timestamp = timestamp,
                        Payload = payload
                    });
                }

                int sampleRate = AdtsSampleRate(frequencyIndex);
                if (sampleRate > 0) { elapsedMs += 1024 * 1000 / sampleRate; }
                timestamp = baseTimestamp + (uint)elapsedMs;
                consumed += frameLength;
            }

            if (consumed > 0)
            {
                var remaining = new byte[_adtsRemainder.Length - consumed];
                Array.Copy(_adtsRemainder, consumed, remaining, 0, remaining.Length);
                _adtsRemainder = remaining;
            }

            // Evita acumular basura si el stream de audio no es ADTS.
            if (_adtsRemainder.Length > 64 * 1024) { _adtsRemainder = new byte[0]; }
        }

        private static int AdtsSampleRate(int index)
        {
            int[] rates =
            {
                96000, 88200, 64000, 48000, 44100, 32000,
                24000, 22050, 16000, 12000, 11025, 8000, 7350
            };
            return index >= 0 && index < rates.Length ? rates[index] : 0;
        }

        private static List<byte[]> SplitAnnexB(byte[] data)
        {
            var result = new List<byte[]>();
            var current = new List<byte>();
            int i = 0;

            while (i < data.Length)
            {
                int startCodeLength = 0;
                if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                }
                else if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    startCodeLength = 3;
                }

                if (startCodeLength > 0)
                {
                    if (current.Count > 0) { result.Add(current.ToArray()); current.Clear(); }
                    i += startCodeLength;
                    continue;
                }

                current.Add(data[i]);
                i++;
            }

            if (current.Count > 0) { result.Add(current.ToArray()); }
            return result;
        }

        private static byte[] BuildAvcSequenceHeader(byte[] sps, byte[] pps)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                stream.WriteByte(1);                           // configurationVersion
                stream.WriteByte(sps.Length > 1 ? sps[1] : (byte)100);
                stream.WriteByte(sps.Length > 2 ? sps[2] : (byte)0);
                stream.WriteByte(sps.Length > 3 ? sps[3] : (byte)40);
                stream.WriteByte(0xFF);                        // lengthSizeMinusOne = 3
                stream.WriteByte(0xE1);                        // 1 SPS
                WriteTwoBytes(stream, sps.Length);
                stream.Write(sps, 0, sps.Length);
                stream.WriteByte(1);                           // 1 PPS
                WriteTwoBytes(stream, pps.Length);
                stream.Write(pps, 0, pps.Length);
                return stream.ToArray();
            }
        }

        private static byte[] BuildAudioSpecificConfig(int profile, int frequencyIndex, int channelConfig)
        {
            if (frequencyIndex >= 15 || channelConfig == 0) { return null; }
            int audioObjectType = profile + 1;
            return new byte[]
            {
                (byte)((audioObjectType << 3) | (frequencyIndex >> 1)),
                (byte)(((frequencyIndex & 1) << 7) | (channelConfig << 3))
            };
        }

        private static void WriteTwoBytes(System.IO.Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteFourBytes(System.IO.Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }
}
