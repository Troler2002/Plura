using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Core;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Relay que corre dentro de la propia app: Twitch (HLS) → MPEG-TS → FLV → RTMP.
    /// No usa backend, ffmpeg ni PC. Expone la misma interfaz que el antiguo cliente
    /// HTTP para que la UI no tenga que conocer el protocolo.
    /// </summary>
    public sealed class OnDeviceRelayClient
    {
        private static readonly OnDeviceRelayClient Instance = new OnDeviceRelayClient();

        private readonly TwitchService _twitch = new TwitchService();
        private readonly object _gate = new object();
        private readonly Dictionary<string, OutputWorker> _workers =
            new Dictionary<string, OutputWorker>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _runCts;
        private Timer _healthTimer;
        private IDisposable _executionSession;
        private string _channel = string.Empty;
        private string _displayName = string.Empty;
        private string _title = string.Empty;
        private string _globalError = string.Empty;
        private DateTime _startedAt;
        private int _generation;
        private bool _running;
        private bool _basicRequesting;
        private bool _executionDenied;

        public static OnDeviceRelayClient Current { get { return Instance; } }

        public Task<RelayStatus> GetStatusAsync(CancellationToken token)
        {
            return Task.FromResult(Snapshot());
        }

        public async Task<RelayStatus> StartAsync(
            string channel, IList<RelayTarget> targets, CancellationToken token)
        {
            var login = TwitchService.NormalizeLogin(channel);
            if (!TwitchService.IsValidLogin(login))
            {
                throw new OnDeviceRelayException("Escribe un canal de Twitch válido.");
            }

            if (targets == null || targets.Count == 0)
            {
                throw new OnDeviceRelayException("Activa al menos una plataforma destino.");
            }

            var info = await _twitch.GetChannelAsync(login, token);
            if (!info.Live)
            {
                throw new OnDeviceRelayException(
                    "El canal " + login + " no está en vivo. Inicia antes la emisión desde la Xbox.");
            }

            await StopAsync(token);

            var deduped = new List<RelayTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                if (target == null) { continue; }
                var key = string.IsNullOrWhiteSpace(target.Platform) ? "destino" : target.Platform;
                if (seen.Add(key)) { deduped.Add(target); }
            }

            lock (_gate)
            {
                _generation++;
                _channel = login;
                _displayName = info.DisplayName;
                _title = info.Title ?? string.Empty;
                _globalError = string.Empty;
                _startedAt = DateTime.UtcNow;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _running = true;
                _executionDenied = false;

                foreach (var target in deduped)
                {
                    var worker = new OutputWorker(target);
                    worker.Status.Platform = target.Platform;
                    worker.Status.State = "connecting";
                    worker.Cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
                    _workers[worker.Key] = worker;
                    worker.Task = Task.Run(() => RunOutputAsync(worker, _generation, worker.Cts.Token));
                }
            }

            var titleToken = _runCts.Token;
            var ignoredTitleSync = Task.Run(() => TitleSyncLoopAsync(titleToken));
            var ignored = RequestExtendedExecutionAsync();
            BackgroundKeepAlive.Start();
            StartHealthTimer(_generation);
            App.Log("On-device relay started for " + login + " with " + deduped.Count + " outputs" +
                    (_title.Length > 0 ? " · title=\"" + _title + "\"" : string.Empty));
            return Snapshot();
        }

        public async Task<RelayStatus> StopAsync(CancellationToken token)
        {
            StopHealthTimer();
            BackgroundKeepAlive.Stop();
            OutputWorker[] workers;
            CancellationTokenSource runCts;
            IDisposable session;

            lock (_gate)
            {
                if (_running) { App.Log("Relay stop requested"); }
                _generation++;
                _running = false;
                _title = string.Empty;
                runCts = _runCts;
                _runCts = null;
                workers = _workers.Values.ToArray();
                _workers.Clear();
                session = _executionSession;
                _executionSession = null;
            }

            if (runCts != null)
            {
                try { runCts.Cancel(); } catch { }
            }
            foreach (var worker in workers)
            {
                try { worker.Cts?.Cancel(); } catch { }
            }

            if (workers.Length > 0)
            {
                try { await Task.WhenAll(workers.Select(w => w.Task).Where(t => t != null)); }
                catch (Exception ex) { App.Log("Relay stop: " + ex.Message); }
            }

            foreach (var worker in workers)
            {
                worker.Cts?.Dispose();
            }
            runCts?.Dispose();
            ReleaseExtendedExecution(session);

            return Snapshot();
        }

        public async Task<RelayChannelInfo> CheckChannelAsync(string login, CancellationToken token)
        {
            var info = await _twitch.GetChannelAsync(login, token);
            return new RelayChannelInfo
            {
                Login = info.Login,
                DisplayName = info.DisplayName,
                AvatarUrl = info.AvatarUrl,
                AvatarBase64 = info.AvatarBase64,
                AvatarContentType = "image/png",
                Title = info.Title,
                Live = info.Live,
                Viewers = info.Viewers
            };
        }

        // ------------------------------------------------------------ workers

        private async Task RunOutputAsync(OutputWorker worker, int generation, CancellationToken token)
        {
            int restarts = 0;
            DateTime? streamStarted = null;

            while (!token.IsCancellationRequested && generation == _generation)
            {
                worker.Status.LastMediaUtc = DateTime.MinValue;
                worker.VideoFrames = 0;

                var publisher = new RtmpPublisher(
                    worker.Target.ServerUrl, worker.Target.StreamKey, GetTitle());
                worker.Publisher = publisher;

                try
                {
                    worker.Status.State = "connecting";
                    worker.Status.Error = string.Empty;
                    await publisher.ConnectAsync(token);

                    worker.Status.State = "running";
                    worker.Status.Restarts = 0;
                    worker.Status.Error = string.Empty;
                    restarts = 0;
                    streamStarted = DateTime.UtcNow;
                    lock (_gate)
                    {
                        if (!string.IsNullOrWhiteSpace(_globalError)) { _globalError = string.Empty; }
                    }

                    await PushStreamAsync(publisher, worker, token, streamStarted.Value);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    worker.Status.Error = MaskSecret(ex.Message, worker.Target.StreamKey);
                    App.Log("Relay output " + worker.Key + ": " + worker.Status.Error);
                    App.Log("Relay output " + worker.Key + " stack: " +
                            MaskSecret(ex.ToString(), worker.Target.StreamKey));

                    restarts++;
                    worker.Status.Restarts = restarts;
                    if (restarts > 20)
                    {
                        worker.Status.State = "error";
                        _globalError = worker.Key + ": " + worker.Status.Error;
                        break;
                    }

                    worker.Status.State = "restarting";
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(4 * restarts, 20)), token);
                    }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    publisher.Dispose();
                    worker.Publisher = null;
                    // Las métricas de la conexión anterior no deben hacer creer
                    // al supervisor que este destino sigue emitiendo.
                    worker.Status.BitrateKbps = 0;
                    worker.Status.Fps = 0;
                    worker.Status.DurationSeconds = 0;
                    worker.Status.LastMediaUtc = DateTime.MinValue;
                    worker.VideoFrames = 0;
                    if (worker.Status.State != "error") { worker.Status.State = "restarting"; }
                }
            }

            if (worker.Status.State != "error")
            {
                worker.Status.State = "idle";
                worker.Status.BitrateKbps = 0;
                worker.Status.Fps = 0;
                worker.Status.DurationSeconds = 0;
                worker.Status.LastMediaUtc = DateTime.MinValue;
            }
        }

        private async Task PushStreamAsync(
            RtmpPublisher publisher, OutputWorker worker, CancellationToken token, DateTime streamStarted)
        {
            var demuxer = new UnifiedDemuxer();
            string masterUrl = await _twitch.GetMasterPlaylistUrlAsync(_channel, token);
            string mediaUrl = await _twitch.GetMediaPlaylistUrlAsync(masterUrl, token);
            DateTime lastRefresh = DateTime.UtcNow;

            Uri lastSegment = null;
            Uri lastInit = null;
            bool firstPass = true;
            bool modeLogged = false;
            long lastBytes = 0;
            long lastFrames = 0;
            int segmentLogs = 0;
            int segmentFailures = 0;
            int noSegmentPolls = 0;
            DateTime lastStats = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                if ((DateTime.UtcNow - lastRefresh).TotalMinutes > 9)
                {
                    var refreshed = await TryRefreshPlaylistAsync(worker, token, "programada");
                    if (refreshed != null)
                    {
                        masterUrl = refreshed.Master;
                        mediaUrl = refreshed.Media;
                        lastRefresh = DateTime.UtcNow;
                    }
                }

                HlsPlaylist playlist;
                try
                {
                    playlist = await _twitch.GetPlaylistAsync(mediaUrl, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    App.Log("HLS playlist " + worker.Key + ": " + ex.Message);
                    var refreshed = await TryRefreshPlaylistAsync(worker, token, "reintento");
                    if (refreshed != null)
                    {
                        masterUrl = refreshed.Master;
                        mediaUrl = refreshed.Media;
                        lastRefresh = DateTime.UtcNow;
                    }
                    else
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                        catch (OperationCanceledException) { throw; }
                    }
                    continue;
                }

                if (playlist.Init != null &&
                    (lastInit == null || !SameSegment(playlist.Init, lastInit)))
                {
                    var init = await _twitch.GetSegmentAsync(playlist.Init, token);
                    demuxer.SetInit(init);
                    lastInit = playlist.Init;
                    App.Log("HLS init: " + init.Length + " bytes (" + worker.Key + ")");
                }

                int count = playlist.Segments.Count;
                int first;
                if (firstPass || lastSegment == null)
                {
                    // Arrancamos por el final para no inyectar de golpe toda la
                    // ventana HLS y no retrasar la emisión en directo.
                    first = Math.Max(0, count - 2);
                    firstPass = false;
                }
                else
                {
                    int index = playlist.Segments.FindIndex(s => SameSegment(s, lastSegment));
                    if (index >= 0)
                    {
                        first = index + 1;
                    }
                    else
                    {
                        // La lista giró por completo o Twitch reinició el directo.
                        demuxer.Reset(true);
                        first = Math.Max(0, count - 2);
                    }
                }

                bool sentAny = false;
                for (int i = first; i < count && !token.IsCancellationRequested; i++)
                {
                    byte[] segment;
                    try
                    {
                        segment = await _twitch.GetSegmentAsync(playlist.Segments[i], token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        segmentFailures++;
                        if (segmentFailures <= 3)
                        {
                            App.Log("HLS segment " + worker.Key + " failed: " + ex.Message);
                        }
                        continue;
                    }
                    segmentFailures = 0;
                    sentAny = true;

                    var batch = new List<FlvMessage>();
                    demuxer.Feed(segment, batch.Add);
                    if (segmentLogs < 8)
                    {
                        segmentLogs++;
                        App.Log("HLS segment " + worker.Key + ": bytes=" + segment.Length +
                                " flvMessages=" + batch.Count +
                                " pmt=" + demuxer.TsPmtPid +
                                " video=" + demuxer.TsVideoPid +
                                " audio=" + demuxer.TsAudioPid);
                    }
                    if (!modeLogged)
                    {
                        App.Log("HLS container: " + (demuxer.IsFmp4 ? "fMP4/CMAF" : "MPEG-TS") +
                                " (" + worker.Key + ")");
                        modeLogged = true;
                    }

                    bool hasMedia = false;
                    foreach (var message in batch)
                    {
                        await publisher.SendMediaAsync(message, token);
                        if (message.MessageType == 9) { worker.VideoFrames++; }
                        if (message.MessageType == 8 || message.MessageType == 9) { hasMedia = true; }
                    }
                    if (hasMedia) { worker.Status.LastMediaUtc = DateTime.UtcNow; }

                    lastSegment = playlist.Segments[i];
                }

                if (sentAny)
                {
                    noSegmentPolls = 0;
                }
                else
                {
                    noSegmentPolls++;
                    if (noSegmentPolls >= 15)
                    {
                        throw new OnDeviceRelayException(
                            "HLS: 15 sondeos consecutivos sin segmentos útiles.");
                    }
                }

                var now = DateTime.UtcNow;
                double seconds = (now - lastStats).TotalSeconds;
                if (seconds >= 2)
                {
                    long bytes = publisher.BytesSent;
                    long frames = worker.VideoFrames;

                    worker.Status.BitrateKbps = (bytes - lastBytes) * 8.0 / 1000.0 / seconds;
                    worker.Status.Fps = (frames - lastFrames) / seconds;
                    worker.Status.DurationSeconds = (int)(now - streamStarted).TotalSeconds;

                    App.Log("Relay stats " + worker.Key + ": bytes=" + bytes +
                            " frames=" + frames + " bitrateKbps=" +
                            worker.Status.BitrateKbps.ToString("F0") +
                            " state=" + worker.Status.State);
                    lastBytes = bytes;
                    lastFrames = frames;
                    lastStats = now;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Renueva master/media del HLS con reintentos acotados. Un fallo aquí no
        /// debe tumbar el worker: Twitch puede fallar puntualmente al refrescar
        /// el token y el siguiente sondeo debe poder seguir.
        /// </summary>
        private async Task<PlaylistUrls> TryRefreshPlaylistAsync(
            OutputWorker worker, CancellationToken token, string reason)
        {
            for (int attempt = 1; attempt <= 3 && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    var master = await _twitch.GetMasterPlaylistUrlAsync(_channel, token);
                    var media = await _twitch.GetMediaPlaylistUrlAsync(master, token);
                    return new PlaylistUrls { Master = master, Media = media };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    App.Log("HLS refresh (" + reason + ", " + worker.Key + ") intent " +
                            attempt + ": " + ex.Message);
                    if (attempt < 3)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(attempt), token); }
                        catch (OperationCanceledException) { throw; }
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------- título

        private string GetTitle()
        {
            lock (_gate) { return _title; }
        }

        /// <summary>
        /// Lee el título real de Twitch cada ~20 s, lo refleja en el estado y lo
        /// reenvía por metadatos RTMP cuando cambia. No usa tokens ni APIs de
        /// destino: el título viaja dentro de la propia emisión.
        /// </summary>
        private async Task TitleSyncLoopAsync(CancellationToken token)
        {
            var lastTitle = string.Empty;
            var lastAttempt = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (lastAttempt == DateTime.MinValue || (now - lastAttempt).TotalSeconds >= 20)
                    {
                        lastAttempt = now;
                        var info = await _twitch.GetChannelAsync(_channel, token, false);
                        if (!info.Live) { continue; }

                        var title = (info.Title ?? string.Empty).Trim();
                        if (title.Length == 0) { continue; }

                        var titleChanged = !string.Equals(title, lastTitle, StringComparison.Ordinal);
                        if (titleChanged)
                        {
                            lastTitle = title;
                            lock (_gate) { _title = title; }
                            App.Log("Twitch title changed: " + title);

                            // Kick/IVS no garantiza que lea el título de los metadatos,
                            // pero lo reenviamos en cuanto cambia por si el destino
                            // sí lo usa. No depende de ninguna sesión ni login.
                            OutputWorker[] metadataWorkers;
                            lock (_gate) { metadataWorkers = _workers.Values.ToArray(); }
                            foreach (var metadataWorker in metadataWorkers)
                            {
                                var publisher = metadataWorker.Publisher;
                                if (publisher == null || !publisher.IsConnected) { continue; }
                                try
                                {
                                    await publisher.UpdateMetadataTitleAsync(title, token);
                                }
                                catch (Exception ex)
                                {
                                    App.Log("RTMP metadata title " + metadataWorker.Key +
                                            ": " + ex.Message);
                                }
                            }
                        }
                        else
                        {
                            lock (_gate)
                            {
                                if (string.IsNullOrWhiteSpace(_title)) { _title = title; }
                            }
                        }

                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    App.Log("Title sync check: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        // ------------------------------------------------------------- estado

        private RelayStatus Snapshot()
        {
            var status = new RelayStatus();
            OutputWorker[] workers;

            lock (_gate)
            {
                status.Running = _running;
                status.Channel = _channel;
                status.DisplayName = _displayName;
                status.Title = _title;
                status.Error = _globalError;
                status.UptimeSeconds = _running
                    ? (int)(DateTime.UtcNow - _startedAt).TotalSeconds
                    : 0;
                workers = _workers.Values.ToArray();
            }

            foreach (var worker in workers)
            {
                var output = new RelayOutputStatus
                {
                    Platform = worker.Status.Platform,
                    State = worker.Status.State,
                    Error = worker.Status.Error,
                    Restarts = worker.Status.Restarts,
                    BitrateKbps = worker.Status.BitrateKbps,
                    Fps = worker.Status.Fps,
                    DurationSeconds = worker.Status.DurationSeconds,
                    LastMediaUtc = worker.Status.LastMediaUtc
                };
                status.Outputs.Add(output);
            }

            status.Healthy = status.Running &&
                             string.IsNullOrWhiteSpace(status.Error) &&
                             status.Outputs.Count > 0 &&
                             status.Outputs.All(o =>
                                 string.IsNullOrWhiteSpace(o.Error) &&
                                 !string.Equals(o.State, "error", StringComparison.OrdinalIgnoreCase));

            return status;
        }

        /// <summary>
        /// Twitch cambia los parámetros del token al refrescar el playlist, pero el
        /// nombre del segmento se mantiene. Comparar solo la ruta evita reiniciar
        /// el demuxificador cuando cambia la firma de la URL.
        /// </summary>
        private static bool SameSegment(Uri candidate, Uri last)
        {
            if (candidate == null || last == null) { return false; }
            return string.Equals(candidate.AbsolutePath, last.AbsolutePath,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string MaskSecret(string message, string secret)
        {
            var text = message ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
            {
                text = text.Replace(secret, "***");
            }
            return text;
        }

        // ------------------------------------------------ watchdog interno

        private void StartHealthTimer(int generation)
        {
            StopHealthTimer();
            var timer = new Timer(
                state => { var ignored = SelfHealAsync(generation); },
                null, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12));
            lock (_gate) { _healthTimer = timer; }
        }

        private void StopHealthTimer()
        {
            Timer timer;
            lock (_gate)
            {
                timer = _healthTimer;
                _healthTimer = null;
            }
            if (timer != null) { try { timer.Dispose(); } catch { } }
        }

        /// <summary>
        /// Watchdog independiente del DispatcherTimer de la UI. Sustituye los
        /// destinos muertos (o colgados sin media) aunque Xbox estrangule el
        /// hilo de la interfaz mientras la app está en segundo plano.
        /// </summary>
        private async Task SelfHealAsync(int generation)
        {
            var stale = new List<OutputWorker>();
            var replacements = new List<OutputWorker>();

            try
            {
                lock (_gate)
                {
                    if (!_running || generation != _generation || _runCts == null) { return; }
                    var token = _runCts.Token;

                    // El watchdog del relay sigue corriendo aunque el hilo de la
                    // interfaz esté estrangulado: es el sitio más seguro para
                    // volver a poner el audio silencioso que mantiene viva la app.
                    BackgroundKeepAlive.EnsureRunning();

                    stale.AddRange(_workers.Values.Where(IsWorkerUnhealthy));

                    foreach (var worker in stale)
                    {
                        if ((DateTime.UtcNow - worker.LastSelfHealUtc).TotalSeconds < 20) { continue; }

                        var replacement = new OutputWorker(worker.Target);
                        replacement.Status.Platform = worker.Status.Platform;
                        replacement.Status.State = "connecting";
                        replacement.Cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        replacement.LastSelfHealUtc = DateTime.UtcNow;
                        _workers[worker.Key] = replacement;
                        replacement.Task = Task.Run(
                            () => RunOutputAsync(replacement, _generation, replacement.Cts.Token));
                        replacements.Add(replacement);
                    }

                    if (replacements.Count == 0) { return; }
                    _globalError = string.Empty;
                }

                foreach (var worker in stale)
                {
                    try { worker.Cts?.Cancel(); } catch { }
                }
                App.Log("Relay self-heal: " +
                        string.Join(", ", replacements.Select(r => r.Key)));
            }
            catch (Exception ex)
            {
                App.Log("Relay self-heal failed: " + ex.Message);
            }
            await Task.FromResult(0);
        }

        private static bool IsWorkerUnhealthy(OutputWorker worker)
        {
            if (worker == null) { return false; }
            var state = worker.Status.State ?? string.Empty;
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(worker.Status.Error))
            {
                return true;
            }
            if (!string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (worker.Publisher != null && !worker.Publisher.IsConnected) { return true; }

            var now = DateTime.UtcNow;
            if (worker.Status.LastMediaUtc == DateTime.MinValue)
            {
                return worker.Status.DurationSeconds > 60;
            }
            if ((now - worker.Status.LastMediaUtc).TotalSeconds > 45) { return true; }

            // Un socket puede seguir "conectado" mientras el servidor ya no
            // acepta datos: el contador de bits queda a cero aunque HLS siga
            // entregando segmentos. Cortamos y volvemos a publicar.
            return worker.Status.DurationSeconds > 30 &&
                   worker.Status.BitrateKbps <= 1 &&
                   worker.Status.Fps <= 0;
        }

        // ----------------------------------------- ejecución en segundo plano

        /// <summary>
        /// La app llama a esto al volver del segundo plano. Si Xbox revocó la
        /// sesión, se pide otra; si el proceso se cerró, MainPage vuelve a
        /// iniciar el relay desde la intención guardada en LocalSettings.
        /// </summary>
        public void EnsureExtendedExecution(bool force = false)
        {
            IDisposable previous = null;
            lock (_gate)
            {
                if (!_running) { return; }
                if (!force && _executionSession != null) { return; }
                previous = _executionSession;
                _executionSession = null;
            }

            ReleaseExtendedExecution(previous);
            var ignored = RequestExtendedExecutionAsync(force);
        }

        private async Task RequestExtendedExecutionAsync(bool force = false)
        {
            // La Store no permite extendedExecutionUnconstrained sin aprobación.
            //Usamos la sesión básica (sin capacidades restringidas) junto con
            //backgroundMediaPlayback para mantener el relay en segundo plano.
            await RequestBasicExtendedExecutionAsync(force);
        }

        private async Task RequestBasicExtendedExecutionAsync(bool force = false)
        {
            lock (_gate)
            {
                if (!_running || _basicRequesting) { return; }
                if (force) { _executionDenied = false; }
                if (_executionDenied) { return; }
                _basicRequesting = true;
            }

            ExtendedExecutionSession session = null;
            try
            {
                session = new ExtendedExecutionSession
                {
                    Reason = ExtendedExecutionReason.Unspecified,
                    Description = "Plura mantiene el relay de Twitch a otras plataformas"
                };
                session.Revoked += (sender, args) =>
                {
                    App.Log("Basic extended execution revoked: " + args.Reason);
                    lock (_gate)
                    {
                        if (ReferenceEquals(_executionSession, session)) { _executionSession = null; }
                    }
                    try { session.Dispose(); } catch { }
                    ScheduleExtendedExecutionRetry(2);
                };

                var result = await session.RequestExtensionAsync();
                App.Log("Basic extended execution: " + result);
                bool stillRunning;
                bool retry = false;
                lock (_gate)
                {
                    stillRunning = _running;
                    if (result == ExtendedExecutionResult.Allowed && stillRunning)
                    {
                        _executionSession = session;
                        _executionDenied = false;
                        session = null;
                    }
                    else
                    {
                        _executionDenied = true;
                        if (stillRunning) { retry = true; }
                    }
                }

                if (session != null)
                {
                    try { session.Dispose(); } catch { }
                }
                if (retry) { ScheduleExtendedExecutionRetry(30); }
            }
            catch (Exception ex)
            {
                App.Log("Basic extended execution failed: " + ex.Message);
                ScheduleExtendedExecutionRetry(30);
            }
            finally
            {
                lock (_gate) { _basicRequesting = false; }
            }
        }

        /// <summary>
        /// La sesión básica puede denegarse o revocarse de forma transitoria.
        /// Se reintenta en el hilo de la interfaz, que es donde Xbox la concede.
        /// </summary>
        private static void ScheduleExtendedExecutionRetry(int seconds)
        {
            try
            {
                var dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
                    catch { }
                    Current.EnsureExtendedExecution(true);
                });
            }
            catch (Exception ex)
            {
                App.Log("Schedule extended execution retry failed: " + ex.Message);
            }
        }

        private static void ReleaseExtendedExecution(IDisposable session)
        {
            if (session == null) { return; }
            try { session.Dispose(); } catch { }
        }

        private sealed class PlaylistUrls
        {
            public string Master = string.Empty;
            public string Media = string.Empty;
        }

        private sealed class OutputWorker
        {
            public readonly string Key;
            public readonly RelayTarget Target;
            public readonly RelayOutputStatus Status = new RelayOutputStatus();

            public CancellationTokenSource Cts;
            public Task Task;
            public RtmpPublisher Publisher;
            public long VideoFrames;
            public DateTime LastSelfHealUtc = DateTime.MinValue;

            public OutputWorker(RelayTarget target)
            {
                Target = target;
                Key = string.IsNullOrWhiteSpace(target.Platform) ? "destino" : target.Platform;
            }
        }
    }
}
