using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CastDesktop
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("DispatcherUnhandledException", e.Exception);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("UnhandledException", ex);
            }
        }

        private void LogException(string source, Exception ex)
        {
            Exception actual = ex.InnerException ?? ex;
            string details = $"[{source}] Type: {actual.GetType().FullName}\nMessage: {actual.Message}\nStackTrace:\n{actual.StackTrace}";
            Debug.WriteLine(details);
            try
            {
                File.AppendAllText("app_unhandled_error.log", $"[{DateTime.Now}] {details}\n\n");
            }
            catch { }
        }
    }
}
