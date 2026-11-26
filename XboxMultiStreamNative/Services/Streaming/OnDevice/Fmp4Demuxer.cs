using System;
using System.Collections.Generic;
using System.Linq;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Convierte los segmentos fragmented-MP4 (CMAF) que sirve Twitch hoy en día
    /// en mensajes FLV listos para RTMP, sin decodificar ni recodificar.
    /// El init segment (#EXT-X-MAP) aporta avcC/esds; cada media segment aporta
    /// moof/trun con las muestras y mdat con los bytes.
    /// </summary>
    public sealed class Fmp4Demuxer
    {
        private int _videoTrackId = -1;
        private int _audioTrackId = -1;
        private uint _videoTimescale = 90000;
        private uint _audioTimescale = 48000;
        private byte[] _avcConfig;
        private byte[] _audioConfig;
        private bool _videoSequenceSent;
        private bool _audioSequenceSent;
        private long _videoTime;
        private long _audioTime;

        public bool Initialized { get { return _avcConfig != null || _audioConfig != null; } }

        public void SetInit(byte[] init)
        {
            if (init == null || init.Length == 0) { return; }

            _videoTrackId = -1;
            _audioTrackId = -1;
            _avcConfig = null;
            _audioConfig = null;
            _videoSequenceSent = false;
            _audioSequenceSent = false;
            _videoTime = 0;
            _audioTime = 0;

            foreach (var moov in Boxes(init, 0, init.Length).Where(b => b.Type == "moov"))
            {
                foreach (var trak in Boxes(init, moov.DataStart, moov.End).Where(b => b.Type == "trak"))
                {
                    var hdlr = FindBox(init, trak, "hdlr", 4);
                    if (hdlr == null || hdlr.End - hdlr.DataStart < 12) { continue; }
                    string handler = FourCC(init, hdlr.DataStart + 8);

                    var mdhd = FindBox(init, trak, "mdhd", 4);
                    uint timescale = mdhd == null ? 0 : ReadTimescale(init, mdhd);

                    var stbl = FindBox(init, trak, "stbl", 6);
                    if (stbl == null) { continue; }
                    var stsd = FindBox(init, stbl, "stsd", 1);
                    if (stsd == null || stsd.End - stsd.DataStart < 8) { continue; }

                    int entryStart = stsd.DataStart + 8;   // version/flags + entryCount
                    var entry = Boxes(init, entryStart, stsd.End).FirstOrDefault();
                    if (entry == null) { continue; }

                    if (handler == "vide" && (entry.Type == "avc1" || entry.Type == "avc3"))
                    {
                        // VisualSampleEntry tiene 78 bytes de campos fijos antes de avcC.
                        var avcC = FindBoxInRange(init, entry.DataStart + 78, entry.End, "avcC", 2);
                        if (avcC == null) { continue; }
                        _videoTrackId = ReadTrackId(init, trak);
                        _videoTimescale = timescale > 0 ? timescale : 90000;
                        _avcConfig = Slice(init, avcC.DataStart, avcC.End - avcC.DataStart);
                    }
                    else if (handler == "soun" && entry.Type == "mp4a")
                    {
                        // AudioSampleEntry tiene 28 bytes de campos fijos antes de esds.
                        var esds = FindBoxInRange(init, entry.DataStart + 28, entry.End, "esds", 3);
                        if (esds == null) { continue; }
                        var asc = FindDecoderSpecificInfo(init, esds.DataStart, esds.End);
                        if (asc == null || asc.Length == 0) { continue; }
                        _audioTrackId = ReadTrackId(init, trak);
                        _audioTimescale = timescale > 0 ? timescale : 48000;
                        _audioConfig = asc;
                    }
                }
            }
        }

        public void Reset()
        {
            _videoSequenceSent = false;
            _audioSequenceSent = false;
            _videoTime = 0;
            _audioTime = 0;
        }

        public void Clear()
        {
            Reset();
            _videoTrackId = -1;
            _audioTrackId = -1;
            _avcConfig = null;
            _audioConfig = null;
        }

        public void Feed(byte[] segment, Action<FlvMessage> emit)
        {
            if (segment == null || segment.Length == 0 || emit == null) { return; }
            if (_videoTrackId < 0 && _audioTrackId < 0) { return; }

            var top = Boxes(segment, 0, segment.Length).ToList();

            foreach (var moof in top.Where(b => b.Type == "moof"))
            {
                foreach (var traf in Boxes(segment, moof.DataStart, moof.End).Where(b => b.Type == "traf"))
                {
                    var tfhd = FindBox(segment, traf, "tfhd", 1);
                    if (tfhd == null || tfhd.End - tfhd.DataStart < 8) { continue; }
                    int trackTime = (int)ReadU32(segment, tfhd.DataStart + 4);
                    if (trackTime != _videoTrackId && trackTime != _audioTrackId) { continue; }

                    uint flags = ReadU32(segment, tfhd.DataStart);
                    bool defaultBaseIsMoof = (flags & 0x020000) != 0;
                    int tfhdPos = tfhd.DataStart + 8;
                    long baseDataOffset = 0;
                    if ((flags & 0x000001) != 0 && tfhdPos + 8 <= tfhd.End)
                    {
                        baseDataOffset = (long)ReadU64(segment, tfhdPos);
                        tfhdPos += 8;
                    }
                    if ((flags & 0x000002) != 0) { tfhdPos += 4; }
                    uint defaultDuration = 0;
                    uint defaultSize = 0;
                    uint defaultFlags = 0;
                    if ((flags & 0x000008) != 0 && tfhdPos + 4 <= tfhd.End)
                    {
                        defaultDuration = ReadU32(segment, tfhdPos);
                        tfhdPos += 4;
                    }
                    if ((flags & 0x000010) != 0 && tfhdPos + 4 <= tfhd.End)
                    {
                        defaultSize = ReadU32(segment, tfhdPos);
                        tfhdPos += 4;
                    }
                    if ((flags & 0x000020) != 0 && tfhdPos + 4 <= tfhd.End)
                    {
                        defaultFlags = ReadU32(segment, tfhdPos);
                    }

                    var tfdt = FindBox(segment, traf, "tfdt", 1);
                    long decodeTime = DecodeTime(segment, tfdt, trackTime == _videoTrackId ? _videoTime : _audioTime);

                    foreach (var trun in Boxes(segment, traf.DataStart, traf.End).Where(b => b.Type == "trun"))
                    {
                        if (trun.End - trun.DataStart < 8) { continue; }
                        uint trunFlags = ReadU32(segment, trun.DataStart);
                        int sampleCount = (int)ReadU32(segment, trun.DataStart + 4);
                        if (sampleCount <= 0 || sampleCount > 100000) { continue; }

                        int p = trun.DataStart + 8;
                        long dataOffset = 0;
                        if ((trunFlags & 0x000001) != 0 && p + 4 <= trun.End)
                        {
                            dataOffset = (int)ReadU32(segment, p);
                            p += 4;
                        }
                        uint firstFlags = 0;
                        if ((trunFlags & 0x000004) != 0 && p + 4 <= trun.End)
                        {
                            firstFlags = ReadU32(segment, p);
                            p += 4;
                        }

                        long samplePos = defaultBaseIsMoof || (trunFlags & 0x000001) != 0
                            ? moof.Start + baseDataOffset + dataOffset
                            : baseDataOffset + dataOffset;

                        bool isVideo = trackTime == _videoTrackId;
                        uint timescale = isVideo ? _videoTimescale : _audioTimescale;
                        long time = decodeTime;

                        for (int i = 0; i < sampleCount; i++)
                        {
                            uint duration = (trunFlags & 0x000100) != 0 && p + 4 <= trun.End
                                ? ReadU32(segment, p) : defaultDuration;
                            if ((trunFlags & 0x000100) != 0) { p += 4; }

                            uint size = (trunFlags & 0x000200) != 0 && p + 4 <= trun.End
                                ? ReadU32(segment, p) : defaultSize;
                            if ((trunFlags & 0x000200) != 0) { p += 4; }

                            uint sampleFlags = defaultFlags;
                            if ((trunFlags & 0x000400) != 0 && p + 4 <= trun.End)
                            {
                                sampleFlags = ReadU32(segment, p);
                                p += 4;
                            }
                            else if (i == 0) { sampleFlags = firstFlags; }

                            if ((trunFlags & 0x000800) != 0 && p + 4 <= trun.End) { p += 4; }

                            if (size > 0 && samplePos >= 0 && samplePos + size <= segment.Length)
                            {
                                var sample = Slice(segment, (int)samplePos, (int)size);
                                uint timestamp = (uint)(time * 1000 / timescale);
                                if (isVideo) { EmitVideo(sample, timestamp, sampleFlags, emit); }
                                else { EmitAudio(sample, timestamp, emit); }
                            }

                            samplePos += size;
                            time += duration;
                        }

                        if (isVideo) { _videoTime = time; }
                        else { _audioTime = time; }
                    }
                }
            }
        }

        private void EmitVideo(byte[] sample, uint timestamp, uint flags, Action<FlvMessage> emit)
        {
            if (_avcConfig == null || sample.Length == 0) { return; }

            if (!_videoSequenceSent)
            {
                var header = new byte[5 + _avcConfig.Length];
                header[0] = 0x17;
                header[1] = 0x00;
                Array.Copy(_avcConfig, 0, header, 5, _avcConfig.Length);
                emit(new FlvMessage
                {
                    MessageType = 9,
                    Timestamp = timestamp,
                    Payload = header,
                    IsKeyframe = true
                });
                _videoSequenceSent = true;
            }

            bool nonSync = (flags & 0x00010000) != 0;
            bool keyframe = !nonSync || StartsWithKeyframe(sample);
            var payload = new byte[5 + sample.Length];
            payload[0] = (byte)(keyframe ? 0x17 : 0x27);
            payload[1] = 0x01;
            Array.Copy(sample, 0, payload, 5, sample.Length);
            emit(new FlvMessage
            {
                MessageType = 9,
                Timestamp = timestamp,
                Payload = payload,
                IsKeyframe = keyframe
            });
        }

        private void EmitAudio(byte[] sample, uint timestamp, Action<FlvMessage> emit)
        {
            if (_audioConfig == null || sample.Length == 0) { return; }

            if (!_audioSequenceSent)
            {
                var header = new byte[2 + _audioConfig.Length];
                header[0] = 0xAF;
                header[1] = 0x00;
                Array.Copy(_audioConfig, 0, header, 2, _audioConfig.Length);
                emit(new FlvMessage
                {
                    MessageType = 8,
                    Timestamp = timestamp,
                    Payload = header
                });
                _audioSequenceSent = true;
            }

            var payload = new byte[2 + sample.Length];
            payload[0] = 0xAF;
            payload[1] = 0x01;
            Array.Copy(sample, 0, payload, 2, sample.Length);
            emit(new FlvMessage
            {
                MessageType = 8,
                Timestamp = timestamp,
                Payload = payload
            });
        }

        private static bool StartsWithKeyframe(byte[] sample)
        {
            if (sample.Length < 5) { return false; }
            int nalLength = (int)ReadU32(sample, 0);
            if (nalLength <= 0 || nalLength > sample.Length - 4) { return false; }
            return (sample[4] & 0x1F) == 5;
        }

        private static long DecodeTime(byte[] data, Box box, long fallback)
        {
            if (box == null || box.End - box.DataStart < 8) { return fallback; }
            int version = data[box.DataStart];
            if (version == 1 && box.End - box.DataStart >= 16)
            {
                return (long)ReadU64(data, box.DataStart + 8);
            }
            if (version == 0 && box.End - box.DataStart >= 12)
            {
                return ReadU32(data, box.DataStart + 8);
            }
            return fallback;
        }

        private static uint ReadTimescale(byte[] data, Box box)
        {
            int version = data[box.DataStart];
            int offset = version == 1 ? 20 : 12;
            return box.DataStart + offset + 4 <= box.End ? ReadU32(data, box.DataStart + offset) : 0;
        }

        private static int ReadTrackId(byte[] data, Box trak)
        {
            var tkhd = FindBox(data, trak, "tkhd", 3);
            if (tkhd == null) { return -1; }
            int version = data[tkhd.DataStart];
            int offset = version == 1 ? 20 : 12;
            return tkhd.DataStart + offset + 4 <= tkhd.End
                ? (int)ReadU32(data, tkhd.DataStart + offset)
                : -1;
        }

        private static byte[] FindDecoderSpecificInfo(byte[] data, int start, int end)
        {
            int p = start;
            while (p < end)
            {
                byte tag = data[p++];
                uint length = ReadExpandableLength(data, ref p, end);
                if (p + length > end) { return null; }
                if (tag == 0x05) { return Slice(data, p, (int)length); }
                if (tag == 0x03 && length > 3)
                {
                    var nested = FindDecoderSpecificInfo(data, p + 3, p + (int)length);
                    if (nested != null) { return nested; }
                }
                else if (tag == 0x04 && length > 13)
                {
                    var nested = FindDecoderSpecificInfo(data, p + 13, p + (int)length);
                    if (nested != null) { return nested; }
                }
                p += (int)length;
            }
            return null;
        }

        private static uint ReadExpandableLength(byte[] data, ref int p, int end)
        {
            uint result = 0;
            for (int i = 0; i < 4 && p < end; i++)
            {
                byte b = data[p++];
                result = (result << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) { return result; }
            }
            return result;
        }

        private static Box FindBox(byte[] data, Box parent, string type, int depth)
        {
            return FindBoxInRange(data, parent.DataStart, parent.End, type, depth);
        }

        private static Box FindBoxInRange(byte[] data, int start, int end, string type, int depth)
        {
            if (depth <= 0 || start < 0 || end > data.Length || start >= end) { return null; }
            foreach (var box in Boxes(data, start, end))
            {
                if (box.Type == type) { return box; }
                var nested = FindBoxInRange(data, box.DataStart, box.End, type, depth - 1);
                if (nested != null) { return nested; }
            }
            return null;
        }

        private static List<Box> Boxes(byte[] data, int start, int end)
        {
            var result = new List<Box>();
            int p = start;
            while (p + 8 <= end)
            {
                long size = ReadU32(data, p);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > end) { break; }
                    size = (long)ReadU64(data, p + 8);
                    header = 16;
                }
                else if (size == 0) { size = end - p; }
                if (size < header || p + size > end) { break; }
                result.Add(new Box
                {
                    Type = FourCC(data, p + 4),
                    Start = p,
                    DataStart = p + header,
                    End = p + (int)size
                });
                p += (int)size;
            }
            return result;
        }

        private static string FourCC(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) { return string.Empty; }
            return new string(new[]
            {
                (char)data[offset], (char)data[offset + 1],
                (char)data[offset + 2], (char)data[offset + 3]
            });
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        private static ulong ReadU64(byte[] data, int offset)
        {
            return ((ulong)ReadU32(data, offset) << 32) | ReadU32(data, offset + 4);
        }

        private static byte[] Slice(byte[] data, int start, int length)
        {
            var result = new byte[length];
            Array.Copy(data, start, result, 0, length);
            return result;
        }

        private sealed class Box
        {
            public string Type;
            public int Start;
            public int DataStart;
            public int End;
        }
    }
}
