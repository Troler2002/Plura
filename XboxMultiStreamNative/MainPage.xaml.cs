using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;
using Plura.Services;
using Plura.Services.Streaming;
using Plura.Services.Streaming.OnDevice;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Json;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Plura
{
    public sealed partial class MainPage : Page
    {
        private readonly ObsClient _obs = new ObsClient();
        private readonly SettingsStore _store = new SettingsStore();
        private readonly SecureStore _secure = new SecureStore("Plura");
        private readonly PluraBackendClient _backend;
        private readonly OAuthCredentialProvider _oauth;
        private readonly CredentialResolver _resolver;
        private readonly ObsMultiRtmpSink _sink;
        private readonly StreamingCoordinator _coordinator;
        private readonly OnDeviceRelayClient _relay;
        private CancellationTokenSource _pageCts = new CancellationTokenSource();
        // El relay vive más que la página: no se cancela al salir de segundo plano.
        private CancellationTokenSource _relayCts = new CancellationTokenSource();
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _messageTimer;

        private bool _initializing = true;
        private bool _loaded;
        private bool _isBusy;
        private bool _polling;
        private bool _obsWasConnected;
        private bool _connecting;
        private int _reconnectTicks;
        private string _selectionSignature;
        private PlatformViewModel _primaryPlatform;
        private readonly Dictionary<string, DateTime> _startedAt = new Dictionary<string, DateTime>();

        private bool _consoleMode;
        private bool _relayRunning;
        private bool _restoringRelay;
        private int _channelTicks;
        private RelayStatus _relayStatus;
        private RelayChannelInfo _channelInfo;
        private bool _channelChecking;
        private string _channelCheckError = string.Empty;
        private string _channelAvatarUrl = string.Empty;
        private readonly List<Uri> _avatarFallbacks = new List<Uri>();
        private string _lastDisplayedTitle = string.Empty;

        // El relay se reanuda solo mientras exista una intención guardada. El
        // backoff evita una tormenta de reintentos si Twitch o la clave fallan.
        private DateTime _nextAutoStartUtc = DateTime.MinValue;
        private int _autoStartFailures;
        private DateTime _noMediaSinceUtc = DateTime.MinValue;

        // Se espera a que el usuario deje de escribir antes de consultar el
        // canal: así no se comprueban nombres a medio escribir.
        private readonly DispatcherTimer _channelTimer;
        private DateTime _channelEditAt = DateTime.MinValue;

        // El canal y las Stream Keys se guardan en el almacenamiento local de la
        // consola. No se envían a ningún servidor intermedio.

        public MainPage()
        {
            try
            {
                InitializeComponent();

                Platforms = new ObservableCollection<PlatformViewModel>();
                Destinations = new ObservableCollection<PlatformViewModel>();
                SelectionChips = new ObservableCollection<SelectionChip>();

                _backend = new PluraBackendClient(_secure);
                _oauth = new OAuthCredentialProvider(_backend);
                _resolver = new CredentialResolver(_oauth);
                _sink = new ObsMultiRtmpSink(_obs);
                _coordinator = new StreamingCoordinator(_resolver, _sink);
                _relay = OnDeviceRelayClient.Current;

                CreatePlatforms();
                DataContext = this;

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _pollTimer.Tick += PollTimer_Tick;

                _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                _messageTimer.Tick += MessageTimer_Tick;

                _channelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _channelTimer.Tick += ChannelTimer_Tick;

                Loaded += MainPage_Loaded;
                Unloaded += MainPage_Unloaded;
            }
            catch (Exception ex)
            {
                App.Log("MainPage ctor EXCEPTION: " + ex);
                throw;
            }
        }

        public ObservableCollection<PlatformViewModel> Platforms { get; }

        /// <summary>Solo los destinos reales. Twitch es el origen y no se muestra como tarjeta.</summary>
        public ObservableCollection<PlatformViewModel> Destinations { get; }

        public ObservableCollection<SelectionChip> SelectionChips { get; }

        /// <summary>Plataformas que se pueden activar/desactivar (en modo consola Twitch es el origen).</summary>
        private IEnumerable<PlatformViewModel> ToggleTargets
        {
            get { return Destinations.Where(p => p.ToggleEnabled); }
        }

        private void CreatePlatforms()
        {
            // Solo redes que emiten con una Stream Key sobre un servidor de ingesta
            // fijo. TikTok (URL por cuenta/región) y Plura (servidor propio) no
            // encajan en el flujo de una sola clave y se han retirado.
            // Twitch es el origen: la Xbox emite ahí con su función nativa y no
            // aparece como tarjeta de destino.
            AddPlatform("kick", "Kick", "K", "#53FC18", "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app", true);
            AddPlatform("youtube", "YouTube", "Y", "#FF0000", "rtmp://a.rtmp.youtube.com/live2/", false);
            AddPlatform("facebook", "Facebook Gaming", "FB", "#1877F2", "rtmps://live-api-s.facebook.com:443/rtmp/", false);
            AddPlatform("twitch", "Twitch", "T", "#9146FF", "rtmp://live.twitch.tv/app/", false);
        }

        private void AddPlatform(
            string id, string name, string shortName, string brandHex, string defaultServer, bool darkBadgeInk)
        {
            var platform = new PlatformViewModel(
                id, name, shortName, id, brandHex, defaultServer, darkBadgeInk);

            var saved = _store.LoadPlatform(id);
            platform.Enabled = saved.Enabled;
            platform.StreamKey = saved.StreamKey;

            // El servidor de ingesta es fijo para cada plataforma. Se ignora
            // cualquier URL antigua guardada para no romper el relay con un
            // valor obsoleto de versiones anteriores.
            platform.ServerUrl = defaultServer;

            if (!string.IsNullOrWhiteSpace(saved.TargetName)) { platform.TargetName = saved.TargetName; }

            // Sin acceso con cuenta: todas las plataformas usan Stream Key manual.
            platform.Mode = CredentialMode.Manual;
            platform.Linked = false;
            platform.AccountLabel = string.Empty;
            platform.IsConfigOpen = false;

            Platforms.Add(platform);

            // La tarjeta de Twitch se elimina por completo del mosaico: su panel
            // de origen sigue vivo en la columna izquierda.
            if (!string.Equals(id, "twitch", StringComparison.OrdinalIgnoreCase))
            {
                Destinations.Add(platform);
            }
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pageCts.IsCancellationRequested)
            {
                _pageCts = new CancellationTokenSource();
            }
            if (_relayCts.IsCancellationRequested)
            {
                _relayCts = new CancellationTokenSource();
            }

            if (!_loaded)
            {
                _loaded = true;
                LoadSettings();
            }

            UpdateUI();
            _pollTimer.Start();

            if (_consoleMode)
            {
                // Una actualización rearranca el relay aunque la versión anterior
                // hubiera quedado detenida; la configuración guardada se mantiene.
                _store.ApplyBuildMigration();

                // Secuencia única: estado → Twitch → arranque automático con la
                // configuración ya guardada en la consola.
                var ignored = StartConsoleModeAsync();
            }
            else
            {
                var ignored = TryAutoConnectAsync();
            }
        }

        private void LoadSettings()
        {
            var obs = _store.LoadObs();
            ObsHostBox.Text = obs.Host;
            ObsPortBox.Text = obs.Port.ToString();
            ObsPasswordBox.Password = obs.Password;

            TwitchChannelBox.Text = _store.LoadTwitchChannel();
            _consoleMode = true;
            _store.SaveConsoleMode(true);
            foreach (var platform in Platforms)
            {
                platform.SetConsoleMode(true);
            }
            ApplyModeVisibility();

            SelectComboContent(XboxModelBox, _store.LoadXboxModel());

            var stream = _store.LoadStream();
            SelectComboContent(ResolutionBox, stream.Resolution == "720" ? "720p" : "1080p");
            SelectComboContent(FpsBox, stream.Fps.ToString());
            BitrateBox.Text = stream.Bitrate.ToString();
            SelectComboContent(PresetBox, stream.Preset);

            _initializing = true;
            MasterToggle.IsOn = ToggleTargets.Any() && ToggleTargets.All(p => p.Enabled);
            _initializing = false;
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _pollTimer.Stop();
            _messageTimer.Stop();
            _pageCts.Cancel();
            _obs.Dispose();
        }

        // ---------------------------------------------------------------- OBS

        private async Task TryAutoConnectAsync()
        {
            if (string.IsNullOrWhiteSpace(ObsHostBox.Text)) { return; }

            var connected = await ConnectObsAsync(false);
            if (connected)
            {
                await RefreshTargetsAsync();
            }
        }

        private async Task<bool> ConnectObsAsync(bool notify)
        {
            if (_obs.IsConnected) { return true; }

            var host = (ObsHostBox.Text ?? string.Empty).Trim();
            var port = ParsePort(ObsPortBox.Text);
            if (string.IsNullOrWhiteSpace(host))
            {
                SetObsStatus(false, "Falta el host");
                if (notify) { ShowMessage("Escribe la IP del PC donde corre OBS."); }
                return false;
            }

            if (_connecting) { return false; }

            _connecting = true;
            SetObsStatus(null, "Conectando…");
            try
            {
                await _obs.ConnectAsync(host, port, ObsPasswordBox.Password);
                _obsWasConnected = true;
                SetObsStatus(true, "Conectado · " + host + ":" + port);
                App.Log("OBS connected to " + host + ":" + port);
                if (notify) { ShowMessage("Conexión con OBS establecida."); }
                return true;
            }
            catch (Exception ex)
            {
                _obsWasConnected = false;
                SetObsStatus(false, "Sin conexión");
                App.Log("OBS connect failed to " + host + ":" + port + " -> " + ex.GetType().Name + ": " + ex.Message);
                if (notify) { ShowMessage("No se pudo conectar a OBS: " + ex.Message); }
                return false;
            }
            finally
            {
                _connecting = false;
            }
        }

        private async void TestObsButton_Click(object sender, RoutedEventArgs e)
        {
            TestObsButton.IsEnabled = false;
            try
            {
                await _obs.DisconnectAsync();
                _obsWasConnected = false;

                var connected = await ConnectObsAsync(true);
                if (connected)
                {
                    await RefreshTargetsAsync();
                }
            }
            finally
            {
                TestObsButton.IsEnabled = true;
            }
        }

        private async Task RefreshTargetsAsync()
        {
            if (!_obs.IsConnected) { return; }

            ObsStreamStatus main = null;
            try
            {
                main = await _obs.GetStreamStatusAsync();
            }
            catch (Exception ex)
            {
                App.Log("OBS stream_status failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            List<ObsTargetInfo> targets;
            try
            {
                targets = await _obs.ListTargetsAsync();
            }
            catch (Exception ex)
            {
                _obsWasConnected = false;
                SetObsStatus(false, "Sin conexión");

                var message = ex.Message ?? string.Empty;
                if (message.IndexOf("vendor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = "OBS no tiene el plugin Multiple RTMP Output. " +
                              "Instálalo, reinicia OBS y vuelve a conectar.";
                }

                ShowMessage("OBS: " + message);
                App.Log("OBS list_targets failed: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            _obsWasConnected = true;
            var mainActive = main != null && main.Active;

            foreach (var platform in Platforms)
            {
                if (platform == _primaryPlatform)
                {
                    if (mainActive)
                    {
                        platform.IsLive = true;
                        platform.SetStatus(PlatformStatus.Live);
                        platform.BitrateText = FormatBitrate(main.BitrateBps);
                        platform.FpsText = main.Fps > 0
                            ? ((int)Math.Round(main.Fps)).ToString() + " fps"
                            : "—";
                        platform.DurationText = string.IsNullOrWhiteSpace(main.Duration) ? "—" : main.Duration;
                        _startedAt.Remove(platform.Id);
                    }
                    else if (!platform.IsBusy && !KeepStarting(platform))
                    {
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Idle);
                        platform.ResetStats();
                        _startedAt.Remove(platform.Id);
                    }

                    continue;
                }

                var target = FindTarget(targets, platform);
                if (target == null)
                {
                    if (!platform.IsBusy &&
                        !KeepStarting(platform) &&
                        (platform.Status == PlatformStatus.Live || platform.Status == PlatformStatus.Starting))
                    {
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Idle);
                        platform.ResetStats();
                        _startedAt.Remove(platform.Id);
                    }
                    continue;
                }

                platform.ObsTargetId = target.Id;

                if (target.Streaming)
                {
                    platform.IsLive = true;
                    platform.SetStatus(PlatformStatus.Live);
                    platform.BitrateText = FormatBitrate(target.BitrateBps);
                    platform.FpsText = target.Fps > 0
                        ? ((int)Math.Round(target.Fps)).ToString() + " fps"
                        : "—";
                    platform.DurationText = string.IsNullOrWhiteSpace(target.Duration) ? "—" : target.Duration;
                    _startedAt.Remove(platform.Id);
                }
                else if (!platform.IsBusy &&
                         !KeepStarting(platform) &&
                         (platform.Status == PlatformStatus.Live || platform.Status == PlatformStatus.Starting))
                {
                    platform.IsLive = false;
                    platform.SetStatus(PlatformStatus.Idle);
                    platform.ResetStats();
                    _startedAt.Remove(platform.Id);
                }
            }

            // Si OBS dejó de transmitir por su cuenta, olvidamos la principal.
            if (!mainActive && _primaryPlatform != null &&
                !_primaryPlatform.IsBusy && !KeepStarting(_primaryPlatform))
            {
                _primaryPlatform = null;
                _store.SavePrimaryPlatform(string.Empty);
            }

            // Al reabrir la app con OBS transmitiendo, recuperamos la principal.
            if (mainActive && _primaryPlatform == null && !_isBusy)
            {
                var savedId = _store.LoadPrimaryPlatform();
                if (!string.IsNullOrWhiteSpace(savedId))
                {
                    var restored = Platforms.FirstOrDefault(
                        p => string.Equals(p.Id, savedId, StringComparison.OrdinalIgnoreCase));
                    if (restored != null)
                    {
                        _primaryPlatform = restored;
                        App.Log("OBS primary restored: " + restored.Name);
                    }
                }
            }

            App.Log("OBS list_targets ok: " + targets.Count + " targets; main=" + mainActive);
            UpdateUI();
        }

        /// <summary>
        /// Margen de gracia tras pedir el inicio: el fork tarda ~1 s en publicar
        /// las estadísticas, así que no marcamos Idle de inmediato.
        /// </summary>
        private bool KeepStarting(PlatformViewModel platform)
        {
            if (platform.Status != PlatformStatus.Starting) { return false; }

            DateTime started;
            if (!_startedAt.TryGetValue(platform.Id, out started)) { return false; }

            return (DateTime.UtcNow - started).TotalSeconds < 15;
        }

        private async Task<ObsStreamStatus> SafeGetStreamStatusAsync()
        {
            if (!_obs.IsConnected) { return null; }

            try
            {
                return await _obs.GetStreamStatusAsync();
            }
            catch (Exception ex)
            {
                App.Log("OBS stream_status failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private async void PollTimer_Tick(object sender, object e)
        {
            if (_polling) { return; }

            if (_consoleMode)
            {
                _polling = true;
                try
                {
                    await RefreshRelayAsync(false);

                    // Cada ~6 s para que el título cambie en la app poco después
                    // de cambiarlo en Twitch, sin consultar en cada tick.
                    if (++_channelTicks >= 3)
                    {
                        _channelTicks = 0;
                        await CheckChannelAsync(false);
                    }
                }
                finally
                {
                    _polling = false;
                }

                // Supervisor: si hay una emisión guardada y el relay cayó,
                // vuelve a levantarla en cuanto Twitch esté en vivo.
                try
                {
                    await AutoRestartRelayIfNeededAsync();
                }
                catch (Exception ex)
                {
                    App.Log("Relay supervisor failed: " + ShortText(ex.Message, 220));
                }
                return;
            }

            if (!_obs.IsConnected)
            {
                if (_obsWasConnected)
                {
                    _obsWasConnected = false;
                    SetObsStatus(false, "Desconectado · reintentando…");
                }

                // Reintenta cada ~10 s si hay host configurado.
                if (!string.IsNullOrWhiteSpace(ObsHostBox.Text) && ++_reconnectTicks >= 5)
                {
                    _reconnectTicks = 0;
                    _polling = true;
                    try
                    {
                        await ConnectObsAsync(false);
                    }
                    finally
                    {
                        _polling = false;
                    }
                }

                return;
            }

            _reconnectTicks = 0;
            _polling = true;
            try
            {
                await RefreshTargetsAsync();
            }
            finally
            {
                _polling = false;
            }
        }

        // --------------------------------------------------------- Empezar / parar

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) { return; }

            if (_consoleMode)
            {
                await StartConsoleRelayAsync();
                return;
            }

            var selected = Platforms.Where(p => p.Enabled).ToList();

            if (selected.Count == 0)
            {
                ShowMessage("Activa al menos una plataforma con su switch.");
                return;
            }

            var ready = new List<PlatformViewModel>();
            var incomplete = new List<PlatformViewModel>();

            foreach (var platform in selected)
            {
                if (IsPlatformReady(platform))
                {
                    ready.Add(platform);
                }
                else
                {
                    incomplete.Add(platform);
                    platform.IsConfigOpen = true;
                }
            }

            if (ready.Count == 0)
            {
                ShowMessage("Configura la clave de las plataformas activas (en Kick: clave de retransmisión).");
                return;
            }

            await StartPlatformsAsync(ready);

            if (incomplete.Count > 0)
            {
                ShowMessage("Omitidas por configuración incompleta: " +
                    string.Join(", ", incomplete.Select(p => p.Name)));
            }
        }

        private async Task StartPlatformsAsync(List<PlatformViewModel> selected)
        {
            if (_isBusy) { return; }

            _isBusy = true;
            UpdateUI();

            try
            {
                if (!await ConnectObsAsync(true)) { return; }

                var status = await SafeGetStreamStatusAsync();
                var mainActive = status != null && status.Active;

                // Si OBS ya transmite, conservamos la principal; si no, elegimos
                // la primera plataforma seleccionada como principal.
                if (!mainActive)
                {
                    _primaryPlatform = null;
                    _store.SavePrimaryPlatform(string.Empty);
                }
                var primary = mainActive ? _primaryPlatform : selected.FirstOrDefault();

                var errors = new List<string>();
                var canStartTargets = mainActive;

                if (primary != null && !mainActive)
                {
                    primary.IsBusy = true;
                    primary.IsLive = false;
                    primary.SetStatus(PlatformStatus.Starting);
                    primary.ResetStats();
                    _startedAt[primary.Id] = DateTime.UtcNow;
                    UpdateUI();

                    try
                    {
                        ShowMessage("Iniciando transmisión principal (" + primary.Name + ")…");
                        var endpoint = await _coordinator.StartPrimaryAsync(primary, _pageCts.Token);
                        ApplyAccountLabel(primary, endpoint);

                        _primaryPlatform = primary;
                        _store.SavePrimaryPlatform(primary.Id);
                        canStartTargets = await _obs.WaitForStreamActiveAsync(15000);
                        if (!canStartTargets)
                        {
                            throw new ObsException("OBS no confirmó la transmisión principal.");
                        }

                        primary.IsLive = true;
                        primary.SetStatus(PlatformStatus.Live);
                    }
                    catch (Exception ex)
                    {
                        primary.IsLive = false;
                        primary.SetStatus(PlatformStatus.Error);
                        _primaryPlatform = null;
                        _store.SavePrimaryPlatform(string.Empty);
                        _startedAt.Remove(primary.Id);
                        errors.Add(primary.Name + ": " + ex.Message);
                    }
                    finally
                    {
                        primary.IsBusy = false;
                    }
                }

                if (canStartTargets)
                {
                    foreach (var platform in selected)
                    {
                        if (platform == primary) { continue; }
                        if (platform.IsLive || platform.Status == PlatformStatus.Starting) { continue; }

                        platform.IsBusy = true;
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Starting);
                        platform.ResetStats();
                        _startedAt[platform.Id] = DateTime.UtcNow;
                        UpdateUI();

                        try
                        {
                            var endpoint = await _coordinator.StartAsync(platform, _pageCts.Token);
                            ApplyAccountLabel(platform, endpoint);
                        }
                        catch (Exception ex)
                        {
                            platform.IsLive = false;
                            platform.SetStatus(PlatformStatus.Error);
                            _startedAt.Remove(platform.Id);
                            errors.Add(platform.Name + ": " + ex.Message);
                        }
                        finally
                        {
                            platform.IsBusy = false;
                        }
                    }
                }
                else if (primary != null && errors.Count == 0)
                {
                    errors.Add("La transmisión principal no llegó a estar activa.");
                }

                if (errors.Count > 0)
                {
                    ShowMessage(errors[0]);
                }
                else
                {
                    ShowMessage("Inicio solicitado a " + selected.Count + " plataforma(s).");
                }

                _pollTimer.Start();
                await Task.Delay(1500);
                await RefreshTargetsAsync();
            }
            finally
            {
                _isBusy = false;
                UpdateUI();
            }
        }

        private void ApplyAccountLabel(PlatformViewModel platform, StreamEndpoint endpoint)
        {
            if (endpoint == null) { return; }
            if (endpoint.Source != CredentialSource.Account) { return; }
            if (string.IsNullOrWhiteSpace(endpoint.AccountLabel)) { return; }
            if (string.Equals(endpoint.AccountLabel, platform.AccountLabel, StringComparison.Ordinal)) { return; }

            platform.AccountLabel = endpoint.AccountLabel;
            SavePlatform(platform);
        }

        private async void PlatformStart_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            if (_consoleMode)
            {
                ShowMessage("En modo consola usa el botón Iniciar de arriba.");
                return;
            }

            if (platform.IsLive)
            {
                ShowMessage(platform.Name + " ya está en vivo.");
                return;
            }

            if (!IsPlatformReady(platform))
            {
                platform.IsConfigOpen = true;
                ShowMessage(platform.Name + ": pega la " + platform.StreamKeyDisplayName + ".");
                return;
            }

            await StartPlatformsAsync(new List<PlatformViewModel> { platform });
        }

        private async void PlatformStop_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            if (_consoleMode)
            {
                ShowMessage("En modo consola usa el botón Detener de arriba.");
                return;
            }

            await StopPlatformAsync(platform);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) { return; }

            if (_consoleMode)
            {
                await StopConsoleRelayAsync();
                return;
            }

            var main = await SafeGetStreamStatusAsync();
            var anyLive = Platforms.Any(p => p.IsLive || p.Status == PlatformStatus.Starting) ||
                          (main != null && main.Active) ||
                          _primaryPlatform != null;

            if (!anyLive)
            {
                ShowMessage("No hay transmisiones activas.");
                return;
            }

            await StopSessionAsync();
            ShowMessage("Transmisiones detenidas.");
        }

        /// <summary>
        /// Detiene toda la sesión. Los destinos se paran antes que el stream
        /// principal porque comparten el encoder de OBS.
        /// </summary>
        private async Task StopSessionAsync()
        {
            if (_isBusy) { return; }

            _isBusy = true;
            UpdateUI();

            try
            {
                if (_obs.IsConnected)
                {
                    var liveTargets = Platforms
                        .Where(p => p != _primaryPlatform && (p.IsLive || p.Status == PlatformStatus.Starting))
                        .ToList();

                    foreach (var platform in liveTargets)
                    {
                        try
                        {
                            await _coordinator.StopAsync(platform, _pageCts.Token);
                        }
                        catch (Exception ex)
                        {
                            App.Log("OBS stop target failed (" + platform.Name + "): " + ex.Message);
                        }
                    }

                    var main = await SafeGetStreamStatusAsync();
                    if (_primaryPlatform != null || (main != null && main.Active))
                    {
                        try
                        {
                            await _coordinator.StopPrimaryAsync(_pageCts.Token);
                        }
                        catch (Exception ex)
                        {
                            ShowMessage("OBS: " + ex.Message);
                        }
                    }
                }
            }
            finally
            {
                _primaryPlatform = null;
                _store.SavePrimaryPlatform(string.Empty);
                _startedAt.Clear();

                foreach (var platform in Platforms)
                {
                    platform.IsLive = false;
                    if (platform.Status != PlatformStatus.Error)
                    {
                        platform.SetStatus(PlatformStatus.Idle);
                    }
                    platform.ResetStats();
                }

                _isBusy = false;
                UpdateUI();
            }
        }

        private async Task StopPlatformAsync(PlatformViewModel platform)
        {
            if (_isBusy) { return; }

            // Detener la principal obliga a detener todo (encoder compartido).
            if (platform == _primaryPlatform)
            {
                await StopSessionAsync();
                ShowMessage("Se detuvo la plataforma principal; también se detuvieron los demás destinos.");
                return;
            }

            platform.IsBusy = true;
            UpdateUI();

            try
            {
                if (_obs.IsConnected)
                {
                    try
                    {
                        await _coordinator.StopAsync(platform, _pageCts.Token);
                    }
                    catch (Exception ex)
                    {
                        ShowMessage(platform.Name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                platform.IsLive = false;
                platform.SetStatus(PlatformStatus.Idle);
                platform.ResetStats();
                platform.IsBusy = false;
                _startedAt.Remove(platform.Id);
                UpdateUI();
            }
        }

        // ------------------------------------------------------------- Modo consola

        private void ApplyModeVisibility()
        {
            // Plura funciona únicamente en modo consola: la Xbox emite a Twitch
            // con su función nativa y el motor local reparte la señal al resto.
            ObsCard.Visibility = Visibility.Collapsed;
            XboxCard.Visibility = Visibility.Collapsed;
            StreamCard.Visibility = Visibility.Collapsed;
            ConsoleCard.Visibility = Visibility.Visible;
            StartButton.Content = "▶  Iniciar";
        }

        private Task StartConsoleRelayAsync()
        {
            return StartConsoleRelayAsync(null, null, false);
        }

        /// <summary>
        /// Inicia el relay desde la UI o desde la sesión guardada al volver del
        /// segundo plano. Guardar la intención permite reanudarlo sin PC.
        /// </summary>
        private async Task StartConsoleRelayAsync(
            string explicitChannel, IList<string> explicitTargets, bool fromRestore)
        {
            if (_isBusy || (_restoringRelay && !fromRestore)) { return; }

            if (_pageCts.IsCancellationRequested) { _pageCts = new CancellationTokenSource(); }
            if (_relayCts.IsCancellationRequested) { _relayCts = new CancellationTokenSource(); }

            if (!ApplyBackendUrl()) { return; }

            var channel = string.IsNullOrWhiteSpace(explicitChannel)
                ? NormalizeTwitchLogin(TwitchChannelBox.Text)
                : NormalizeTwitchLogin(explicitChannel);
            if (channel.Length == 0)
            {
                if (!fromRestore)
                {
                    ShowMessage("Escribe el canal de Twitch que emite desde la Xbox.");
                }
                return;
            }

            var selected = explicitTargets == null
                ? Platforms
                    .Where(p => p.Enabled &&
                                !string.Equals(p.Id, "twitch", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : Platforms
                    .Where(p => explicitTargets.Any(id => string.Equals(
                                    id, p.Id, StringComparison.OrdinalIgnoreCase)) &&
                                !string.Equals(p.Id, "twitch", StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (selected.Count == 0)
            {
                ShowMessage("Activa al menos una plataforma destino. En modo consola Twitch es el origen.");
                return;
            }

            _isBusy = true;
            UpdateUI();

            try
            {
                var targets = new List<RelayTarget>();
                var incomplete = new List<string>();

                foreach (var platform in selected)
                {
                    platform.IsBusy = true;
                    platform.SetStatus(PlatformStatus.Starting);
                    platform.ResetStats();
                    UpdateUI();

                    try
                    {
                        var resolved = await _resolver.ResolveAsync(platform, _pageCts.Token);
                        if (!resolved.Success)
                        {
                            incomplete.Add(platform.Name);
                            platform.SetStatus(PlatformStatus.Error);
                            continue;
                        }

                        ApplyAccountLabel(platform, resolved.Endpoint);
                        targets.Add(new RelayTarget
                        {
                            Platform = platform.Id,
                            ServerUrl = resolved.Endpoint.ServerUrl,
                            StreamKey = resolved.Endpoint.StreamKey,
                            AccountLabel = platform.AccountLabel
                        });
                    }
                    catch (Exception ex)
                    {
                        incomplete.Add(platform.Name);
                        platform.SetStatus(PlatformStatus.Error);
                        App.Log("Relay credentials failed (" + platform.Name + "): " + ex.Message);
                    }
                    finally
                    {
                        platform.IsBusy = false;
                    }
                }

                if (targets.Count == 0)
                {
                    if (!fromRestore)
                    {
                        ShowMessage("No hay destinos listos. Pega la clave de transmisión de al menos una plataforma (en Kick: clave de retransmisión).");
                    }
                    return;
                }

                // La intención se guarda ANTES de conectar: si Xbox mata la app
                // a mitad de arranque, el supervisor podrá reanudar el relay.
                _store.SaveRelaySession(channel, targets.Select(t => t.Platform));
                _store.SaveTwitchChannel(channel);
                _store.SetRelayUserStopped(false);

                ShowMessage("Iniciando relay local desde Twitch…");
                App.Log("Starting relay: " + channel + " -> " +
                    string.Join(", ", targets.Select(t => t.Platform)));
                // Token propio del relay: no muere si la página sale de pantalla.
                var status = await _relay.StartAsync(channel, targets, _relayCts.Token);
                ApplyRelayStatus(status);
                TrackRelayMedia(status);
                _autoStartFailures = 0;
                _nextAutoStartUtc = DateTime.MinValue;

                // Audio silencioso en segundo plano: mantiene el proceso vivo
                // cuando el jugador abre un juego o vuelve al dashboard.
                BackgroundKeepAlive.Start();

                var message = "Relay activo en la consola: Twitch → " +
                    string.Join(" + ", targets.Select(t => t.Platform)) + ".";
                if (incomplete.Count > 0)
                {
                    message += " Sin configurar: " + string.Join(", ", incomplete) + ".";
                }
                ShowMessage(message);

                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                _autoStartFailures++;
                _nextAutoStartUtc = DateTime.UtcNow.AddSeconds(
                    10 + Math.Min(_autoStartFailures, 6) * 10);
                if (!fromRestore) { ShowMessage("Relay: " + ex.Message); }
                App.Log("Relay start failed: " + ShortText(ex.Message, 220));
            }
            finally
            {
                _isBusy = false;
                UpdateUI();
            }
        }

        private async Task StopConsoleRelayAsync()
        {
            if (_isBusy) { return; }

            _isBusy = true;
            UpdateUI();

            try
            {
                var status = await _relay.StopAsync(_relayCts.Token);
                ApplyRelayStatus(status);
                BackgroundKeepAlive.Stop();
                _store.ClearRelaySession();
                _store.SetRelayUserStopped(true);
                _nextAutoStartUtc = DateTime.MinValue;
                _autoStartFailures = 0;
                _noMediaSinceUtc = DateTime.MinValue;
                ShowMessage("Relay detenido. La emisión de la Xbox a Twitch sigue activa.");
            }
            catch (Exception ex)
            {
                ShowMessage("Relay: " + ex.Message);
            }
            finally
            {
                _isBusy = false;
                UpdateUI();
            }
        }

        /// <summary>
        /// Reanuda una emisión guardada cuando la app vuelve del segundo plano
        /// o se abre de nuevo. Si el relay sigue vivo, solo renueva la sesión
        /// de ejecución extendida.
        /// </summary>
        internal async Task RestoreRelaySessionAsync(bool forceExecution = false)
        {
            if (!_loaded || !_consoleMode || _isBusy || _restoringRelay) { return; }

            var session = _store.LoadRelaySession();
            if (!session.Active ||
                string.IsNullOrWhiteSpace(session.Channel) ||
                session.Targets.Count == 0)
            {
                return;
            }

            _restoringRelay = true;
            try
            {
                var status = await _relay.GetStatusAsync(_relayCts.Token);
                if (status != null && status.Running && IsRelayHealthy(status))
                {
                    ApplyRelayStatus(status);
                    TrackRelayMedia(status);
                    BackgroundKeepAlive.Start();
                    _relay.EnsureExtendedExecution(forceExecution);
                    if (!_pollTimer.IsEnabled) { _pollTimer.Start(); }
                    return;
                }

                App.Log("Restoring saved relay: " + session.Channel + " -> " +
                    string.Join(", ", session.Targets));
                _nextAutoStartUtc = DateTime.MinValue;
                await AutoRestartRelayIfNeededAsync(true);
                BackgroundKeepAlive.Start();
                _relay.EnsureExtendedExecution(forceExecution);
                if (!_pollTimer.IsEnabled) { _pollTimer.Start(); }
            }
            catch (Exception ex)
            {
                App.Log("Restore saved relay failed: " + ShortText(ex.Message, 220));
            }
            finally
            {
                _restoringRelay = false;
            }
        }

        /// <summary>
        /// Mantiene el estado de "sin media" para detectar un relay colgado
        /// (RTMP aceptado pero sin segmentos útiles) y reiniciarlo.
        /// </summary>
        private void TrackRelayMedia(RelayStatus status)
        {
            if (status == null || !status.Running)
            {
                _noMediaSinceUtc = DateTime.MinValue;
                return;
            }

            var outputs = status.Outputs ?? new List<RelayOutputStatus>();
            var flowing = outputs.Count > 0 &&
                          string.IsNullOrWhiteSpace(status.Error) &&
                          outputs.All(o =>
                              string.Equals(o.State, "running", StringComparison.OrdinalIgnoreCase) &&
                              string.IsNullOrWhiteSpace(o.Error) &&
                              o.MediaFresh &&
                              (o.BitrateKbps > 1 || o.Fps > 0));
            if (flowing)
            {
                _noMediaSinceUtc = DateTime.MinValue;
                return;
            }

            if (_noMediaSinceUtc == DateTime.MinValue)
            {
                _noMediaSinceUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Supervisor del relay. Respeta la intención guardada: si el usuario
        /// pulsó Detener no hay sesión y no se reanuda nada.
        /// </summary>
        private async Task AutoRestartRelayIfNeededAsync(bool ignoreGuards = false)
        {
            if (!_loaded || !_consoleMode) { return; }
            if (!ignoreGuards && (_isBusy || _restoringRelay || _polling)) { return; }

            var session = _store.LoadRelaySession();
            if (!session.Active ||
                string.IsNullOrWhiteSpace(session.Channel) ||
                session.Targets.Count == 0)
            {
                return;
            }

            if (_channelInfo == null ||
                !string.Equals(_channelInfo.Login, session.Channel, StringComparison.OrdinalIgnoreCase))
            {
                await CheckChannelAsync(false);
            }

            if (_channelInfo == null || !_channelInfo.Live)
            {
                if (_relayRunning) { return; }
                return;
            }

            var status = await _relay.GetStatusAsync(_relayCts.Token);
            ApplyRelayStatus(status);
            TrackRelayMedia(status);

            var outputFailed = status != null && status.Outputs != null &&
                               status.Outputs.Any(o =>
                                   !string.IsNullOrWhiteSpace(o.Error) ||
                                   string.Equals(o.State, "error", StringComparison.OrdinalIgnoreCase));
            var staleOutput = status != null && status.Running &&
                              status.UptimeSeconds > 20 &&
                              status.Outputs.Count > 0 &&
                              status.Outputs.Any(o => !IsOutputSending(o));
            var relayFailed = status == null || !status.Running || outputFailed || staleOutput ||
                              !string.IsNullOrWhiteSpace(status.Error);
            var stuck = _noMediaSinceUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - _noMediaSinceUtc).TotalSeconds > 45;
            if (!relayFailed && !stuck)
            {
                _autoStartFailures = 0;
                _nextAutoStartUtc = DateTime.MinValue;
                return;
            }

            if (DateTime.UtcNow < _nextAutoStartUtc) { return; }

            App.Log(relayFailed
                ? "Relay con error de salida; reiniciando destinos."
                : "Relay sin media durante 45 s; reiniciando destinos.");
            await _relay.StopAsync(_relayCts.Token);
            ApplyRelayStatus(await _relay.GetStatusAsync(_relayCts.Token));
            _noMediaSinceUtc = DateTime.MinValue;

            var reason = stuck ? "reinicio por falta de media"
                       : relayFailed ? "reinicio por error de salida"
                       : "reanudación automática";
            App.Log("Auto-start relay (" + reason + "): " + session.Channel + " -> " +
                string.Join(", ", session.Targets));
            _nextAutoStartUtc = DateTime.UtcNow.AddSeconds(
                5 + Math.Min(_autoStartFailures, 8) * 5);
            await StartConsoleRelayAsync(session.Channel, session.Targets, true);
        }

        internal void PrepareForSuspension()
        {
            // Una suspensión puede congelar un arranque en curso. Se liberan los
            // guardos para que ResumeRelayAfterBackground pueda reanudarlo.
            _restoringRelay = false;
            _isBusy = false;
            _nextAutoStartUtc = DateTime.MinValue;
        }

        internal void ResumeAfterBackground()
        {
            // El silencio de fondo puede haberse detenido al suspender: se
            // rearma aquí también, no solo al entrar en segundo plano.
            if (_relayRunning) { BackgroundKeepAlive.Start(); }
            if (_loaded && !_pollTimer.IsEnabled) { _pollTimer.Start(); }
            var ignored = RestoreRelaySessionAsync(true);
        }

        /// <summary>
        /// Al pasar a segundo plano se garantiza que el audio silencioso siga
        /// activo: es lo que evita que Xbox congele el proceso del relay.
        /// </summary>
        internal void KeepAliveInBackground()
        {
            if (_relayRunning) { BackgroundKeepAlive.Start(); }
        }

        private async Task StartConsoleModeAsync()
        {
            await RefreshRelayAsync(false);
            await CheckChannelAsync(false);
            await RestoreRelaySessionAsync();
            await AutoStartConfiguredRelayAsync();
        }

        /// <summary>
        /// Si no hay una sesión guardada pero la consola ya tiene un canal y una
        /// Stream Key configurados, arranca igualmente al abrir la app. El usuario
        /// solo necesita pulsar Detener para impedirlo en el futuro.
        /// </summary>
        private async Task AutoStartConfiguredRelayAsync()
        {
            if (!_loaded || !_consoleMode || _isBusy || _restoringRelay) { return; }
            if (_store.LoadRelayUserStopped()) { return; }

            var session = _store.LoadRelaySession();
            if (session.Active || (_relayStatus != null && _relayStatus.Running)) { return; }

            var login = NormalizeTwitchLogin(TwitchChannelBox.Text);
            if (login.Length == 0 || _channelInfo == null || !_channelInfo.Live) { return; }

            var targets = Platforms
                .Where(p => p.Enabled &&
                            !string.Equals(p.Id, "twitch", StringComparison.OrdinalIgnoreCase) &&
                            IsPlatformReady(p))
                .Select(p => p.Id)
                .ToList();
            if (targets.Count == 0) { return; }

            App.Log("Auto-start configured relay: " + login + " -> " + string.Join(", ", targets));
            await StartConsoleRelayAsync(login, targets, true);
        }

        private async Task RefreshRelayAsync(bool notify)
        {
            if (!_consoleMode || !_loaded) { return; }

            try
            {
                var status = await _relay.GetStatusAsync(_relayCts.Token);
                ApplyRelayStatus(status);
                TrackRelayMedia(status);

                if (string.Equals(BackendStatusText.Text, "Sin conexión", StringComparison.Ordinal) ||
                    string.Equals(BackendStatusText.Text, "Sin probar", StringComparison.Ordinal))
                {
                    BackendStatusText.Text = "Conectado";
                    BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 126, 232, 160));
                }

                if (notify)
                {
                    ShowMessage(status.Running
                        ? "Relay activo en " + status.Channel + "."
                        : "El relay está detenido.");
                }
            }
            catch (Exception ex)
            {
                _relayRunning = false;
                RelayStatusText.Text = "SIN MOTOR";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 42, 12, 17));
                RelayDetailText.Text =
                    "El motor local no pudo consultar Twitch: " + ex.Message;
                foreach (var platform in Platforms)
                {
                    platform.IsLive = false;
                    platform.SetStatus(PlatformStatus.Idle);
                    platform.ResetStats();
                }
                BackendStatusText.Text = "Sin conexión";
                BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                if (notify) { ShowMessage("Relay: " + ex.Message); }
            }
        }

        /// <summary>
        /// Un destino solo está realmente en directo si RTMP sigue recibiendo
        /// media reciente. El estado "running" por sí solo puede quedarse
        /// colgado cuando Xbox congela el proceso o el servidor RTMP deja de
        /// aceptar datos sin cerrar el socket.
        /// </summary>
        private static bool IsOutputSending(RelayOutputStatus output)
        {
            return output != null &&
                   string.Equals(output.State, "running", StringComparison.OrdinalIgnoreCase) &&
                   string.IsNullOrWhiteSpace(output.Error) &&
                   output.MediaFresh &&
                   (output.BitrateKbps > 1 || output.Fps > 0);
        }

        private static bool IsRelayHealthy(RelayStatus status)
        {
            if (status == null || !status.Running ||
                !string.IsNullOrWhiteSpace(status.Error))
            {
                return false;
            }
            if (status.Outputs == null || status.Outputs.Count == 0) { return false; }
            if (status.Outputs.Any(o => !string.IsNullOrWhiteSpace(o.Error) ||
                string.Equals(o.State, "error", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Durante los primeros segundos no hay métricas todavía; no hay que
            // reiniciar un relay que acaba de arrancar.
            if (status.UptimeSeconds <= 15) { return true; }
            return status.Outputs.All(IsOutputSending);
        }

        private void ApplyRelayStatus(RelayStatus status)
        {
            _relayStatus = status;
            if (status != null && !string.IsNullOrWhiteSpace(status.Title))
            {
                SetStreamTitle(status.Title);
            }
            var running = status != null && status.Running;
            var outputFailed = status != null && status.Outputs.Any(o =>
                !string.IsNullOrWhiteSpace(o.Error) ||
                string.Equals(o.State, "error", StringComparison.OrdinalIgnoreCase));
            var healthy = IsRelayHealthy(status);
            var noSignal = running && !healthy && !outputFailed &&
                           string.IsNullOrWhiteSpace(status?.Error);
            var wasRunning = _relayRunning;
            _relayRunning = running;

            if (healthy)
            {
                RelayStatusText.Text = "RELAY EN VIVO";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35));
            }
            else if (running && noSignal)
            {
                RelayStatusText.Text = "RELAY SIN SEÑAL";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 196, 92));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 74, 52, 8));
            }
            else if (running)
            {
                RelayStatusText.Text = "RELAY CON ERROR";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 70, 12, 20));
            }
            else
            {
                RelayStatusText.Text = "RELAY DETENIDO";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 34));
            }

            foreach (var platform in Platforms)
            {
                if (string.Equals(platform.Id, "twitch", StringComparison.OrdinalIgnoreCase))
                {
                    if (running || wasRunning)
                    {
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Idle);
                        platform.ResetStats();
                    }
                    continue;
                }

                var output = status == null
                    ? null
                    : status.Outputs.FirstOrDefault(o =>
                        string.Equals(o.Platform, platform.Id, StringComparison.OrdinalIgnoreCase));

                if (running && output != null)
                {
                    if (string.Equals(output.State, "error", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(output.Error))
                    {
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Error);
                        platform.ResetStats();
                        continue;
                    }

                    var sending = IsOutputSending(output);
                    platform.IsLive = sending;
                    platform.SetStatus(sending ? PlatformStatus.Live : PlatformStatus.Starting);
                    platform.BitrateText = FormatBitrate(output.BitrateKbps * 1000.0);
                    platform.FpsText = output.Fps > 0
                        ? Math.Round(output.Fps).ToString() + " fps"
                        : "—";
                    platform.DurationText = FormatDuration(
                        output.DurationSeconds > 0
                            ? output.DurationSeconds
                            : (status != null ? status.UptimeSeconds : 0));
                }
                else if (wasRunning ||
                         platform.Status == PlatformStatus.Live ||
                         platform.Status == PlatformStatus.Starting)
                {
                    platform.IsLive = false;
                    if (platform.Status != PlatformStatus.Error)
                    {
                        platform.SetStatus(PlatformStatus.Idle);
                    }
                    platform.ResetStats();
                }
            }

            if (status != null)
            {
                if (running)
                {
                    var parts = status.Outputs
                        .Select(o => o.Platform + ": " + o.State +
                                     (IsOutputSending(o) ? " · enviando" : " · sin señal") +
                                     (string.IsNullOrWhiteSpace(o.Error)
                                         ? string.Empty
                                         : " (" + o.Error + ")"))
                        .ToList();
                    RelayDetailText.Text = "Canal " + status.Channel +
                        (parts.Count > 0 ? " · " + string.Join(" · ", parts) : string.Empty) +
                        ((status.Error ?? string.Empty).Length > 0 ? " · " + status.Error : string.Empty);
                }
                else if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    RelayDetailText.Text = status.Error;
                }
            }

            UpdateUI();
        }

        /// <summary>
        /// Muestra en la cabecera y en la tarjeta de estado el título real del
        /// directo de Twitch. Se llama tanto al consultar Twitch como al leer el
        /// estado del relay, así que el cambio se refleja aunque la app esté
        /// emitiendo en segundo plano.
        /// </summary>
        private void SetStreamTitle(string title)
        {
            var clean = (title ?? string.Empty).Trim();
            var hasTitle = clean.Length > 0;
            if (!hasTitle)
            {
                clean = _channelInfo != null && _channelInfo.Live
                    ? "El directo de Twitch todavía no tiene título"
                    : "Esperando el título de Twitch…";
            }

            if (string.Equals(clean, _lastDisplayedTitle, StringComparison.Ordinal)) { return; }

            var changed = hasTitle;
            _lastDisplayedTitle = clean;
            ChannelTitleText.Text = clean;
            HeaderTitleText.Text = clean;
            TitleSourceText.Text = hasTitle
                ? "Copiado automáticamente de Twitch · cambia al instante si lo editas allí."
                : "Cuando pongas título al directo en Twitch aparecerá aquí automáticamente.";

            if (changed)
            {
                App.Log("Twitch title now: " + ShortText(clean, 180));
            }
        }

        private async Task CheckChannelAsync(bool notify)
        {
            if (!_consoleMode) { return; }

            var login = NormalizeTwitchLogin(TwitchChannelBox.Text);
            if (login.Length == 0)
            {
                _channelInfo = null;
                ClearChannelAvatar();
                ChannelAvatarFallback.Text = "T";
                ChannelStatusText.Text = "Escribe tu canal cuando quieras; se guarda automáticamente.";
                ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
                SetStreamTitle(string.Empty);
                _channelChecking = false;
                _channelCheckError = string.Empty;
                UpdateStartIndicator();
                return;
            }

            _channelChecking = true;
            _channelCheckError = string.Empty;
            UpdateStartIndicator();

            try
            {
                var info = await _relay.CheckChannelAsync(login, _pageCts.Token);

                // Si el usuario cambió de canal mientras se consultaba, se
                // descarta la respuesta anterior.
                if (!string.Equals(login, NormalizeTwitchLogin(TwitchChannelBox.Text),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _channelInfo = info;
                _channelCheckError = string.Empty;
                await ApplyChannelAvatarAsync(info);

                if (info.Live)
                {
                    ChannelStatusText.Text = "EN VIVO · " + info.DisplayName +
                        (info.Viewers > 0
                            ? " · " + info.Viewers.ToString("N0") + " espectadores"
                            : string.Empty);
                    ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 126, 232, 160));
                }
                else
                {
                    ChannelStatusText.Text = "Fuera de línea · " + info.DisplayName;
                    ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 176, 32));
                }

                SetStreamTitle(info.Title);

                if (notify)
                {
                    ShowMessage(info.Live
                        ? "Canal en vivo: " + info.DisplayName + "."
                        : "El canal está fuera de línea. Inicia la emisión en la Xbox.");
                }
            }
            catch (Exception ex)
            {
                _channelInfo = null;
                ClearChannelAvatar();
                ChannelStatusText.Text = "No se pudo comprobar el canal.";
                ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                _channelCheckError = ex.Message;
                App.Log("Check channel failed: " + ex.Message);
                if (notify) { ShowMessage("Twitch: " + ex.Message); }
            }
            finally
            {
                _channelChecking = false;
                UpdateUI();
            }
        }

        private async Task ApplyChannelAvatarAsync(RelayChannelInfo info)
        {
            var fallback = string.IsNullOrWhiteSpace(info.DisplayName)
                ? info.Login
                : info.DisplayName;
            var initial = string.IsNullOrWhiteSpace(fallback)
                ? "T"
                : fallback.Substring(0, 1).ToUpperInvariant();
            ChannelAvatarFallback.Text = initial;

            var url = (info.AvatarUrl ?? string.Empty).Trim();
            var identity = url.Length > 0 ? url : "login:" + (info.Login ?? string.Empty);
            if (string.Equals(identity, _channelAvatarUrl, StringComparison.Ordinal))
            {
                // Ya hay una foto cargada para este canal: no se toca la vista
                // para no taparla con la inicial en cada sondeo.
                return;
            }

            // Va a cambiar la foto: se deja la inicial visible hasta que la
            // imagen esté lista.
            ChannelAvatarFallback.Visibility = Visibility.Visible;
            ChannelAvatarImage.Background = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22));

            // 1) El backend ya descargó la foto y la manda en base64. Así la
            //    consola no depende de poder llegar a la CDN de Twitch.
            var base64 = (info.AvatarBase64 ?? string.Empty).Trim();
            if (base64.Length > 0)
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        var writer = new DataWriter(stream.GetOutputStreamAt(0));
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                        writer.Dispose();

                        stream.Seek(0);
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);

                        _channelAvatarUrl = identity;
                        ShowChannelAvatar(bitmap);
                        App.Log("Avatar base64 ok: " + identity + " (" + bytes.Length + " bytes)");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Avatar base64 failed: " + ex.Message);
                }
            }

            // 2) Respaldo: usar directamente la URL pública de la foto de Twitch.
            _avatarFallbacks.Clear();

            if (url.Length > 0 &&
                Uri.TryCreate(url, UriKind.Absolute, out var directUri) &&
                (string.Equals(directUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(directUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
            {
                _avatarFallbacks.Add(directUri);
            }

            if (_avatarFallbacks.Count == 0)
            {
                ClearChannelAvatar();
                ChannelAvatarFallback.Text = initial;
                return;
            }

            _channelAvatarUrl = identity;
            TryLoadNextAvatar();
        }

        private void ShowChannelAvatar(ImageSource source)
        {
            ChannelAvatarImage.Background = new ImageBrush
            {
                ImageSource = source,
                Stretch = Stretch.UniformToFill
            };
            ChannelAvatarFallback.Visibility = Visibility.Collapsed;
        }

        private bool TryLoadNextAvatar()
        {
            if (_avatarFallbacks.Count == 0)
            {
                return false;
            }

            var uri = _avatarFallbacks[0];
            _avatarFallbacks.RemoveAt(0);

            var remote = new BitmapImage
            {
                DecodePixelWidth = 112,
                CreateOptions = BitmapCreateOptions.None
            };
            remote.ImageFailed += ChannelAvatarImage_ImageFailed;
            remote.ImageOpened += ChannelAvatarRemote_ImageOpened;
            remote.UriSource = uri;
            ChannelAvatarImage.Background = new ImageBrush
            {
                ImageSource = remote,
                Stretch = Stretch.UniformToFill
            };
            App.Log("Avatar remote: " + uri);
            return true;
        }

        private void ClearChannelAvatar()
        {
            _channelAvatarUrl = string.Empty;
            _avatarFallbacks.Clear();
            ChannelAvatarImage.Background = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22));
            ChannelAvatarFallback.Visibility = Visibility.Visible;
        }

        private void ChannelAvatarRemote_ImageOpened(object sender, RoutedEventArgs e)
        {
            ChannelAvatarFallback.Visibility = Visibility.Collapsed;
        }

        private void ChannelAvatarImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Si la foto no carga (sin red, URL caída) se prueba el siguiente
            // respaldo y, si no queda ninguno, se deja la inicial del canal.
            App.Log("Avatar image failed: " + (e != null ? e.ErrorMessage : string.Empty));
            ChannelAvatarImage.Background = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22));
            ChannelAvatarFallback.Visibility = Visibility.Visible;
            if (TryLoadNextAvatar()) { return; }
            ClearChannelAvatar();
        }

        private async void CheckChannel_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyBackendUrl()) { return; }
            await CheckChannelAsync(true);
        }

        /// <summary>
        /// Pega el nombre o el enlace del canal desde el portapapeles, lo
        /// normaliza y comprueba el canal (incluida su foto) al instante.
        /// </summary>
        private async void PasteTwitchChannel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    ShowMessage("El portapapeles no tiene texto. Copia el nombre o el enlace del canal de Twitch.");
                    return;
                }

                var text = (await content.GetTextAsync()).Trim();
                var login = NormalizeTwitchLogin(text);
                if (!IsValidTwitchLogin(login))
                {
                    ShowMessage("El portapapeles no contiene un canal de Twitch válido. Copia algo como twitch.tv/tu_canal.");
                    return;
                }

                TwitchChannelBox.Text = login;
                _store.SaveTwitchChannel(login);
                ShowMessage("Canal pegado: " + login + ".");

                if (ApplyBackendUrl())
                {
                    await CheckChannelAsync(true);
                }
            }
            catch (Exception ex)
            {
                App.Log("Paste Twitch channel failed: " + ex.Message);
                ShowMessage("No se pudo leer el portapapeles: " + ex.Message);
            }
        }

        private static bool IsValidTwitchLogin(string login)
        {
            if (string.IsNullOrEmpty(login) || login.Length < 3 || login.Length > 25)
            {
                return false;
            }

            foreach (var c in login)
            {
                var valid = (c >= 'a' && c <= 'z') ||
                            (c >= '0' && c <= '9') ||
                            c == '_';
                if (!valid) { return false; }
            }

            return true;
        }

        private void TwitchChannelBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }

            var login = NormalizeTwitchLogin(TwitchChannelBox.Text);
            _store.SaveTwitchChannel(login);

            // Si el canal escrito ya no coincide con el que muestra la foto,
            // se oculta la imagen anterior hasta comprobar el nuevo.
            if (_channelInfo != null &&
                !string.Equals(login, _channelInfo.Login, StringComparison.OrdinalIgnoreCase))
            {
                _channelInfo = null;
                ClearChannelAvatar();
            }

            _channelEditAt = DateTime.UtcNow;
            if (_consoleMode) { _channelTimer.Start(); }
        }

        private async void ChannelTimer_Tick(object sender, object e)
        {
            _channelTimer.Stop();
            if ((DateTime.UtcNow - _channelEditAt).TotalMilliseconds < 800) { return; }
            if (!_consoleMode || _polling) { return; }

            _polling = true;
            try
            {
                await CheckChannelAsync(false);
            }
            catch (Exception ex)
            {
                App.Log("Channel check: " + ex.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        private static string NormalizeTwitchLogin(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) { return string.Empty; }

            var marker = text.IndexOf("twitch.tv", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                // Acepta enlaces como https://www.twitch.tv/canal o m.twitch.tv/canal.
                text = text.Substring(marker + "twitch.tv".Length).TrimStart('/');
                var slash = text.IndexOfAny(new[] { '/', '?', '#' });
                if (slash >= 0) { text = text.Substring(0, slash); }
            }

            text = text.Trim().TrimStart('@', '#');
            return text.ToLowerInvariant();
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds <= 0) { return "—"; }

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? ((int)span.TotalHours).ToString("00") + ":" +
                  span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00")
                : span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }

        // ------------------------------------------------------------- Controles

        private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_initializing) { return; }

            var on = MasterToggle.IsOn;
            _initializing = true;
            foreach (var platform in ToggleTargets)
            {
                platform.Enabled = on;
            }
            _initializing = false;

            SaveAll();
            UpdateUI();
        }

        private void PlatformToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_initializing) { return; }

            var toggle = sender as ToggleSwitch;
            var platform = toggle?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            platform.Enabled = toggle.IsOn;
            SaveAll();
            UpdateUI();
        }

        // --------------------------------------------------------- Motor local

        private bool ApplyBackendUrl()
        {
            // Ya no hay URL que configurar: el relay vive dentro de la consola.
            return true;
        }

        private void BackendUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Conservado por compatibilidad con XAML antiguo; no hace nada.
        }

        private async void TestBackend_Click(object sender, RoutedEventArgs e)
        {
            TestBackendButton.IsEnabled = false;
            BackendStatusText.Text = "Comprobando…";
            BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));

            try
            {
                var login = NormalizeTwitchLogin(TwitchChannelBox.Text);
                if (login.Length > 0)
                {
                    var info = await _relay.CheckChannelAsync(login, _pageCts.Token);
                    BackendStatusText.Text = info.Live ? "Listo · canal en vivo" : "Listo · canal fuera de línea";
                }
                else
                {
                    BackendStatusText.Text = "Listo";
                }

                BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 126, 232, 160));
                ShowMessage("El motor local está listo: no se necesita PC ni backend.");
            }
            catch (Exception ex)
            {
                BackendStatusText.Text = ex.Message;
                BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                ShowMessage("Motor local: " + ex.Message);
            }
            finally
            {
                TestBackendButton.IsEnabled = true;
            }
        }

        private void StreamKeyBox_Loaded(object sender, RoutedEventArgs e)
        {
            var box = sender as PasswordBox;
            var platform = box?.DataContext as PlatformViewModel;
            if (platform != null && box.Password != platform.StreamKey)
            {
                box.Password = platform.StreamKey;
            }
        }

        private void StreamKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = sender as PasswordBox;
            var platform = box?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            if (platform.StreamKey != box.Password)
            {
                platform.StreamKey = box.Password;
                SavePlatform(platform);
                UpdateUI();
            }
        }

        /// <summary>Pega la clave de la plataforma desde el portapapeles.</summary>
        private async void PasteStreamKey_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            if (button == null) { return; }

            var platform = button.DataContext as PlatformViewModel;
            var keyName = platform == null
                ? "clave de la plataforma"
                : platform.StreamKeyDisplayName;
            var keyNameTitle = char.ToUpperInvariant(keyName[0]) + keyName.Substring(1);

            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    ShowMessage("El portapapeles no tiene texto. Copia la " + keyName + " primero.");
                    return;
                }

                var text = (await content.GetTextAsync()).Trim();
                if (text.Length == 0)
                {
                    ShowMessage("La " + keyName + " del portapapeles está vacía.");
                    return;
                }

                var box = FindDescendant<PasswordBox>(button.Parent as DependencyObject);
                if (box == null)
                {
                    ShowMessage("No se encontró el campo de " + keyName + ".");
                    return;
                }

                box.Password = text;
                ShowMessage(keyNameTitle + " pegada.");
            }
            catch (Exception ex)
            {
                App.Log("Paste stream key failed: " + ex.Message);
                ShowMessage("No se pudo leer el portapapeles: " + ex.Message);
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            return FindDescendant<T>(root, null);
        }

        private static T FindDescendant<T>(DependencyObject root, string name) where T : DependencyObject
        {
            if (root == null) { return null; }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null &&
                    (string.IsNullOrEmpty(name) ||
                     string.Equals((child as FrameworkElement)?.Name, name, StringComparison.Ordinal)))
                {
                    return match;
                }

                var nested = FindDescendant<T>(child, name);
                if (nested != null) { return nested; }
            }

            return null;
        }

        private void ObsHostBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveObs();
        }

        private void ObsPortBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveObs();
        }

        private void ObsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveObs();
        }

        private void XboxModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) { return; }
            _store.SaveXboxModel(GetComboContent(XboxModelBox));
        }

        private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveStreamSettings();
        }

        private void FpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveStreamSettings();
        }

        private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveStreamSettings();
        }

        private void BitrateBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }
            SaveStreamSettings();
        }

        // ------------------------------------------------------------- Guardado

        private void SaveObs()
        {
            _store.SaveObs(
                (ObsHostBox.Text ?? string.Empty).Trim(),
                ParsePort(ObsPortBox.Text),
                ObsPasswordBox.Password);
        }

        private void SaveStreamSettings()
        {
            var settings = new StreamSettings
            {
                Resolution = GetComboContent(ResolutionBox) == "720p" ? "720" : "1080",
                Fps = GetComboContent(FpsBox) == "30" ? 30 : 60,
                Bitrate = ParseInt(BitrateBox.Text, 4500),
                Preset = GetComboContent(PresetBox),
                Audio = true
            };

            _store.SaveStream(settings);
        }

        private void SavePlatform(PlatformViewModel platform)
        {
            _store.SavePlatform(platform.Id, new PlatformSettings
            {
                Enabled = platform.Enabled,
                StreamKey = platform.StreamKey,
                ServerUrl = platform.ServerUrl,
                TargetName = platform.TargetName,
                Mode = platform.Mode == CredentialMode.Account ? "account" : "manual",
                Linked = platform.Linked,
                AccountLabel = platform.AccountLabel
            });
        }

        private void SaveAll()
        {
            if (!_loaded) { return; }

            SaveObs();
            _store.SaveXboxModel(GetComboContent(XboxModelBox));
            SaveStreamSettings();

            foreach (var platform in Platforms)
            {
                SavePlatform(platform);
            }
        }

        // ---------------------------------------------------------------- UI

        private void UpdateUI()
        {
            if (!_loaded) { return; }

            RebuildSelection();

            _initializing = true;
            MasterToggle.IsOn = ToggleTargets.Any() && ToggleTargets.All(p => p.Enabled);
            _initializing = false;

            if (_consoleMode)
            {
                var targets = Platforms
                    .Where(p => !string.Equals(p.Id, "twitch", StringComparison.OrdinalIgnoreCase) &&
                                p.Enabled &&
                                IsPlatformReady(p))
                    .ToList();

                StartButton.IsEnabled = !_isBusy &&
                    targets.Count > 0 &&
                    !string.IsNullOrWhiteSpace(NormalizeTwitchLogin(TwitchChannelBox.Text));

                StopButton.IsEnabled = !_isBusy &&
                    (_relayRunning ||
                     Platforms.Any(p => p.IsLive || p.Status == PlatformStatus.Starting));
            }
            else
            {
                StartButton.IsEnabled = !_isBusy && Platforms.Any(IsPlatformReady);

                StopButton.IsEnabled = !_isBusy &&
                    Platforms.Any(p => p.IsLive || p.Status == PlatformStatus.Starting);
            }

            StartButton.Content = _relayRunning ? "↻  Reiniciar" : "▶  Iniciar";

            UpdateGlobalStatus();
            UpdateStartIndicator();
        }

        private void RebuildSelection()
        {
            var signature = string.Join(",", Destinations.Select(p => p.Enabled ? "1" : "0")) +
                            (_consoleMode ? "|console" : "|obs");
            if (signature == _selectionSignature) { return; }
            _selectionSignature = signature;

            SelectionChips.Clear();

            foreach (var platform in Destinations)
            {
                var brand = platform.BrandBrush.Color;
                var on = platform.Enabled;
                SelectionChips.Add(new SelectionChip
                {
                    Name = platform.Name,
                    Accent = platform.BrandBrush,
                    Background = new SolidColorBrush(on
                        ? Color.FromArgb(255, 10, 10, 12)
                        : Color.FromArgb(255, 20, 20, 23)),
                    Foreground = new SolidColorBrush(on
                        ? Color.FromArgb(255, 244, 244, 246)
                        : Color.FromArgb(255, 120, 120, 130)),
                    Border = new SolidColorBrush(on
                        ? Color.FromArgb(170, brand.R, brand.G, brand.B)
                        : Color.FromArgb(40, 255, 255, 255)),
                    Opacity = on ? 1.0 : 0.6
                });
            }

            var selected = Destinations.Where(p => p.Enabled && p.ToggleEnabled).Select(p => p.Name).ToList();
            if (selected.Count == 0)
            {
                SelectionSummary.Text = _consoleMode
                    ? "Twitch es el origen. Activa al menos un destino con su switch."
                    : "Ninguna plataforma seleccionada. Activa al menos un switch.";
                SelectionSummary.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
            }
            else
            {
                SelectionSummary.Text = (_consoleMode ? "Destinos: " : "Seleccionadas: ") +
                                        string.Join(" · ", selected);
                SelectionSummary.Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 244, 246));
            }
        }

        private static string ShortText(string value, int max)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length <= max) { return text; }
            return text.Substring(0, max - 1) + "…";
        }

        /// <summary>Indicador junto a Iniciar: emisión de Twitch, relay y errores.</summary>
        private void UpdateStartIndicator()
        {
            if (!_loaded || StartStatusText == null) { return; }

            var outputError = _relayStatus?.Outputs == null
                ? null
                : _relayStatus.Outputs
                    .Select(o => o.Error)
                    .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            var relayError = string.IsNullOrWhiteSpace(_relayStatus?.Error)
                ? outputError
                : _relayStatus.Error;

            string text;
            byte r, g, b, br, bg, bb;

            if (!string.IsNullOrWhiteSpace(relayError))
            {
                text = "ERROR RELAY · " + ShortText(relayError, 26);
                r = 255; g = 107; b = 122;
                br = 70; bg = 16; bb = 28;
            }
            else if (_relayRunning)
            {
                // El relay puede estar "running" antes de recibir el primer
                // segmento útil. Solo mostramos verde cuando hay medios fluyendo.
                var flowing = _relayStatus?.Outputs != null &&
                              _relayStatus.Outputs.Any(o => o.BitrateKbps > 1 || o.Fps > 0);
                text = flowing ? "EN EMISIÓN · RELAY OK" : "EN VIVO · SIN MEDIA";
                r = flowing ? (byte)126 : (byte)255;
                g = flowing ? (byte)232 : (byte)176;
                b = flowing ? (byte)160 : (byte)32;
                br = flowing ? (byte)16 : (byte)42;
                bg = flowing ? (byte)42 : (byte)34;
                bb = flowing ? (byte)27 : (byte)16;
            }
            else if (_isBusy)
            {
                text = "INICIANDO…";
                r = 255; g = 176; b = 32;
                br = 42; bg = 34; bb = 16;
            }
            else if (!string.IsNullOrWhiteSpace(_channelCheckError))
            {
                text = "SIN TWITCH · " + ShortText(_channelCheckError, 24);
                r = 255; g = 107; b = 122;
                br = 70; bg = 16; bb = 28;
            }
            else if (_channelChecking)
            {
                text = "COMPROBANDO…";
                r = 255; g = 176; b = 32;
                br = 42; bg = 34; bb = 16;
            }
            else if (_channelInfo != null && _channelInfo.Live)
            {
                text = "EN VIVO · SIN RELAY";
                r = 255; g = 176; b = 32;
                br = 42; bg = 34; bb = 16;
            }
            else if (_channelInfo != null)
            {
                text = "FUERA DE LÍNEA";
                r = 155; g = 155; b = 164;
                br = 30; bg = 30; bb = 34;
            }
            else
            {
                text = "COMPROBANDO…";
                r = 155; g = 155; b = 164;
                br = 30; bg = 30; bb = 34;
            }

            StartStatusText.Text = text;
            var foreground = new SolidColorBrush(Color.FromArgb(255, r, g, b));
            StartStatusText.Foreground = foreground;
            StartStatusDot.Fill = foreground;
            StartStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, br, bg, bb));
        }

        private void UpdateGlobalStatus()
        {
            var live = Platforms.Where(p => p.IsLive).Select(p => p.Name).ToList();

            if (_consoleMode && _relayRunning)
            {
                GlobalStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35));
                GlobalStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Text = live.Count > 0
                    ? "EN VIVO · " + string.Join(" + ", live)
                    : "RELAY EN VIVO";
                return;
            }

            if (_consoleMode && _channelInfo != null && _channelInfo.Live)
            {
                GlobalStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 255, 176, 32));
                GlobalStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Text = "CANAL EN VIVO · PULSA INICIAR";
                return;
            }

            if (live.Count > 0)
            {
                GlobalStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35));
                GlobalStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Text = "EN VIVO · " + string.Join(" + ", live);
            }
            else if (Platforms.Any(p => p.Status == PlatformStatus.Starting))
            {
                GlobalStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 255, 176, 32));
                GlobalStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                GlobalStatusText.Text = "TRANSMITIENDO…";
            }
            else
            {
                GlobalStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 34));
                GlobalStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
                GlobalStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
                GlobalStatusText.Text = "DETENIDO";
            }
        }

        private void SetObsStatus(bool? connected, string text)
        {
            if (connected == true)
            {
                ObsStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35));
                ObsStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
            }
            else if (connected == false)
            {
                ObsStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 42, 12, 17));
                ObsStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
            }
            else
            {
                ObsStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 34));
                ObsStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
            }

            ObsStatusText.Text = text;
        }

        private void ShowMessage(string message)
        {
            FooterStatus.Text = message;
            FooterStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 244, 246));
            _messageTimer.Stop();
            _messageTimer.Start();
        }

        private void MessageTimer_Tick(object sender, object e)
        {
            _messageTimer.Stop();
            FooterStatus.Text = "Listo.";
            FooterStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
        }

        // -------------------------------------------------------------- Helpers

        private static bool IsPlatformReady(PlatformViewModel platform)
        {
            if (platform == null || !platform.Enabled) { return false; }

            if (platform.Mode == CredentialMode.Account)
            {
                return platform.Linked;
            }

            return !string.IsNullOrWhiteSpace(platform.StreamKey) &&
                   !string.IsNullOrWhiteSpace(platform.ServerUrl);
        }

        private static ObsTargetInfo FindTarget(List<ObsTargetInfo> targets, PlatformViewModel platform)
        {
            if (targets == null) { return null; }

            if (!string.IsNullOrWhiteSpace(platform.ObsTargetId))
            {
                var byId = targets.FirstOrDefault(t => t.Id == platform.ObsTargetId);
                if (byId != null) { return byId; }
            }

            var name = (platform.TargetName ?? string.Empty).Trim();
            var exact = targets.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.Name) &&
                string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) { return exact; }

            return targets.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.Name) &&
                t.Name.IndexOf(platform.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatBitrate(double bitsPerSecond)
        {
            if (bitsPerSecond <= 0) { return "—"; }
            var kbps = bitsPerSecond / 1000.0;
            return kbps >= 1000
                ? (kbps / 1000.0).ToString("0.00") + " Mbps"
                : Math.Round(kbps) + " kbps";
        }

        private static int ParsePort(string text)
        {
            if (int.TryParse((text ?? string.Empty).Trim(), out var port) && port >= 1 && port <= 65535)
            {
                return port;
            }

            return 4455;
        }

        private static int ParseInt(string text, int fallback)
        {
            return int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;
        }

        private static string GetComboContent(ComboBox box)
        {
            var item = box?.SelectedItem as ComboBoxItem;
            return item == null ? string.Empty : Convert.ToString(item.Content);
        }

        private static void SelectComboContent(ComboBox box, string value)
        {
            if (box == null) { return; }

            for (var i = 0; i < box.Items.Count; i++)
            {
                var item = box.Items[i] as ComboBoxItem;
                if (item != null &&
                    string.Equals(Convert.ToString(item.Content), value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    return;
                }
            }

            if (box.SelectedIndex < 0 && box.Items.Count > 0)
            {
                box.SelectedIndex = 0;
            }
        }
    }
}
