using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Plura.Services.Streaming;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Plura.Models
{
    public enum PlatformStatus
    {
        Idle,
        Starting,
        Live,
        Error
    }

    public sealed class PlatformViewModel : INotifyPropertyChanged
    {
        private static readonly Color LiveBackground = Color.FromArgb(255, 230, 0, 35);
        private static readonly Color LiveForeground = Color.FromArgb(255, 5, 5, 6);
        private static readonly Color IdleBackground = Color.FromArgb(255, 30, 30, 34);
        private static readonly Color IdleForeground = Color.FromArgb(255, 155, 155, 164);
        private static readonly Color StartingBackground = Color.FromArgb(255, 255, 176, 32);
        private static readonly Color StartingForeground = Color.FromArgb(255, 5, 5, 6);
        private static readonly Color ErrorBackground = Color.FromArgb(255, 60, 12, 18);
        private static readonly Color ErrorForeground = Color.FromArgb(255, 255, 107, 122);
        private static readonly Color TabActiveBackground = Color.FromArgb(255, 230, 0, 35);
        private static readonly Color TabActiveForeground = Color.FromArgb(255, 5, 5, 6);
        private static readonly Color TabInactiveBackground = Color.FromArgb(255, 26, 26, 31);
        private static readonly Color TabInactiveForeground = Color.FromArgb(255, 155, 155, 164);
        private static readonly Color LinkedBackground = Color.FromArgb(255, 18, 54, 32);
        private static readonly Color LinkedForeground = Color.FromArgb(255, 126, 232, 160);
        private static readonly Color UnlinkedBackground = Color.FromArgb(255, 30, 30, 34);
        private static readonly Color UnlinkedForeground = Color.FromArgb(255, 155, 155, 164);

        private bool _enabled;
        private string _streamKey = string.Empty;
        private string _serverUrl;
        private string _targetName;
        private CredentialMode _mode = CredentialMode.Manual;
        private bool _linked;
        private string _accountLabel = string.Empty;
        private bool _isLinking;
        private string _linkStatus = string.Empty;
        private string _linkCode = string.Empty;
        private string _linkUrl = string.Empty;
        private string _statusText = "LISTA";
        private PlatformStatus _status = PlatformStatus.Idle;
        private bool _isLive;
        private bool _isBusy;
        private bool _isConfigOpen;
        private string _bitrateText = "—";
        private string _fpsText = "—";
        private string _durationText = "—";
        private string _obsTargetId = string.Empty;
        private bool _consoleMode;

        public PlatformViewModel(
            string id,
            string name,
            string shortName,
            string keyword,
            string brandHex,
            string defaultServerUrl,
            bool darkBadgeInk)
        {
            Id = id;
            Name = name;
            ShortName = shortName;
            Keyword = keyword;
            DefaultServerUrl = defaultServerUrl;
            BrandBrush = BrushFromHex(brandHex);
            BadgeInkBrush = new SolidColorBrush(darkBadgeInk ? Colors.Black : Colors.White);
            _serverUrl = defaultServerUrl;
            _targetName = name;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Id { get; }

        public string Name { get; }

        public string ShortName { get; }

        public string Keyword { get; }

        public string DefaultServerUrl { get; }

        public SolidColorBrush BrandBrush { get; }

        public SolidColorBrush BadgeInkBrush { get; }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (Set(ref _enabled, value))
                {
                    Raise(nameof(CanStart));
                    Raise(nameof(StatusBackground));
                    Raise(nameof(StatusForeground));
                    Raise(nameof(AccentThickness));
                }
            }
        }

        public string StreamKey
        {
            get { return _streamKey; }
            set
            {
                if (Set(ref _streamKey, value ?? string.Empty))
                {
                    Raise(nameof(CanStart));
                }
            }
        }

        public string ServerUrl
        {
            get { return _serverUrl; }
            set
            {
                if (Set(ref _serverUrl, value))
                {
                    Raise(nameof(CanStart));
                }
            }
        }

        public string TargetName
        {
            get { return _targetName; }
            set { Set(ref _targetName, value); }
        }

        /// <summary>Manual = URL + clave. Account = credenciales del backend.</summary>
        public CredentialMode Mode
        {
            get { return _mode; }
            set
            {
                if (Set(ref _mode, value))
                {
                    Raise(nameof(IsManualMode));
                    Raise(nameof(IsAccountMode));
                    Raise(nameof(ManualVisibility));
                    Raise(nameof(AccountVisibility));
                    Raise(nameof(AccountTabBackground));
                    Raise(nameof(AccountTabForeground));
                    Raise(nameof(ManualTabBackground));
                    Raise(nameof(ManualTabForeground));
                    Raise(nameof(CanStart));
                }
            }
        }

        public bool IsManualMode
        {
            get { return _mode == CredentialMode.Manual; }
        }

        public bool IsAccountMode
        {
            get { return _mode == CredentialMode.Account; }
        }

        public bool Linked
        {
            get { return _linked; }
            set
            {
                if (Set(ref _linked, value))
                {
                    Raise(nameof(AccountStatusText));
                    Raise(nameof(AccountStatusBackground));
                    Raise(nameof(AccountStatusForeground));
                    Raise(nameof(AccountButtonText));
                    Raise(nameof(CanStart));
                }
            }
        }

        public string AccountLabel
        {
            get { return _accountLabel; }
            set
            {
                if (Set(ref _accountLabel, value ?? string.Empty))
                {
                    Raise(nameof(AccountStatusText));
                }
            }
        }

        public bool IsLinking
        {
            get { return _isLinking; }
            set
            {
                if (Set(ref _isLinking, value))
                {
                    Raise(nameof(CanManageAccount));
                    Raise(nameof(LinkProgressVisibility));
                }
            }
        }

        public string LinkStatus
        {
            get { return _linkStatus; }
            set { Set(ref _linkStatus, value ?? string.Empty); }
        }

        public string LinkCode
        {
            get { return _linkCode; }
            set
            {
                if (Set(ref _linkCode, value ?? string.Empty))
                {
                    Raise(nameof(LinkCodeText));
                    Raise(nameof(LinkCodeVisibility));
                }
            }
        }

        public string LinkUrl
        {
            get { return _linkUrl; }
            set
            {
                if (Set(ref _linkUrl, value ?? string.Empty))
                {
                    Raise(nameof(LinkUrlText));
                    Raise(nameof(LinkCodeVisibility));
                }
            }
        }

        public PlatformStatus Status
        {
            get { return _status; }
        }

        public string StatusText
        {
            get { return IsOrigin ? "ORIGEN" : _statusText; }
        }

        public bool IsLive
        {
            get { return _isLive; }
            set
            {
                if (Set(ref _isLive, value))
                {
                    Raise(nameof(LiveVisibility));
                    Raise(nameof(CanStop));
                }
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(CanStart));
                    Raise(nameof(CanStop));
                }
            }
        }

        public bool IsConfigOpen
        {
            get { return _isConfigOpen; }
            set
            {
                if (Set(ref _isConfigOpen, value))
                {
                    Raise(nameof(ConfigVisibility));
                    Raise(nameof(SettingsVisibility));
                }
            }
        }

        public string BitrateText
        {
            get { return _bitrateText; }
            set { Set(ref _bitrateText, value); }
        }

        public string FpsText
        {
            get { return _fpsText; }
            set { Set(ref _fpsText, value); }
        }

        public string DurationText
        {
            get { return _durationText; }
            set { Set(ref _durationText, value); }
        }

        public string ObsTargetId
        {
            get { return _obsTargetId; }
            set { Set(ref _obsTargetId, value); }
        }

        public bool CanStart
        {
            get
            {
                if (!_enabled || _isBusy) { return false; }

                if (_mode == CredentialMode.Account)
                {
                    return _linked;
                }

                return !string.IsNullOrWhiteSpace(_streamKey) &&
                       !string.IsNullOrWhiteSpace(_serverUrl);
            }
        }

        public bool CanStop
        {
            get { return _isBusy || _isLive || _status == PlatformStatus.Starting; }
        }

        public bool CanManageAccount
        {
            get { return !_isLinking; }
        }

        public string AccountStatusText
        {
            get
            {
                if (!_linked) { return "SIN CUENTA VINCULADA"; }
                return string.IsNullOrWhiteSpace(_accountLabel)
                    ? "CUENTA VINCULADA"
                    : "CUENTA VINCULADA · " + _accountLabel;
            }
        }

        public string AccountButtonText
        {
            get { return _linked ? "Desvincular" : "Conectar cuenta"; }
        }

        public string LinkCodeText
        {
            get
            {
                return string.IsNullOrWhiteSpace(_linkCode)
                    ? string.Empty
                    : "CÓDIGO: " + _linkCode;
            }
        }

        public string LinkUrlText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_linkUrl)) { return string.Empty; }
                return "Abre " + _linkUrl + " en tu teléfono o PC y escribe el código.";
            }
        }

        public Visibility LinkCodeVisibility
        {
            get
            {
                return string.IsNullOrWhiteSpace(_linkCode) && string.IsNullOrWhiteSpace(_linkUrl)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility LinkProgressVisibility
        {
            get { return _isLinking ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ManualVisibility
        {
            get { return _mode == CredentialMode.Manual ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AccountVisibility
        {
            get { return _mode == CredentialMode.Account ? Visibility.Visible : Visibility.Collapsed; }
        }

        public SolidColorBrush AccountTabBackground
        {
            get
            {
                return new SolidColorBrush(_mode == CredentialMode.Account
                    ? TabActiveBackground
                    : TabInactiveBackground);
            }
        }

        public SolidColorBrush AccountTabForeground
        {
            get
            {
                return new SolidColorBrush(_mode == CredentialMode.Account
                    ? TabActiveForeground
                    : TabInactiveForeground);
            }
        }

        public SolidColorBrush ManualTabBackground
        {
            get
            {
                return new SolidColorBrush(_mode == CredentialMode.Manual
                    ? TabActiveBackground
                    : TabInactiveBackground);
            }
        }

        public SolidColorBrush ManualTabForeground
        {
            get
            {
                return new SolidColorBrush(_mode == CredentialMode.Manual
                    ? TabActiveForeground
                    : TabInactiveForeground);
            }
        }

        public SolidColorBrush AccountStatusBackground
        {
            get
            {
                return new SolidColorBrush(_linked ? LinkedBackground : UnlinkedBackground);
            }
        }

        public SolidColorBrush AccountStatusForeground
        {
            get
            {
                return new SolidColorBrush(_linked ? LinkedForeground : UnlinkedForeground);
            }
        }

        public Visibility LiveVisibility
        {
            get { return _isLive ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ConfigVisibility
        {
            get { return _isConfigOpen ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>La clave se muestra siempre en los destinos; Twitch es el origen.</summary>
        public Visibility SettingsVisibility
        {
            get { return IsOrigin ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ConfigButtonVisibility
        {
            get { return Visibility.Collapsed; }
        }

        /// <summary>Pista concreta de dónde sacar la Stream Key de cada red.</summary>
        public string StreamKeyHint
        {
            get
            {
                switch ((Id ?? string.Empty).ToLowerInvariant())
                {
                    case "youtube":
                        return "Pega solo la clave · YouTube Studio → Transmitir → Clave de transmisión.";
                    case "kick":
                        return "Pega solo la clave · Kick → Configuración → Stream Key.";
                    case "facebook":
                        return "Pega solo la clave · Facebook Live Producer → Clave de transmisión.";
                    default:
                        return "Pega solo la clave desde el panel de la plataforma.";
                }
            }
        }

        // --------------------------------------------------- modo consola

        /// <summary>En modo consola Twitch es el origen, no un destino.</summary>
        public bool IsOrigin
        {
            get
            {
                return _consoleMode &&
                       string.Equals(Id, "twitch", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool ToggleEnabled
        {
            get { return !IsOrigin; }
        }

        public Visibility ToggleVisibility
        {
            get { return IsOrigin ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ActionsVisibility
        {
            get { return _consoleMode ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility OriginVisibility
        {
            get { return IsOrigin ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ObsOnlyVisibility
        {
            get { return _consoleMode ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string StatusLabel
        {
            get { return _consoleMode ? "ESTADO RELAY" : "ESTADO OBS"; }
        }

        public void SetConsoleMode(bool consoleMode)
        {
            if (_consoleMode == consoleMode) { return; }

            _consoleMode = consoleMode;
            Raise(nameof(IsOrigin));
            Raise(nameof(ToggleEnabled));
            Raise(nameof(ToggleVisibility));
            Raise(nameof(ActionsVisibility));
            Raise(nameof(OriginVisibility));
            Raise(nameof(ObsOnlyVisibility));
            Raise(nameof(SettingsVisibility));
            Raise(nameof(ConfigButtonVisibility));
            Raise(nameof(StatusLabel));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBackground));
            Raise(nameof(StatusForeground));
        }

        public Thickness AccentThickness
        {
            get { return _enabled ? new Thickness(4, 0, 0, 0) : new Thickness(0); }
        }

        public void ResetStats()
        {
            BitrateText = "—";
            FpsText = "—";
            DurationText = "—";
        }

        public void ResetLink()
        {
            LinkStatus = string.Empty;
            LinkCode = string.Empty;
            LinkUrl = string.Empty;
        }

        public SolidColorBrush StatusBackground
        {
            get
            {
                if (IsOrigin) { return new SolidColorBrush(Color.FromArgb(255, 230, 0, 35)); }
                if (_status == PlatformStatus.Live) { return new SolidColorBrush(LiveBackground); }
                if (_status == PlatformStatus.Starting) { return new SolidColorBrush(StartingBackground); }
                if (_status == PlatformStatus.Error) { return new SolidColorBrush(ErrorBackground); }
                return new SolidColorBrush(IdleBackground);
            }
        }

        public SolidColorBrush StatusForeground
        {
            get
            {
                if (IsOrigin) { return new SolidColorBrush(Color.FromArgb(255, 5, 5, 6)); }
                if (_status == PlatformStatus.Live) { return new SolidColorBrush(LiveForeground); }
                if (_status == PlatformStatus.Starting) { return new SolidColorBrush(StartingForeground); }
                if (_status == PlatformStatus.Error) { return new SolidColorBrush(ErrorForeground); }
                return new SolidColorBrush(IdleForeground);
            }
        }

        public void SetStatus(PlatformStatus status)
        {
            _status = status;
            switch (status)
            {
                case PlatformStatus.Live:
                    _statusText = "EN VIVO";
                    break;
                case PlatformStatus.Starting:
                    _statusText = "CONECTANDO";
                    break;
                case PlatformStatus.Error:
                    _statusText = "ERROR";
                    break;
                default:
                    _statusText = "LISTA";
                    break;
            }

            Raise(nameof(StatusText));
            Raise(nameof(StatusBackground));
            Raise(nameof(StatusForeground));
            Raise(nameof(CanStart));
            Raise(nameof(CanStop));
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) { return new SolidColorBrush(Colors.Gray); }
            var value = hex.TrimStart('#');
            if (value.Length == 6)
            {
                return new SolidColorBrush(Color.FromArgb(
                    255,
                    Convert.ToByte(value.Substring(0, 2), 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)));
            }
            return new SolidColorBrush(Colors.Gray);
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) { return false; }
            field = value;
            Raise(propertyName);
            return true;
        }

        private void Raise(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) { return; }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
