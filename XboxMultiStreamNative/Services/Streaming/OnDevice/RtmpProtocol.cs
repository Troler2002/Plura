using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Plura.Services.Streaming.OnDevice
{
    public sealed class RtmpMessage
    {
        public byte Type { get; set; }
        public uint Timestamp { get; set; }
        public uint StreamId { get; set; }
        public byte[] Payload { get; set; } = new byte[0];
    }

    public sealed class AmfObject
    {
        public readonly List<KeyValuePair<string, object>> Properties =
            new List<KeyValuePair<string, object>>();

        public AmfObject Set(string key, object value)
        {
            Properties.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
    }

    /// <summary>Array asociativo AMF0 (marker 0x08), usado en onMetaData.</summary>
    public sealed class AmfEcmaArray
    {
        public readonly List<KeyValuePair<string, object>> Properties =
            new List<KeyValuePair<string, object>>();

        public AmfEcmaArray Set(string key, object value)
        {
            Properties.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
    }

    /// <summary>Codificador/decodificador AMF0 mínimo para comandos RTMP.</summary>
    public static class Amf0
    {
        public static byte[] Encode(params object[] values)
        {
            using (var stream = new MemoryStream())
            {
                foreach (var value in values) { Write(stream, value); }
                return stream.ToArray();
            }
        }

        private static void Write(Stream stream, object value)
        {
            if (value == null)
            {
                stream.WriteByte(0x05);                        // null
            }
            else if (value is string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.WriteByte(0x02);
                WriteTwo(stream, bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            else if (value is bool flag)
            {
                stream.WriteByte(0x01);
                stream.WriteByte(flag ? (byte)1 : (byte)0);
            }
            else if (value is byte || value is short || value is int || value is long ||
                     value is float || value is double || value is decimal)
            {
                stream.WriteByte(0x00);
                var bytes = BitConverter.GetBytes(Convert.ToDouble(value));
                if (BitConverter.IsLittleEndian) { Array.Reverse(bytes); }
                stream.Write(bytes, 0, 8);
            }
            else if (value is AmfObject obj)
            {
                stream.WriteByte(0x03);
                WriteProperties(stream, obj.Properties);
            }
            else if (value is AmfEcmaArray array)
            {
                stream.WriteByte(0x08);
                WriteFour(stream, array.Properties.Count);
                WriteProperties(stream, array.Properties);
            }
            else
            {
                Write(stream, value.ToString());
            }
        }

        private static void WriteProperties(Stream stream, List<KeyValuePair<string, object>> properties)
        {
            foreach (var property in properties)
            {
                var name = Encoding.UTF8.GetBytes(property.Key ?? string.Empty);
                WriteTwo(stream, name.Length);
                stream.Write(name, 0, name.Length);
                Write(stream, property.Value);
            }
            WriteTwo(stream, 0);
            stream.WriteByte(0x09);                            // end of object
        }

        public static object Read(Stream stream)
        {
            int marker = stream.ReadByte();
            if (marker < 0) { return null; }

            switch (marker)
            {
                case 0x00:
                {
                    var bytes = ReadExactly(stream, 8);
                    if (BitConverter.IsLittleEndian) { Array.Reverse(bytes); }
                    return BitConverter.ToDouble(bytes, 0);
                }
                case 0x01:
                    return stream.ReadByte() != 0;
                case 0x02:
                case 0x0C:
                {
                    int length = marker == 0x0C ? ReadInt32(stream) : ReadUInt16(stream);
                    return Encoding.UTF8.GetString(ReadExactly(stream, length));
                }
                case 0x03:
                case 0x08:
                {
                    if (marker == 0x08) { ReadInt32(stream); }
                    var obj = new AmfObject();
                    while (true)
                    {
                        int keyLength = ReadUInt16(stream);
                        if (keyLength == 0)
                        {
                            stream.ReadByte();                 // 0x09
                            break;
                        }
                        var key = Encoding.UTF8.GetString(ReadExactly(stream, keyLength));
                        obj.Set(key, Read(stream));
                    }
                    return obj;
                }
                case 0x05:
                case 0x06:
                    return null;
                case 0x0A:                                     // strict array
                {
                    int count = ReadInt32(stream);
                    var list = new List<object>();
                    for (int i = 0; i < count; i++) { list.Add(Read(stream)); }
                    return list;
                }
                case 0x0B:                                     // date
                    ReadExactly(stream, 10);
                    return null;
                default:
                    return null;
            }
        }

        public static string CommandName(byte[] payload)
        {
            if (payload == null || payload.Length < 2) { return string.Empty; }
            try
            {
                using (var stream = new MemoryStream(payload))
                {
                    return Read(stream) as string ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static List<object> ReadValues(byte[] payload, int count)
        {
            var result = new List<object>();
            if (payload == null) { return result; }
            try
            {
                using (var stream = new MemoryStream(payload))
                {
                    for (int i = 0; i < count; i++) { result.Add(Read(stream)); }
                }
            }
            catch
            {
                // Un comando truncado se ignora.
            }
            return result;
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int current = stream.Read(buffer, read, count - read);
                if (current <= 0) { throw new EndOfStreamException(); }
                read += current;
            }
            return buffer;
        }

        private static int ReadUInt16(Stream stream)
        {
            var bytes = ReadExactly(stream, 2);
            return (bytes[0] << 8) | bytes[1];
        }

        private static int ReadInt32(Stream stream)
        {
            var bytes = ReadExactly(stream, 4);
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        private static void WriteTwo(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteFour(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }

    /// <summary>Lee mensajes RTMP del stream, resolviendo formatos 0..3 de chunk.</summary>
    public sealed class RtmpChunkReader
    {
        private readonly Stream _stream;
        private readonly Dictionary<int, ChunkState> _states = new Dictionary<int, ChunkState>();

        public int IncomingChunkSize { get; set; } = 128;

        public RtmpChunkReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<RtmpMessage> ReadAsync(CancellationToken token)
        {
            while (true)
            {
                var first = new byte[1];
                await ReadExactlyAsync(_stream, first, 0, 1, token);
                int format = first[0] >> 6;
                int csid = first[0] & 0x3F;

                if (csid == 0)
                {
                    var extra = new byte[1];
                    await ReadExactlyAsync(_stream, extra, 0, 1, token);
                    csid = extra[0] + 64;
                }
                else if (csid == 1)
                {
                    var extra = new byte[2];
                    await ReadExactlyAsync(_stream, extra, 0, 2, token);
                    csid = (extra[0] | (extra[1] << 8)) + 64;
                }

                if (!_states.TryGetValue(csid, out var state))
                {
                    state = new ChunkState();
                    _states[csid] = state;
                }

                if (format == 0)
                {
                    var header = await ReadExactlyAsync(_stream, null, 0, 11, token);
                    state.Timestamp = ReadUInt24(header, 0);
                    if (state.Timestamp == 0xFFFFFF)
                    {
                        var ext = await ReadExactlyAsync(_stream, null, 0, 4, token);
                        state.Timestamp = ReadUInt32(ext, 0);
                    }
                    state.Length = (int)ReadUInt24(header, 3);
                    state.Type = header[6];
                    state.StreamId = ReadUInt32Le(header, 7);
                    StartMessage(state);
                }
                else if (format == 1)
                {
                    var header = await ReadExactlyAsync(_stream, null, 0, 7, token);
                    uint delta = ReadUInt24(header, 0);
                    if (delta == 0xFFFFFF)
                    {
                        var ext = await ReadExactlyAsync(_stream, null, 0, 4, token);
                        delta = ReadUInt32(ext, 0);
                    }
                    state.Timestamp += delta;
                    state.Length = (int)ReadUInt24(header, 3);
                    state.Type = header[6];
                    StartMessage(state);
                }
                else if (format == 2)
                {
                    var header = await ReadExactlyAsync(_stream, null, 0, 3, token);
                    uint delta = ReadUInt24(header, 0);
                    if (delta == 0xFFFFFF)
                    {
                        var ext = await ReadExactlyAsync(_stream, null, 0, 4, token);
                        delta = ReadUInt32(ext, 0);
                    }
                    state.Timestamp += delta;
                    StartMessage(state);
                }
                else if (state.Buffer == null || state.Remaining == 0)
                {
                    // Formato 3 con mensaje terminado: se reutiliza el cabecero anterior.
                    StartMessage(state);
                }

                int chunk = Math.Min(IncomingChunkSize, state.Remaining);
                if (chunk > 0)
                {
                    int written = state.Length - state.Remaining;
                    await ReadExactlyAsync(_stream, state.Buffer, written, chunk, token);
                    state.Remaining -= chunk;
                }

                if (state.Remaining > 0) { return null; }       // sigue el siguiente chunk

                var message = new RtmpMessage
                {
                    Type = state.Type,
                    Timestamp = state.Timestamp,
                    StreamId = state.StreamId,
                    Payload = state.Buffer
                };
                state.Buffer = null;
                return message;
            }
        }

        private static void StartMessage(ChunkState state)
        {
            state.Remaining = state.Length;
            state.Buffer = new byte[state.Length];
        }

        private sealed class ChunkState
        {
            public uint Timestamp;
            public int Length;
            public byte Type;
            public uint StreamId;
            public int Remaining;
            public byte[] Buffer;
        }

        private static uint ReadUInt24(byte[] data, int offset)
        {
            return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        private static uint ReadUInt32Le(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, byte[] reuse, int offset, int count,
            CancellationToken token)
        {
            var buffer = reuse ?? new byte[count];
            int read = 0;
            while (read < count)
            {
                int current = await stream.ReadAsync(buffer, offset + read, count - read, token);
                if (current <= 0) { throw new EndOfStreamException(); }
                read += current;
            }
            return buffer;
        }
    }

    /// <summary>Escribe mensajes RTMP usando chunks de tamaño configurable.</summary>
    public sealed class RtmpChunkWriter
    {
        private readonly Stream _stream;
        private readonly byte[] _header = new byte[18];

        public int OutChunkSize { get; set; } = 4096;

        public RtmpChunkWriter(Stream stream)
        {
            _stream = stream;
        }

        public async Task WriteAsync(int csid, uint timestamp, byte type, uint streamId, byte[] payload,
            CancellationToken token)
        {
            var data = payload ?? new byte[0];
            int offset = 0;
            bool first = true;

            while (offset < data.Length || first)
            {
                int count = Math.Min(OutChunkSize, data.Length - offset);
                int index = 0;

                if (csid < 64)
                {
                    _header[index++] = (byte)((first ? 0 : 3) << 6 | csid);
                }
                else if (csid < 320)
                {
                    _header[index++] = (byte)((first ? 0 : 3) << 6);
                    _header[index++] = (byte)(csid - 64);
                }

                if (first)
                {
                    uint stamp = Math.Min(timestamp, 0xFFFFFFu);
                    _header[index++] = (byte)(stamp >> 16);
                    _header[index++] = (byte)(stamp >> 8);
                    _header[index++] = (byte)stamp;
                    _header[index++] = (byte)(data.Length >> 16);
                    _header[index++] = (byte)(data.Length >> 8);
                    _header[index++] = (byte)data.Length;
                    _header[index++] = type;
                    _header[index++] = (byte)streamId;
                    _header[index++] = (byte)(streamId >> 8);
                    _header[index++] = (byte)(streamId >> 16);
                    _header[index++] = (byte)(streamId >> 24);
                }

                await _stream.WriteAsync(_header, 0, index, token);
                if (count > 0) { await _stream.WriteAsync(data, offset, count, token); }
                offset += count;
                first = false;
            }

            await _stream.FlushAsync();
        }

        public Task WriteProtocolAsync(byte type, byte[] payload, CancellationToken token)
        {
            return WriteAsync(2, 0, type, 0, payload, token);
        }
    }
}
