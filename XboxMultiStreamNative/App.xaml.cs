using System;
using System.IO;
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

        private void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log("UnhandledException: " + e.Message + "\r\n" + Describe(e.Exception));
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
