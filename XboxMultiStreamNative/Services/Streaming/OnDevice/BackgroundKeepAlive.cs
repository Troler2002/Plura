using System;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Mantiene vivo el proceso de la app mientras el relay emite y otra app o
    /// juego está en primer plano. Xbox permite que una app con audio de fondo
    /// siga ejecutándose; el archivo es silencio, así que no se oye nada.
    ///
    /// El silencio se vigila también con un temporizador: si Xbox pausa el
    /// reproductor al cambiar de juego, un cambio de estado no siempre vuelve a
    /// dispararse y la app acababa congelada con el relay "en vivo" en la UI.
    /// </summary>
    internal static class BackgroundKeepAlive
    {
        private const int RearmCooldownSeconds = 2;
        private const int WatchdogSeconds = 5;

        private static readonly object Gate = new object();
        private static MediaPlayer _player;
        private static Timer _watchdog;
        private static bool _started;
        private static DateTime _lastRearmUtc = DateTime.MinValue;
        private static DateTime _lastWatchdogLogUtc = DateTime.MinValue;
        private static int _watchdogBusy;

        public static void Start()
        {
            EnsureWatchdog();

            lock (Gate)
            {
                if (_started && IsPlaying(_player)) { return; }
                if ((DateTime.UtcNow - _lastRearmUtc).TotalSeconds < RearmCooldownSeconds)
                {
                    return;
                }
            }

            Rearm("start");
        }

        /// <summary>
        /// Lo llama el watchdog del relay: aunque la interfaz esté congelada,
        /// vuelve a poner el silencio si el sistema lo pausó.
        /// </summary>
        public static void EnsureRunning()
        {
            EnsureWatchdog();

            lock (Gate)
            {
                if (_started && IsPlaying(_player)) { return; }
            }

            Start();
        }

        private static void EnsureWatchdog()
        {
            lock (Gate)
            {
                if (_watchdog != null) { return; }
                _watchdog = new Timer(
                    state => WatchdogTick(),
                    null,
                    TimeSpan.FromSeconds(WatchdogSeconds),
                    TimeSpan.FromSeconds(WatchdogSeconds));
            }
        }

        private static void WatchdogTick()
        {
            if (Interlocked.Exchange(ref _watchdogBusy, 1) != 0) { return; }
            try
            {
                bool shouldRearm;
                string state;
                lock (Gate)
                {
                    if (!_started)
                    {
                        return;
                    }
                    var session = _player?.PlaybackSession;
                    state = session == null ? "none" : session.PlaybackState.ToString();
                    shouldRearm = session == null ||
                                  session.PlaybackState != MediaPlaybackState.Playing;
                }

                if (shouldRearm)
                {
                    App.Log("Background keep-alive watchdog: silencio en " +
                            state + "; rearmando.");
                    Rearm("watchdog:" + state);
                }
                else if ((DateTime.UtcNow - _lastWatchdogLogUtc).TotalSeconds > 120)
                {
                    _lastWatchdogLogUtc = DateTime.UtcNow;
                    App.Log("Background keep-alive watchdog: silencio en reproducción.");
                }
            }
            catch (Exception ex)
            {
                App.Log("Background keep-alive watchdog failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogBusy, 0);
            }
        }

        private static void Rearm(string reason)
        {
            MediaPlayer previous;
            lock (Gate)
            {
                if ((DateTime.UtcNow - _lastRearmUtc).TotalSeconds < RearmCooldownSeconds)
                {
                    return;
                }
                previous = _player;
                _player = null;
                _started = false;
                _lastRearmUtc = DateTime.UtcNow;
            }

            App.Log("Background keep-alive rearm: " + reason);

            MediaPlayer next;
            try
            {
                next = CreatePlayer();
            }
            catch (Exception ex)
            {
                App.Log("Background media keep-alive failed: " + ex.Message);
                // El reproductor anterior sigue sonando si se pudo crear el nuevo;
                // no lo tiramos cuando la recomposición ha fallado.
                if (previous != null)
                {
                    lock (Gate)
                    {
                        _player = previous;
                        _started = true;
                    }
                }
                return;
            }

            lock (Gate)
            {
                _player = next;
                _started = true;
            }
            // Se sustituye solo después de crear el nuevo: nunca hay un hueco
            // sin sesión de audio que permita a Xbox suspender la app.
            DisposePlayer(previous);
        }

        private static MediaPlayer CreatePlayer()
        {
            var player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                IsLoopingEnabled = true,
                Volume = 1
            };
            player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
            player.MediaFailed += MediaFailed;
            try { player.CommandManager.IsEnabled = true; } catch { }
            player.Source = MediaSource.CreateFromUri(
                new Uri("ms-appx:///Assets/silence.wav"));
            player.Play();
            App.Log("Background media keep-alive started (state=" +
                    player.PlaybackSession.PlaybackState + ")");
            return player;
        }

        private static bool IsPlaying(MediaPlayer player)
        {
            try
            {
                return player != null &&
                       player.PlaybackSession != null &&
                       player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            }
            catch
            {
                return false;
            }
        }

        private static void PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                var state = sender.PlaybackState;
                App.Log("Background keep-alive state " + state + ".");
                if (state != MediaPlaybackState.Playing)
                {
                    Rearm("state:" + state);
                }
            }
            catch (Exception ex)
            {
                App.Log("Background keep-alive state failed: " + ex.Message);
            }
        }

        private static void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            try
            {
                App.Log("Background keep-alive media failed: " + args.Error +
                        (string.IsNullOrWhiteSpace(args.ErrorMessage)
                            ? string.Empty
                            : " · " + args.ErrorMessage));
                Rearm("media-failed");
            }
            catch (Exception ex)
            {
                App.Log("Background keep-alive failure handler failed: " + ex.Message);
            }
        }

        public static void Stop()
        {
            MediaPlayer player;
            lock (Gate)
            {
                player = _player;
                _player = null;
                _started = false;
                _lastRearmUtc = DateTime.MinValue;
            }
            DisposePlayer(player);
        }

        private static void DisposePlayer(MediaPlayer player)
        {
            if (player == null) { return; }
            try
            {
                player.PlaybackSession.PlaybackStateChanged -= PlaybackStateChanged;
                player.MediaFailed -= MediaFailed;
            }
            catch { }
            try
            {
                player.Pause();
                player.Source = null;
                player.Dispose();
                App.Log("Background media keep-alive stopped");
            }
            catch (Exception ex)
            {
                App.Log("Background media keep-alive stop failed: " + ex.Message);
            }
        }
    }
}
