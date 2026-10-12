using System;
using System.Windows.Forms;
using MHToolsN.Services;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Every change an agent makes to the model goes through here first. Changing
    /// the view (selection, zoom, colours, hiding) does not: it is not part of the
    /// coordination data, and the user sees it happen.
    ///
    /// - Trusted C# Off: refused, with a message the agent can pass on.
    /// - Ask: Navisworks asks the user, in words, what the agent wants to do. No is
    ///   the default button: a key pressed by accident refuses.
    /// - Auto: allowed, and logged.
    ///
    /// Runs on the UI thread (inside a bridge method). While the question is open the
    /// agent's call reports "running" and the agent polls get_execution.
    /// </summary>
    internal static class ChangeGate
    {
        public static void Allow(BridgeHost host, string what)
        {
            if (host.Mode == CodeMode.Off)
                throw new BridgeException("CHANGES_OFF",
                    "Changing the model needs Trusted C# switched on in Navisworks (MHTools-N > Trusted C#, Ask or Auto). " +
                    "Ask the user to switch it on; dryRun=true previews work without it.");

            if (host.Mode == CodeMode.Ask)
            {
                DialogResult answer;
                string text = "An AI app connected through the Agent Bridge wants to:\n\n" + what +
                              "\n\nAllow it? It is one step you can undo with Ctrl+Z.";
                try
                {
                    IWin32Window owner = NavisApp.Gui != null ? NavisApp.Gui.MainWindow : null;
                    answer = owner != null
                        ? MessageBox.Show(owner, text, "MHTools-N - AI change", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                        : MessageBox.Show(text, "MHTools-N - AI change", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                }
                catch (Exception ex)
                {
                    Watchdog.LogException("Bridge: asking the user", ex);
                    answer = DialogResult.No;
                }
                if (answer != DialogResult.Yes)
                {
                    Watchdog.Log("Bridge change REFUSED by the user: " + what);
                    throw new BridgeException("REJECTED_BY_USER",
                        "The user said No in Navisworks. Nothing was changed. Ask them before trying again.");
                }
            }
            Watchdog.Log("Bridge change (" + host.ModeName + "): " + what);
        }
    }
}
