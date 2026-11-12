using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;
using Plura.Services;
using Plura.Services.Streaming;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Plura
{
    public sealed partial class MainPage : Page
    {
        private readonly ObsClient _obs = new ObsClient();
        private readonly SettingsStore _store = new SettingsStore();
        private readonly SecureStore _secure = new SecureStore("Plura");
        private readonly PluraBackendClient _backend;
        private readonly LinkCodeAuthService _auth;
        private readonly OAuthCredentialProvider _oauth;
        private readonly CredentialResolver _resolver;
        private readonly ObsMultiRtmpSink _sink;
        private readonly StreamingCoordinator _coordinator;
        private readonly ConsoleRelayClient _relay;
        private readonly CancellationTokenSource _pageCts = new CancellationTokenSource();
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
        private int _channelTicks;
        private RelayStatus _relayStatus;
        private RelayChannelInfo _channelInfo;

        public MainPage()
        {
            try
            {
                InitializeComponent();

                Platforms = new ObservableCollection<PlatformViewModel>();
                SelectionChips = new ObservableCollection<SelectionChip>();

                _backend = new PluraBackendClient(_secure);
                _auth = new LinkCodeAuthService(_backend);
                _oauth = new OAuthCredentialProvider(_backend);
                _resolver = new CredentialResolver(_oauth);
                _sink = new ObsMultiRtmpSink(_obs);
                _coordinator = new StreamingCoordinator(_resolver, _sink);
                _relay = new ConsoleRelayClient(_backend);

                CreatePlatforms();
                DataContext = this;

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _pollTimer.Tick += PollTimer_Tick;

                _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                _messageTimer.Tick += MessageTimer_Tick;

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

        public ObservableCollection<SelectionChip> SelectionChips { get; }

        /// <summary>Plataformas que se pueden activar/desactivar (en modo consola Twitch es el origen).</summary>
        private IEnumerable<PlatformViewModel> ToggleTargets
        {
            get { return Platforms.Where(p => p.ToggleEnabled); }
        }

        private void CreatePlatforms()
        {
            // Servidores de ingesta oficiales. TikTok y Plura no tienen una URL
            // fija: TikTok la da por cuenta/región y Plura es tu propio servidor.
            AddPlatform("twitch", "Twitch", "T", "#9146FF", "rtmp://live.twitch.tv/app/", false);
            AddPlatform("youtube", "YouTube", "Y", "#FF0000", "rtmp://a.rtmp.youtube.com/live2/", false);
            AddPlatform("kick", "Kick", "K", "#53FC18", "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app", true);
            AddPlatform("facebook", "Facebook Gaming", "FB", "#1877F2", "rtmps://live-api-s.facebook.com:443/rtmp/", false);
            AddPlatform("tiktok", "TikTok", "TT", "#FE2C55", string.Empty, false);
            AddPlatform("plura", "Plura", "P", "#E60023", string.Empty, true);
        }

        private void AddPlatform(
            string id, string name, string shortName, string brandHex, string defaultServer, bool darkBadgeInk)
        {
            var platform = new PlatformViewModel(
                id, name, shortName, id, brandHex, defaultServer, darkBadgeInk);

            var saved = _store.LoadPlatform(id);
            platform.Enabled = saved.Enabled;
            platform.StreamKey = saved.StreamKey;
            if (!string.IsNullOrWhiteSpace(saved.ServerUrl)) { platform.ServerUrl = saved.ServerUrl; }
            if (!string.IsNullOrWhiteSpace(saved.TargetName)) { platform.TargetName = saved.TargetName; }

            platform.Mode = string.Equals(saved.Mode, "account", StringComparison.OrdinalIgnoreCase)
                ? CredentialMode.Account
                : CredentialMode.Manual;
            platform.Linked = saved.Linked;
            platform.AccountLabel = saved.AccountLabel;
            platform.IsConfigOpen =
                !platform.Linked && string.IsNullOrWhiteSpace(platform.StreamKey);

            Platforms.Add(platform);
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                LoadSettings();
            }

            UpdateUI();
            _pollTimer.Start();

            if (_consoleMode)
            {
                var ignoredStatus = RefreshRelayAsync(false);
                var ignoredChannel = CheckChannelAsync(false);
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

            var backendUrl = _store.LoadBackendUrl();
            BackendUrlBox.Text = backendUrl;
            _backend.BaseUrl = backendUrl;

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

                    if (++_channelTicks >= 5)
                    {
                        _channelTicks = 0;
                        await CheckChannelAsync(false);
                    }
                }
                finally
                {
                    _polling = false;
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
                ShowMessage("Configura las plataformas activas en modo Manual (URL + clave) o vincula su cuenta.");
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
                ShowMessage(platform.Name + ": configura el modo Manual o vincula tu cuenta.");
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
            // con su función nativa y el backend reparte la señal al resto.
            ObsCard.Visibility = Visibility.Collapsed;
            XboxCard.Visibility = Visibility.Collapsed;
            StreamCard.Visibility = Visibility.Collapsed;
            ConsoleCard.Visibility = Visibility.Visible;
            StartButton.Content = "▶  Iniciar relay";
        }

        private async Task StartConsoleRelayAsync()
        {
            if (_isBusy) { return; }

            if (!ApplyBackendUrl()) { return; }

            var channel = NormalizeTwitchLogin(TwitchChannelBox.Text);
            if (channel.Length == 0)
            {
                ShowMessage("Escribe tu canal de Twitch o pulsa Usar mi cuenta.");
                return;
            }

            var selected = Platforms
                .Where(p => p.Enabled &&
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
                            StreamKey = resolved.Endpoint.StreamKey
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
                    ShowMessage("No hay destinos listos. Configura URL + clave o vincula cada cuenta.");
                    return;
                }

                ShowMessage("Iniciando relay desde Twitch…");
                var status = await _relay.StartAsync(channel, targets, _pageCts.Token);
                ApplyRelayStatus(status);

                ShowMessage("Relay activo: Twitch → " +
                    string.Join(" + ", targets.Select(t => t.Platform)) + ".");

                if (incomplete.Count > 0)
                {
                    ShowMessage("Sin configurar: " + string.Join(", ", incomplete) + ".");
                }

                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                ShowMessage("Relay: " + ex.Message);
                App.Log("Relay start failed: " + ex);
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
                var status = await _relay.StopAsync(_pageCts.Token);
                ApplyRelayStatus(status);
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

        private async Task RefreshRelayAsync(bool notify)
        {
            if (!_consoleMode) { return; }

            try
            {
                var status = await _relay.GetStatusAsync(_pageCts.Token);
                ApplyRelayStatus(status);

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
                RelayStatusText.Text = "SIN BACKEND";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 42, 12, 17));
                if (notify) { ShowMessage("Relay: " + ex.Message); }
            }
        }

        private void ApplyRelayStatus(RelayStatus status)
        {
            _relayStatus = status;
            var running = status != null && status.Running;
            var wasRunning = _relayRunning;
            _relayRunning = running;

            if (running)
            {
                RelayStatusText.Text = "RELAY EN VIVO";
                RelayStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 5, 5, 6));
                RelayStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35));
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
                    if (string.Equals(output.State, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        platform.IsLive = false;
                        platform.SetStatus(PlatformStatus.Error);
                        platform.ResetStats();
                        continue;
                    }

                    var sending = string.Equals(output.State, "running", StringComparison.OrdinalIgnoreCase);
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
                        .Select(o => o.Platform + ": " + o.State)
                        .ToList();
                    RelayDetailText.Text = "Canal " + status.Channel +
                        (parts.Count > 0 ? " · " + string.Join(" · ", parts) : string.Empty) +
                        (status.Error.Length > 0 ? " · " + status.Error : string.Empty);
                }
                else if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    RelayDetailText.Text = status.Error;
                }
            }

            UpdateUI();
        }

        private async Task CheckChannelAsync(bool notify)
        {
            if (!_consoleMode) { return; }

            var login = NormalizeTwitchLogin(TwitchChannelBox.Text);
            if (login.Length == 0)
            {
                _channelInfo = null;
                ChannelStatusText.Text = "Escribe tu canal o vincula tu cuenta de Twitch.";
                ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));
                return;
            }

            try
            {
                var info = await _relay.CheckChannelAsync(login, _pageCts.Token);
                _channelInfo = info;

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

                if (!string.IsNullOrWhiteSpace(info.Title))
                {
                    RelayDetailText.Text = info.Title;
                }

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
                ChannelStatusText.Text = "No se pudo comprobar el canal.";
                ChannelStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                if (notify) { ShowMessage("Twitch: " + ex.Message); }
            }
            finally
            {
                UpdateUI();
            }
        }

        private async void UseTwitchAccount_Click(object sender, RoutedEventArgs e)
        {
            var twitch = Platforms.FirstOrDefault(
                p => string.Equals(p.Id, "twitch", StringComparison.OrdinalIgnoreCase));
            if (twitch == null) { return; }

            if (!ApplyBackendUrl()) { return; }

            UseTwitchAccountButton.IsEnabled = false;
            try
            {
                if (!twitch.Linked)
                {
                    await LinkAccountAsync(twitch);
                }

                if (!twitch.Linked) { return; }

                try
                {
                    var info = await _relay.GetLinkedChannelAsync(_pageCts.Token);
                    if (!string.IsNullOrWhiteSpace(info.Login))
                    {
                        TwitchChannelBox.Text = info.Login;
                        _store.SaveTwitchChannel(info.Login);
                        ShowMessage("Canal detectado: " + info.Login);
                        await CheckChannelAsync(true);
                    }
                }
                catch (Exception ex)
                {
                    ShowMessage("No se pudo detectar el canal: " + ex.Message +
                                " Escríbelo manualmente.");
                }
            }
            finally
            {
                UseTwitchAccountButton.IsEnabled = true;
            }
        }

        private async void CheckChannel_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyBackendUrl()) { return; }
            await CheckChannelAsync(true);
        }

        private void TwitchChannelBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }
            _store.SaveTwitchChannel(NormalizeTwitchLogin(TwitchChannelBox.Text));
        }

        private static string NormalizeTwitchLogin(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) { return string.Empty; }

            var marker = text.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                text = text.Substring(marker + "twitch.tv/".Length);
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

        private void ToggleConfig_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null) { return; }
            platform.IsConfigOpen = !platform.IsConfigOpen;
        }

        // ------------------------------------------------- Credenciales / cuentas

        private void ModeAccount_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            platform.Mode = CredentialMode.Account;
            SavePlatform(platform);
            UpdateUI();

            if (!platform.Linked)
            {
                ShowMessage(platform.Name + ": pulsa Conectar cuenta para vincularla.");
            }
        }

        private void ModeManual_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            platform.Mode = CredentialMode.Manual;
            SavePlatform(platform);
            UpdateUI();
        }

        private async void AccountLink_Click(object sender, RoutedEventArgs e)
        {
            var platform = (sender as FrameworkElement)?.DataContext as PlatformViewModel;
            if (platform == null || platform.IsLinking) { return; }

            if (platform.Linked)
            {
                await UnlinkAccountAsync(platform);
                return;
            }

            await LinkAccountAsync(platform);
        }

        private async Task LinkAccountAsync(PlatformViewModel platform)
        {
            if (!ApplyBackendUrl()) { return; }

            platform.IsLinking = true;
            platform.ResetLink();
            platform.LinkStatus = "Iniciando vinculación…";
            UpdateUI();

            try
            {
                var challenge = await _auth.StartLinkAsync(
                    platform.Id, platform.Name, _pageCts.Token);

                platform.LinkCode = challenge.Code;
                platform.LinkUrl = challenge.VerificationUrl;
                platform.LinkStatus = "Esperando confirmación…";

                ShowMessage(platform.Name + ": abre " + challenge.VerificationUrl +
                            " y escribe el código " + challenge.Code);

                while (!_pageCts.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow > challenge.ExpiresAt)
                    {
                        platform.LinkStatus = "El código caducó.";
                        ShowMessage(platform.Name + ": el código caducó. Vuelve a intentarlo.");
                        return;
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(challenge.IntervalSeconds), _pageCts.Token);

                    var poll = await _auth.PollLinkAsync(challenge, _pageCts.Token);

                    if (poll.State == LinkPollState.Linked)
                    {
                        platform.Linked = true;
                        platform.AccountLabel = poll.AccountLabel ?? string.Empty;
                        platform.ResetLink();
                        platform.LinkStatus = "Cuenta vinculada.";
                        SavePlatform(platform);
                        UpdateUI();

                        ShowMessage(platform.Name + ": cuenta vinculada" +
                            (string.IsNullOrWhiteSpace(platform.AccountLabel)
                                ? "."
                                : " · " + platform.AccountLabel));
                        return;
                    }

                    if (poll.State == LinkPollState.Expired || poll.State == LinkPollState.Error)
                    {
                        platform.LinkStatus = poll.Error;
                        ShowMessage(platform.Name + ": " + poll.Error);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                platform.LinkStatus = ex.Message;
                ShowMessage(platform.Name + ": " + ex.Message);
            }
            finally
            {
                platform.IsLinking = false;
                UpdateUI();
            }
        }

        private async Task UnlinkAccountAsync(PlatformViewModel platform)
        {
            if (!ApplyBackendUrl()) { return; }

            platform.IsLinking = true;
            UpdateUI();

            try
            {
                await _auth.UnlinkAsync(platform.Id, _pageCts.Token);
                platform.Linked = false;
                platform.AccountLabel = string.Empty;
                platform.ResetLink();
                platform.LinkStatus = "Cuenta desvinculada.";
                SavePlatform(platform);
                ShowMessage(platform.Name + ": cuenta desvinculada.");
            }
            catch (Exception ex)
            {
                // Aunque el backend no responda, la app deja de usar la cuenta.
                platform.Linked = false;
                platform.AccountLabel = string.Empty;
                platform.ResetLink();
                platform.LinkStatus = "Desvinculada en esta consola.";
                SavePlatform(platform);
                ShowMessage(platform.Name + ": desvinculada en esta consola. " + ex.Message);
            }
            finally
            {
                platform.IsLinking = false;
                UpdateUI();
            }
        }

        private bool ApplyBackendUrl()
        {
            var url = (BackendUrlBox.Text ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                ShowMessage("Escribe la URL del backend de Plura, por ejemplo http://192.168.1.57:8787.");
                return false;
            }

            _store.SaveBackendUrl(url);
            _backend.BaseUrl = url;
            return true;
        }

        private void BackendUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) { return; }
            _store.SaveBackendUrl(BackendUrlBox.Text);
            _backend.BaseUrl = BackendUrlBox.Text;
        }

        private async void TestBackend_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyBackendUrl()) { return; }

            TestBackendButton.IsEnabled = false;
            BackendStatusText.Text = "Conectando…";
            BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 164));

            try
            {
                var data = await _backend.GetAsync("/v1/health", _pageCts.Token);
                var name = PluraBackendClient.GetString(data, "name", "Plura backend");
                var version = PluraBackendClient.GetString(data, "version", string.Empty);

                BackendStatusText.Text = "Conectado · " + name +
                    (string.IsNullOrWhiteSpace(version) ? string.Empty : " " + version);
                BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 126, 232, 160));
            }
            catch (Exception ex)
            {
                BackendStatusText.Text = ex.Message;
                BackendStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 107, 122));
                ShowMessage("Backend: " + ex.Message);
            }
            finally
            {
                TestBackendButton.IsEnabled = true;
            }
        }

        private void PlatformServerBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var box = sender as TextBox;
            var platform = box?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            platform.ServerUrl = box.Text;
            SavePlatform(platform);
        }

        private void PlatformTargetBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var box = sender as TextBox;
            var platform = box?.DataContext as PlatformViewModel;
            if (platform == null) { return; }

            platform.TargetName = box.Text;
            SavePlatform(platform);
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

            UpdateGlobalStatus();
        }

        private void RebuildSelection()
        {
            var signature = string.Join(",", Platforms.Select(p => p.Enabled ? "1" : "0")) +
                            (_consoleMode ? "|console" : "|obs");
            if (signature == _selectionSignature) { return; }
            _selectionSignature = signature;

            SelectionChips.Clear();

            foreach (var platform in Platforms)
            {
                if (_consoleMode && platform.IsOrigin)
                {
                    SelectionChips.Add(new SelectionChip
                    {
                        Name = platform.Name + " (origen)",
                        Accent = platform.BrandBrush,
                        Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 12)),
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 244, 246)),
                        Border = new SolidColorBrush(Color.FromArgb(255, 230, 0, 35)),
                        Opacity = 1.0
                    });
                    continue;
                }

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
                        ? Color.FromArgb(150, 230, 0, 35)
                        : Color.FromArgb(40, 255, 255, 255)),
                    Opacity = on ? 1.0 : 0.6
                });
            }

            var selected = Platforms.Where(p => p.Enabled && p.ToggleEnabled).Select(p => p.Name).ToList();
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
