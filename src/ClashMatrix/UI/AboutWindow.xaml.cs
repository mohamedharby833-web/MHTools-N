using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClashMatrix.Services;

namespace ClashMatrix.UI
{
    public partial class AboutWindow : Window
    {
        /// <summary>Where the LinkedIn button goes. Empty hides the button.</summary>
        private const string LinkedInUrl = "https://www.linkedin.com/in/mohamed8harby/";

        /// <param name="host">The running Navisworks, e.g. "Navisworks Manage 2027  (API 24.0, build 1234)".</param>
        public AboutWindow(string host)
        {
            // The look first, so it is there when the XAML is read.
            MhTheme.Apply(this);
            InitializeComponent();

            // As tall as its text, but never taller than the screen: the text scrolls instead.
            try { MaxHeight = SystemParameters.WorkArea.Height; }
            catch (Exception) { }

            Header.Subtitle = "Navisworks add-ins  -  version " + MhTheme.Version;
            BuildText.Text = "Version   " + MhTheme.Version + "   (" + BuildTarget + ")";
            HostText.Text = "Running   " + (string.IsNullOrWhiteSpace(host) ? "Navisworks" : host);
            RuntimeText.Text = "Runtime   " + Runtime;

            LinkedInButton.Visibility = string.IsNullOrWhiteSpace(LinkedInUrl) ? Visibility.Collapsed : Visibility.Visible;
            LogButton.ToolTip = Watchdog.LogPath + " - send this file when a tool misbehaves.";
        }

        private static string BuildTarget
        {
            get
            {
#if NAVIS2024
                return "built for Navisworks 2024";
#elif NAVIS2025
                return "built for Navisworks 2025";
#elif NAVIS2026
                return "built for Navisworks 2026";
#elif NAVIS2027
                return "built for Navisworks 2027";
#else
                return "build target unknown";
#endif
            }
        }

        private static string Runtime
        {
            get
            {
                try { return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription; }
                catch (Exception) { return ".NET Framework " + Environment.Version; }
            }
        }

        private void OnLinkedIn(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LinkedInUrl)) return;
            try
            {
                Process.Start(new ProcessStartInfo(LinkedInUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the browser.\n\n" + LinkedInUrl + "\n\n" + ex.Message,
                    "MHTools-N", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            try
            {
                string log = Watchdog.LogPath;
                if (File.Exists(log))
                    Process.Start("explorer.exe", "/select,\"" + log + "\"");
                else
                    Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(log) + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the log folder.\n\n" + Watchdog.LogPath + "\n\n" + ex.Message,
                    "MHTools-N", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
