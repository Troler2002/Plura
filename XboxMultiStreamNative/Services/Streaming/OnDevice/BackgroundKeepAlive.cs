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

        public static void Start()
        {
            lock (Gate)
            {
                if (_started) { return; }
                _started = true;
            }

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

                lock (Gate) { _player = player; }
                App.Log("Background media keep-alive started");
            }
            catch (Exception ex)
            {
                lock (Gate) { _started = false; }
                App.Log("Background media keep-alive failed: " + ex.Message);
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
            }

            if (player == null) { return; }
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
