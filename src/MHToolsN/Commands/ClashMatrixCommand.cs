using System;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Navisworks.Api;
using NavisApp = Autodesk.Navisworks.Api.Application;
using MHToolsN.UI;

namespace MHToolsN.Commands
{
    /// <summary>The ribbon's commands. They run on Navisworks' UI thread.</summary>
    public static class ClashMatrixCommand
    {
        /// <summary>Clash Matrix: the window copes with an empty document (import / export / XML still work then).</summary>
        public static void Run(string[] parameters)
        {
            ShowOwned(new ClashMatrixWindow(NavisApp.ActiveDocument));
        }

        public static void ExportResults(string[] parameters)
        {
            Document document = NavisApp.ActiveDocument;
            if (document == null || document.IsClear)
            {
                MessageBox.Show("Open a model with clash tests first.", "Export Clash Results",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowOwned(new ExportResultsWindow(document));
        }

        public static void About(string[] parameters)
        {
            ShowOwned(new AboutWindow(HostDescription()));
        }

        /// <summary>"Navisworks Manage 2027  (API 24.0, build 1234)".</summary>
        private static string HostDescription()
        {
            try
            {
                var v = NavisApp.Version;
                string product = string.IsNullOrWhiteSpace(v.RuntimeProductName) ? NavisApp.Title : v.RuntimeProductName;
                return (product ?? "Navisworks") + "  (API " + v.ApiMajor + "." + v.ApiMinor + ", build " + v.Build + ")";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Shows a dialog owned by the Navisworks main window, so it stays in front of it.</summary>
        internal static void ShowOwned(Window window)
        {
            IntPtr owner = IntPtr.Zero;
            try
            {
                if (NavisApp.Gui != null && NavisApp.Gui.MainWindow != null)
                    owner = NavisApp.Gui.MainWindow.Handle;
            }
            catch
            {
                owner = IntPtr.Zero;
            }

            if (owner != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = owner;
            else
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            window.ShowDialog();
        }
    }
}
