using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Plura
{
    public sealed partial class App : Application
    {
        private static readonly object LogLock = new object();

        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
            this.Suspending += App_Suspending;
            this.Resuming += App_Resuming;
            this.EnteredBackground += App_EnteredBackground;
            this.LeavingBackground += App_LeavingBackground;
            Log("App started");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                Frame rootFrame = Window.Current.Content as Frame;

                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    Window.Current.Content = rootFrame;
                }

                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }

                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                Log("OnLaunched EXCEPTION: " + Describe(ex));
                throw;
            }
        }

        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            Log("App suspending");
            try
            {
                var frame = Window.Current.Content as Frame;
                var page = frame?.Content as MainPage;
                page?.PrepareForSuspension();
            }
            catch (Exception ex)
            {
                Log("Prepare for suspension failed: " + Describe(ex));
            }
            try { deferral.Complete(); } catch { }
        }

        private void App_Resuming(object sender, object e)
        {
            Log("App resuming");
            ResumeRelayAfterBackground();
        }

        private void App_LeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            Log("App leaving background");
            ResumeRelayAfterBackground();
        }

        private void App_EnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            Log("App entered background");
            // No se pide otra sesión de ejecución aquí: liberar la actual justo
            // al pasar a segundo plano provoca que Xbox la deniegue. Sí se
            // garantiza que el audio silencioso siga sonando, que es lo que
            // mantiene vivo el proceso en Xbox.
            try
            {
                var frame = Window.Current.Content as Frame;
                var page = frame?.Content as MainPage;
                page?.KeepAliveInBackground();
            }
            catch (Exception ex)
            {
                Log("Keep alive on background failed: " + Describe(ex));
            }
        }

        private static void ResumeRelayAfterBackground()
        {
            try
            {
                var frame = Window.Current.Content as Frame;
                var page = frame?.Content as MainPage;
                if (page == null) { return; }
                page.ResumeAfterBackground();
            }
            catch (Exception ex)
            {
                Log("Resume relay failed: " + Describe(ex));
            }
        }

        private void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log("UnhandledException: " + e.Message + "\r\n" + Describe(e.Exception));
            // Evita que una excepción aislada en un timer o en un handler async
            // void cierre la app y corte el relay en segundo plano.
            e.Handled = true;
        }

        private static string Describe(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            while (ex != null)
            {
                sb.Append(ex.GetType().FullName)
                  .Append(" HResult=0x").Append(ex.HResult.ToString("X8"))
                  .Append(": ").Append(ex.Message)
                  .Append("\r\n").Append(ex.StackTrace).Append("\r\n");
                ex = ex.InnerException;
            }
            return sb.ToString();
        }

        internal static void Log(string message)
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "plura.log");
                var line = DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n";
                lock (LogLock)
                {
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // logging must never crash the app
            }
        }
    }
}
