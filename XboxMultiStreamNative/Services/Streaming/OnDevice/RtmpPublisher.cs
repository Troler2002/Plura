using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>Publica mensajes FLV en un servidor RTMP/RTMPS sin ffmpeg ni PC.</summary>
    public sealed class RtmpPublisher : IDisposable
    {
        private const int HandshakeSize = 1536;

        private static readonly byte[] PlayerKeyOpen = Encoding.ASCII.GetBytes("Genuine Adobe Flash Player 001");
        private static readonly byte[] ServerKeyOpen = Encoding.ASCII.GetBytes("Genuine Adobe Flash Media Server 001");
        private static readonly byte[] KeyTail =
        {
            0xF0, 0xEE, 0xC2, 0x4A, 0x80, 0x68, 0xBE, 0xE8,
            0x2E, 0x00, 0xD0, 0xD1, 0x02, 0x9E, 0x7E, 0x57,
            0x6E, 0xEC, 0x5D, 0x2D, 0x29, 0x80, 0x6F, 0xAB,
            0x93, 0xB8, 0xE6, 0x36, 0xCF, 0xEB, 0x31, 0xAE
        };

        private static readonly byte[] PlayerKeyFull = Combine(PlayerKeyOpen, KeyTail);
        private static readonly byte[] ServerKeyFull = Combine(ServerKeyOpen, KeyTail);

        private readonly string _serverUrl;
        private readonly string _streamKey;
        private readonly string _title;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _pumpCts = new CancellationTokenSource();

        private StreamSocket _socket;
        private Stream _input;
        private Stream _output;
        private RtmpChunkReader _reader;
        private RtmpChunkWriter _writer;
        private Task _readPump;
        private uint _streamId;
        private string _fatalError = string.Empty;
        private bool _connected;
        private bool _disposed;

        public RtmpPublisher(string serverUrl, string streamKey, string title = "")
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                throw new OnDeviceRelayException("Falta la URL del servidor de ingesta.");
            }
            if (string.IsNullOrWhiteSpace(streamKey))
            {
                throw new OnDeviceRelayException("Falta la clave de la plataforma.");
            }

            _serverUrl = serverUrl.Trim();
            _streamKey = streamKey.Trim();
            _title = (title ?? string.Empty).Trim();
        }

        public bool IsConnected { get { return _connected && _fatalError.Length == 0; } }
        public string Error { get { return _fatalError; } }
        public long BytesSent { get; private set; }

        public async Task ConnectAsync(CancellationToken token)
        {
            var uri = ParseUri(_serverUrl);
            bool secure = string.Equals(uri.Scheme, "rtmps", StringComparison.OrdinalIgnoreCase);

            // Kick (Amazon IVS) rechaza la publicación por RTMP plano: el
            // handshake y el connect pasan, pero cierra la sesión al recibir
            // publish. Por eso se conserva RTMPS y se prueban dos pilas TLS
            // distintas dentro de la consola antes de dar el fallo por error.

            int port = uri.Port >= 0 ? uri.Port : (secure ? 443 : 1935);
            string app = GetApp(uri);
            string playpath = GetPlaypath(uri, _streamKey);
            string tcUrl = (secure ? "rtmps://" : "rtmp://") + uri.Host + ":" + port + "/" + app;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var ct = timeout.Token;

                App.Log("RTMP connecting to " + uri.Host + ":" + port +
                        (secure ? " (RTMPS)" : " (RTMP)"));
                await OpenTransportAsync(uri.Host, port, secure, ct);
                App.Log("RTMP transport connected");

                _reader = new RtmpChunkReader(_input);
                _writer = new RtmpChunkWriter(_output) { OutChunkSize = 4096 };

                await HandshakeAsync(ct);
                await WriteControlAsync(ct);
                App.Log("RTMP handshake and control OK");

                // Para publicar, RTMP no debe incluir fpad/capabilities/audioCodecs/
                // videoCodecs/videoFunction: esos campos son solo de reproducción y
                // Amazon IVS (Kick) cierra la conexión si los recibe.
                await SendCommandAsync(3, 0, ct, "connect", 1.0,
                    new AmfObject()
                        .Set("app", app)
                        .Set("type", "nonprivate")
                        .Set("flashVer", "FMLE/3.0 (compatible; FMSc/1.0)")
                        .Set("tcUrl", tcUrl));
                await WaitForResultAsync(1, "NetConnection.Connect.Success", ct);
                App.Log("RTMP application connected: " + app);

                await SendCommandAsync(3, 0, ct, "releaseStream", 2.0, null, playpath);
                await SendCommandAsync(3, 0, ct, "FCPublish", 3.0, null, playpath);
                await SendCommandAsync(3, 0, ct, "createStream", 4.0);
                var result = await WaitForResultAsync(4, null, ct);
                _streamId = ToUInt(result);

                // FFmpeg/OBS no envían Set Buffer Length antes de publish. Hacerlo
                // hace que algunos ingestas de Amazon IVS cierren la conexión sin
                // enviar onStatus, así que el sequence queda idéntico a OBS.
                App.Log("RTMP publish: playpath length=" + playpath.Length +
                        " key length=" + _streamKey.Length);
                await SendCommandAsync(3, _streamId, ct, "publish", 5.0, null, playpath, "live");
                await WaitForStatusAsync("NetStream.Publish.Start", ct);
                App.Log("RTMP publish accepted");

                var metadata = new AmfEcmaArray()
                    .Set("duration", 0.0)
                    .Set("width", 1280.0)
                    .Set("height", 720.0)
                    .Set("videocodecid", 7.0)
                    .Set("framerate", 30.0)
                    .Set("audiocodecid", 10.0)
                    .Set("audiosamplerate", 48000.0)
                    .Set("stereo", true);

                // El título de Twitch viaja también en los metadatos RTMP. Se
                // incluyen todos los nombres que usan reproductores y servicios
                // para que el destino pueda tomarlo sin sesión ni login.
                ApplyTitle(metadata, _title);

                await WriteAsync(8, 0, 0x12, _streamId,
                    Amf0.Encode("@setDataFrame", "onMetaData", metadata), ct);

                _connected = true;
                _readPump = Task.Run(() => ReadPumpAsync(_pumpCts.Token));
            }
        }

        /// <summary>
        /// Reenvía el título cuando cambia en Twitch. No necesita token, cookie
        /// ni sesión: es un bloque onMetaData más dentro del mismo stream RTMP.
        /// </summary>
        public async Task UpdateMetadataTitleAsync(string title, CancellationToken token)
        {
            var value = (title ?? string.Empty).Trim();
            if (value.Length == 0 || !IsConnected) { return; }

            var metadata = new AmfEcmaArray();
            ApplyTitle(metadata, value);
            await WriteAsync(8, 0, 0x12, _streamId,
                Amf0.Encode("@setDataFrame", "onMetaData", metadata), token);
            App.Log("RTMP metadata title reenviado: " + Short(value));
        }

        private static void ApplyTitle(AmfEcmaArray metadata, string title)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(title)) { return; }
            metadata.Set("title", title);
            metadata.Set("stream_title", title);
            metadata.Set("streamTitle", title);
            metadata.Set("description", title);
            metadata.Set("name", title);
            metadata.Set("display_title", title);
        }

        private static string Short(string value)
        {
            var text = value ?? string.Empty;
            return text.Length > 120 ? text.Substring(0, 120) : text;
        }

        private async Task OpenTransportAsync(
            string host, int port, bool secure, CancellationToken token)
        {
            if (!secure)
            {
                _socket = await ConnectSocketAsync(host, port, false, token);
                _input = _socket.InputStream.AsStreamForRead();
                _output = _socket.OutputStream.AsStreamForWrite();
                return;
            }

            // StreamSocket con Tls12 conecta en Xbox pero se queda sin leer datos
            // de RTMP (y en otros ingestas falla con 0x80090330). SslStream sobre
            // un socket plano usa otra pila dentro de .NET Native y completa el
            // handshake TLS 1.2 que Amazon IVS/Facebook exigen.
            _socket = await ConnectSocketAsync(host, port, false, token);
            var duplex = new SocketDuplexStream(
                _socket.InputStream.AsStreamForRead(),
                _socket.OutputStream.AsStreamForWrite());
            var ssl = new SslStream(duplex, false);
            App.Log("RTMPS SslStream authenticating: " + host + ":" + port);
            await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, false);
            App.Log("RTMPS SslStream OK: " + ssl.SslProtocol + " " + ssl.CipherAlgorithm);
            _input = ssl;
            _output = ssl;
        }

        private sealed class SocketDuplexStream : Stream
        {
            private readonly Stream _read;
            private readonly Stream _write;

            public SocketDuplexStream(Stream read, Stream write)
            {
                _read = read;
                _write = write;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken token)
            {
                return await _read.ReadAsync(buffer, offset, count, token);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _read.Read(buffer, offset, count);
            }

            public override async Task WriteAsync(
                byte[] buffer, int offset, int count, CancellationToken token)
            {
                await _write.WriteAsync(buffer, offset, count, token);
                await _write.FlushAsync();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _write.Write(buffer, offset, count);
                _write.Flush();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        private static async Task<StreamSocket> ConnectSocketAsync(
            string host, int port, bool secure, CancellationToken token)
        {
            if (!secure)
            {
                var plain = new StreamSocket();
                plain.Control.NoDelay = true;
                await plain.ConnectAsync(new HostName(host), port.ToString(),
                    SocketProtectionLevel.PlainSocket);
                return plain;
            }

            // En Xbox, Tls12 puede negociar TLS 1.3 y Schannel devuelve SEC_E_DECRYPT_FAILURE
            // (0x80090330). Algunos ingestas solo aceptan TLS 1.2, así que se fuerza ese
            // nivel primero y se prueban alternativas antes de rendirse.
            var levels = new[]
            {
                (SocketProtectionLevel)0x00000008, // Tls12 (valor explícito; en Xbox evita la ruta Tls13)
                SocketProtectionLevel.Tls12,
                SocketProtectionLevel.Tls10
            };

            Exception last = null;
            foreach (var level in levels)
            {
                var socket = new StreamSocket();
                try
                {
                    socket.Control.NoDelay = true;
                    await socket.ConnectAsync(new HostName(host), port.ToString(), level);
                    App.Log("RTMP TLS connected: " + level);
                    return socket;
                }
                catch (Exception ex)
                {
                    last = ex;
                    App.Log("RTMP TLS attempt " + level + " failed: " + Describe(ex));
                    try { socket.Dispose(); } catch { }
                }
            }

            throw new OnDeviceRelayException("No se pudo establecer RTMPS: " +
                (last == null ? "sin detalle" : Describe(last)));
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) { return string.Empty; }
            return ex.GetType().Name + ": " + ex.Message + " (HRESULT=0x" +
                ex.HResult.ToString("X8") + ")";
        }

        public async Task SendMediaAsync(FlvMessage message, CancellationToken token)
        {
            if (message == null || message.Payload == null || message.Payload.Length == 0) { return; }
            if (!IsConnected)
            {
                throw new OnDeviceRelayException(_fatalError.Length > 0
                    ? _fatalError
                    : "El publicador RTMP no está conectado.");
            }

            await WriteAsync(message.MessageType == 8 ? 6 : 7,
                message.Timestamp, message.MessageType, _streamId, message.Payload, token);
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _connected = false;

            try { _pumpCts.Cancel(); } catch { }
            try { _socket?.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
            try { _pumpCts.Dispose(); } catch { }
        }

        // ------------------------------------------------------------- handshake

        private async Task HandshakeAsync(CancellationToken token)
        {
            var c1 = new byte[HandshakeSize];
            c1[4] = 9;
            c1[5] = 0;
            c1[6] = 124;
            c1[7] = 2;
            Array.Copy(RandomBytes(HandshakeSize - 8), 0, c1, 8, HandshakeSize - 8);

            int clientPos = DigestPos(c1, 8, 728, 12);
            Array.Copy(Hmac(PlayerKeyOpen, c1, clientPos), 0, c1, clientPos, 32);

            var c0c1 = new byte[HandshakeSize + 1];
            c0c1[0] = 3;
            Array.Copy(c1, 0, c0c1, 1, HandshakeSize);
            await WriteRawAsync(c0c1, token);

            var s0 = await ReadExactlyAsync(1, token);
            if (s0[0] != 3 && s0[0] != 6)
            {
                throw new OnDeviceRelayException("Versión RTMP no soportada por el servidor: " + s0[0] + ".");
            }

            var s1 = await ReadExactlyAsync(HandshakeSize, token);
            var s2 = await ReadExactlyAsync(HandshakeSize, token);
            App.Log("RTMP S0S1S2 received: version=" + s1[5] + "." + s1[6] + "." +
                    s1[7] + "." + s1[8]);

            int serverPos = FindServerDigest(s1, 772);
            if (serverPos == 0) { serverPos = FindServerDigest(s1, 8); }
            App.Log("RTMP server digest position: " + serverPos);

            bool useComplex = serverPos > 0 && s1[5] >= 3;
            if (useComplex)
            {
                // Según RTMP spec / FFmpeg, la firma de S2 se deriva del digest
                // del CLIENTE (C1), no del digest del servidor (S1).
                byte[] digest = Hmac(ServerKeyFull, c1, clientPos);
                byte[] expected = Hmac(digest, s2, HandshakeSize - 32);
                useComplex = Same(expected, Segment(s2, HandshakeSize - 32, 32));
            }

            byte[] c2;
            if (useComplex)
            {
                c2 = RandomBytes(HandshakeSize);
                byte[] digest = Hmac(PlayerKeyFull, s1, serverPos);
                byte[] signature = Hmac(digest, c2, HandshakeSize - 32);
                Array.Copy(signature, 0, c2, HandshakeSize - 32, 32);
            }
            else
            {
                // Handshake simple: el servidor espera que se le devuelva S1.
                c2 = s1;
            }

            await WriteRawAsync(c2, token);
            App.Log("RTMP C2 sent (" + (useComplex ? "complex" : "simple") + ")");
        }

        private static int DigestPos(byte[] buffer, int offset, int mod, int add)
        {
            int sum = 0;
            for (int i = 0; i < 4; i++) { sum += buffer[offset + i]; }
            return sum % mod + add;
        }

        private static int FindServerDigest(byte[] s1, int offset)
        {
            int pos = DigestPos(s1, offset, 728, offset + 4);
            if (pos + 32 > s1.Length) { return 0; }
            byte[] expected = Hmac(ServerKeyOpen, s1, pos);
            return Same(expected, Segment(s1, pos, 32)) ? pos : 0;
        }

        private static byte[] Hmac(byte[] key, byte[] data, int gap)
        {
            byte[] payload;
            if (gap > 0)
            {
                payload = new byte[data.Length - 32];
                Array.Copy(data, 0, payload, 0, gap);
                Array.Copy(data, gap + 32, payload, gap, data.Length - gap - 32);
            }
            else
            {
                payload = data;
            }

            using (var hmac = new System.Security.Cryptography.HMACSHA256(key))
            {
                return hmac.ComputeHash(payload);
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) { return false; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
            return diff == 0;
        }

        private static byte[] Segment(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static byte[] Combine(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static byte[] RandomBytes(int count)
        {
            var buffer = CryptographicBuffer.GenerateRandom((uint)count);
            byte[] result;
            CryptographicBuffer.CopyToByteArray(buffer, out result);
            return result;
        }

        // ----------------------------------------------------------- RTMP control

        private async Task WriteControlAsync(CancellationToken token)
        {
            // Solo Set Chunk Size. Enviar Window Acknowledgement Size o Set Peer
            // Bandwidth antes del connect hace que Amazon IVS/Kick cierre la sesión.
            await WriteProtocolAsync(1, BigEndian(4096), token);
        }

        private async Task SendCommandAsync(int csid, uint streamId, CancellationToken token,
            string name, double transactionId, params object[] arguments)
        {
            var values = new List<object> { name, transactionId };
            if (arguments != null) { values.AddRange(arguments); }
            await WriteAsync(csid, 0, 20, streamId, Amf0.Encode(values.ToArray()), token);
        }

        private async Task<object> WaitForResultAsync(double transactionId, string expectedCode,
            CancellationToken token)
        {
            while (true)
            {
                var message = await ReadMessageAsync(token);
                HandleMessage(message);

                if (message.Type != 20) { continue; }
                var values = Amf0.ReadValues(message.Payload, 4);
                if (values.Count < 2) { continue; }
                if (!string.Equals(values[0] as string, "_result", StringComparison.Ordinal)) { continue; }
                if (!SameNumber(values[1], transactionId)) { continue; }

                if (expectedCode != null)
                {
                    var info = values.Count > 3 ? values[3] as AmfObject : null;
                    var code = GetString(info, "code");
                    if (!string.Equals(code, expectedCode, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new OnDeviceRelayException("RTMP: " +
                            (code.Length > 0 ? code : "el servidor rechazó la conexión."));
                    }
                }

                return values.Count > 3 ? values[3] : null;
            }
        }

        private async Task WaitForStatusAsync(string expectedCode, CancellationToken token)
        {
            var lastCode = string.Empty;
            var lastDescription = string.Empty;

            while (true)
            {
                RtmpMessage message;
                try
                {
                    message = await ReadMessageAsync(token);
                }
                catch (EndOfStreamException)
                {
                    throw new OnDeviceRelayException("RTMP: el servidor cerró la conexión" +
                        (lastCode.Length > 0
                            ? " tras " + lastCode +
                              (lastDescription.Length > 0 ? " (" + lastDescription + ")" : string.Empty)
                            : " al publicar (posible clave de la plataforma inválida o caducada)."));
                }

                HandleMessage(message);

                if (message.Type != 20) { continue; }
                var values = Amf0.ReadValues(message.Payload, 4);
                var name = values.Count > 0 ? values[0] as string : string.Empty;
                if (name != "_result" && name != "onStatus") { continue; }

                var info = values.Count > 3 ? values[3] as AmfObject : null;
                var code = GetString(info, "code");
                var description = GetString(info, "description");
                lastCode = code;
                lastDescription = description;
                App.Log("RTMP status: " + name + " " + code +
                        (description.Length > 0 ? " · " + description : string.Empty));

                if (string.Equals(code, expectedCode, StringComparison.OrdinalIgnoreCase)) { return; }

                if (string.Equals(name, "_error", StringComparison.OrdinalIgnoreCase) ||
                    code.StartsWith("NetStream.Publish.", StringComparison.OrdinalIgnoreCase) ||
                    code.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("Rejected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("Denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("BadName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new OnDeviceRelayException("RTMP: " +
                        (code.Length > 0 ? code : name) +
                        (description.Length > 0 ? " · " + description : string.Empty));
                }
            }
        }

        private async Task ReadPumpAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(token);
                    HandleMessage(message);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _fatalError = ex.Message;
                _connected = false;
                try { _socket?.Dispose(); } catch { }
            }
        }

        private async Task<RtmpMessage> ReadMessageAsync(CancellationToken token)
        {
            while (true)
            {
                var message = await _reader.ReadAsync(token);
                if (message != null) { return message; }
            }
        }

        private void HandleMessage(RtmpMessage message)
        {
            if (message == null) { return; }

            switch (message.Type)
            {
                case 1:                                        // Set Chunk Size
                    if (message.Payload.Length >= 4)
                    {
                        int size = (int)(BigValue(message.Payload, 0) & 0x7FFFFFFFu);
                        if (size >= 128) { _reader.IncomingChunkSize = size; }
                    }
                    break;
                case 4:                                        // User Control
                    if (message.Payload.Length >= 6 && message.Payload[0] == 0 &&
                        (message.Payload[1] == 6 || message.Payload[1] == 7))
                    {
                        // PingRequest → PingResponse con el mismo valor.
                        var pong = new byte[] { 0, 7, message.Payload[2], message.Payload[3],
                                                message.Payload[4], message.Payload[5] };
                        var ignored = WriteAsync(2, 0, 4, 0, pong, CancellationToken.None);
                    }
                    break;
                case 20:                                       // AMF0 command
                {
                    var values = Amf0.ReadValues(message.Payload, 4);
                    var name = values.Count > 0 ? values[0] as string : string.Empty;
                    var info = values.Count > 3 ? values[3] as AmfObject : null;
                    var code = GetString(info, "code");
                    if (string.Equals(name, "onStatus", StringComparison.Ordinal) &&
                        (code.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         code.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _fatalError = code + ": " + GetString(info, "description");
                        _connected = false;
                    }
                    break;
                }
            }
        }

        private async Task WriteAsync(int csid, uint timestamp, byte type, uint streamId, byte[] payload,
            CancellationToken token)
        {
            await _writeLock.WaitAsync(token);
            try
            {
                await _writer.WriteAsync(csid, timestamp, type, streamId, payload, token);
                BytesSent += payload.Length;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private Task WriteProtocolAsync(byte type, byte[] payload, CancellationToken token)
        {
            return WriteAsync(2, 0, type, 0, payload, token);
        }

        private async Task WriteRawAsync(byte[] data, CancellationToken token)
        {
            await _output.WriteAsync(data, 0, data.Length, token);
            await _output.FlushAsync();
            BytesSent += data.Length;
        }

        private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken token)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int current = await _input.ReadAsync(buffer, read, count - read, token);
                if (current <= 0) { throw new OnDeviceRelayException("El servidor RTMP cerró la conexión."); }
                read += current;
            }
            return buffer;
        }

        // --------------------------------------------------------------- helpers

        private static Uri ParseUri(string value)
        {
            var text = value.Trim();
            if (text.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(text);
            }
            throw new OnDeviceRelayException("La URL de ingesta debe empezar por rtmp:// o rtmps://.");
        }

        private static string GetApp(Uri uri)
        {
            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? Uri.UnescapeDataString(segments[0]) : "live";
        }

        private static string GetPlaypath(Uri uri, string key)
        {
            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var path = segments.Length > 1
                ? string.Join("/", segments.Skip(1).Select(Uri.UnescapeDataString))
                : string.Empty;
            return path.Length > 0 ? path + "/" + key : key;
        }

        private static byte[] BigEndian(int value)
        {
            return new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static uint BigValue(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        private static bool SameNumber(object value, double expected)
        {
            if (value is double number) { return Math.Abs(number - expected) < 0.5; }
            if (value is int integer) { return Math.Abs(integer - expected) < 0.5; }
            return false;
        }

        private static uint ToUInt(object value)
        {
            if (value is double number) { return (uint)Math.Max(0, number); }
            if (value is int integer) { return (uint)Math.Max(0, integer); }
            return 1;
        }

        private static string GetString(AmfObject obj, string key)
        {
            if (obj == null) { return string.Empty; }
            foreach (var property in obj.Properties)
            {
                if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value == null ? string.Empty : property.Value.ToString();
                }
            }
            return string.Empty;
        }
    }
}
