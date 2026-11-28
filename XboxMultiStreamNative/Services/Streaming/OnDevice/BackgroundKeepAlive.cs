using System;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Plura.Services.Streaming.OnDevice
{
    /// <summary>
    /// Mantiene vivo el proceso de la app mientras el relay emite y otra app o
    /// juego está en primer plano. Xbox permite que una app con audio de fondo
    /// siga ejecutándose; el archivo es silencio, así que no se oye nada.
    /// </summary>
    internal static class BackgroundKeepAlive
    {
        private static readonly object Gate = new object();
        private static MediaPlayer _player;
        private static bool _started;
        private static DateTime _lastRestartUtc = DateTime.MinValue;

        public static void Start()
        {
            MediaPlayer previous;
            lock (Gate)
            {
                if (_started && IsPlaying(_player)) { return; }
                if (_player != null &&
                    (DateTime.UtcNow - _lastRestartUtc).TotalSeconds < 5)
                {
                    return;
                }
                previous = _player;
                _player = null;
                _started = true;
            }
            DisposePlayer(previous);

            try
            {
                // La categoría Media + SMTC activo es la que Xbox reconoce como
                // reproducción en segundo plano. El archivo es silencio real,
                // así que el volumen puede ir a 1 sin que se oiga nada.
                var player = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.Media,
                    IsLoopingEnabled = true,
                    Volume = 1
                };
                try { player.CommandManager.IsEnabled = true; } catch { }
                player.Source = MediaSource.CreateFromUri(
                    new Uri("ms-appx:///Assets/silence.wav"));
                player.Play();

                lock (Gate)
                {
                    _player = player;
                    _started = true;
                    _lastRestartUtc = DateTime.UtcNow;
                }

                player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
                player.MediaFailed += MediaFailed;
                App.Log("Background media keep-alive started");
            }
            catch (Exception ex)
            {
                lock (Gate) { _started = false; }
                App.Log("Background media keep-alive failed: " + ex.Message);
            }
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
                if (sender.PlaybackState == MediaPlaybackState.Playing) { return; }
                App.Log("Background keep-alive state " + sender.PlaybackState +
                        "; reiniciando silencio.");
                Restart();
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
                Restart();
            }
            catch (Exception ex)
            {
                App.Log("Background keep-alive failure handler failed: " + ex.Message);
            }
        }

        private static void Restart()
        {
            MediaPlayer player;
            lock (Gate)
            {
                if ((DateTime.UtcNow - _lastRestartUtc).TotalSeconds < 5) { return; }
                player = _player;
                _player = null;
                _started = false;
            }
            DisposePlayer(player);
            Start();
        }

        public static void Stop()
        {
            MediaPlayer player;
            lock (Gate)
            {
                player = _player;
                _player = null;
                _started = false;
                _lastRestartUtc = DateTime.MinValue;
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
