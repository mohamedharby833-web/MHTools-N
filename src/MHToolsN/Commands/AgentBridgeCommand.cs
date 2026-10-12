using System;
using System.Windows;
using MHToolsN.Bridge;
using MHToolsN.Services;
using MHToolsN.UI;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace MHToolsN.Commands
{
    /// <summary>
    /// The AI Bridge panel: Agent Bridge (start / stop), Trusted C# (off / ask /
    /// auto) and Connect AI App. The first two show their state on the ribbon
    /// (App.CanExecuteCommand) - a button that reads "On" is harder to forget than
    /// a dialog that said so an hour ago.
    /// </summary>
    public static class AgentBridgeCommand
    {
        public const string Title = "MHTools-N - Agent Bridge";

        /// <summary>The one bridge of this Navisworks session, or null.</summary>
        internal static BridgeHost Host;

        private static bool _closingHooked;

        public static bool IsRunning { get { return Host != null; } }

        public static string CodeModeName { get { return Host == null ? "off" : Host.ModeName; } }

        /// <summary>Starts or stops the bridge.</summary>
        public static void Toggle(string[] parameters)
        {
            if (Host != null)
            {
                Shutdown();
                MessageBox.Show("Agent Bridge stopped. AI apps can no longer reach this Navisworks.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Host = new BridgeHost();
                HookClosing();
                MessageBox.Show(
                    "Agent Bridge started for this Navisworks.\n\n" +
                    "A connected AI app (MHTools-N > Connect AI App) can now see what is open and read the model. " +
                    "It cannot change anything that needs Trusted C# until you switch that on.\n\n" +
                    "The bridge stops when you click Agent Bridge again or close Navisworks.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Starting the Agent Bridge", ex);
                Host = null;
                MessageBox.Show("The Agent Bridge could not start.\n\n" + ex.Message + "\n\nLog: " + Watchdog.LogPath,
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Allows, or stops, agents running generated C#. Asked every time it is switched on.</summary>
        public static void TrustedCode(string[] parameters)
        {
            if (Host == null)
            {
                MessageBox.Show("Start the Agent Bridge first (MHTools-N > Agent Bridge).", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Host.Mode != CodeMode.Off)
            {
                Host.Mode = CodeMode.Off;
                MessageBox.Show("Trusted C# switched off. AI apps can still read the model.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new TrustedCodeWindow();
            ClashMatrixCommand.ShowOwned(window);
            if (window.Choice == CodeMode.Off || Host == null) return;

            // Said out loud: the window just closing gave no sign that anything was
            // switched on, and Navisworks redraws the ribbon label only later.
            Host.Mode = window.Choice;
            MessageBox.Show(
                (window.Choice == CodeMode.Ask
                    ? "Trusted C# is ON - Ask.\n\nBefore each change Navisworks tells you what the AI app wants to do, and does it only if you say Yes."
                    : "Trusted C# is ON - Auto.\n\nChanges an AI app makes are kept without asking. Use it on a copy of the model.") +
                "\n\nThe button reads \"Trusted C# On\" while it is on. Click it again to switch it off; " +
                "it also switches off when the Agent Bridge stops or Navisworks closes.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static void ConnectAi(string[] parameters)
        {
            ClashMatrixCommand.ShowOwned(new ConnectAiWindow());
        }

        /// <summary>Navisworks is closing, or the button stopped it: the session file goes too.</summary>
        public static void Shutdown()
        {
            try
            {
                if (Host != null) Host.Dispose();
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Stopping the Agent Bridge", ex);
            }
            Host = null;
        }

        private static void HookClosing()
        {
            if (_closingHooked) return;
            try
            {
                NavisApp.Gui.Closed += (s, e) => Shutdown();
                _closingHooked = true;
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Agent Bridge: watching for Navisworks closing", ex);
            }
        }
    }
}
