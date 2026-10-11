using System;
using System.Windows;
using Autodesk.Navisworks.Api.Plugins;
using MHToolsN.Services;

namespace MHToolsN
{
    /// <summary>
    /// Navisworks plugin entry point. The plugin is discovered from the
    /// ApplicationPlugins bundle (PackageContents.xml), not from an .addin manifest.
    /// Its buttons sit on the "MHTools-N" ribbon tab (Ribbon/AddinRibbon.xaml).
    /// </summary>
    [Plugin("MHToolsN.App", "MAHB",
        ToolTip = "MHTools-N: clash matrix, clash results export",
        DisplayName = "MHTools-N")]
    [RibbonLayout("AddinRibbon.xaml")]
    [RibbonTab("MHToolsN", DisplayName = "MHTools-N")]
    [Command("MHToolsN.ClashMatrix",
        Icon = "ClashMatrix16.png",
        LargeIcon = "ClashMatrix32.png",
        ToolTip = "Build clash tests from a clash matrix (Excel, or the model's own categories)")]
    [Command("MHToolsN.ExportResults",
        Icon = "ExportResults16.png",
        LargeIcon = "ExportResults32.png",
        ToolTip = "Export clash results to Excel and a PDF report")]
    [Command("MHToolsN.AgentBridge",
        Icon = "AgentBridge16.png",
        LargeIcon = "AgentBridge32.png",
        ToolTip = "Start or stop the Agent Bridge: lets a connected AI app (Claude, Cursor, Codex...) see and work with this Navisworks")]
    [Command("MHToolsN.TrustedCode",
        Icon = "TrustedCode16.png",
        LargeIcon = "TrustedCode32.png",
        ToolTip = "Allow connected AI apps to run generated C# in this Navisworks (off by default)")]
    [Command("MHToolsN.ConnectAi",
        Icon = "ConnectAi16.png",
        LargeIcon = "ConnectAi32.png",
        ToolTip = "Find the AI apps on this PC and connect the Agent Bridge to them in one click")]
    [Command("MHToolsN.About",
        Icon = "About16.png",
        LargeIcon = "About32.png",
        ToolTip = "About MHTools-N")]
    public class App : CommandHandlerPlugin
    {
        /// <summary>
        /// Agent Bridge and Trusted C# show their state on the ribbon: pressed and
        /// "On" while they are on. Navisworks asks this whenever it redraws the ribbon.
        /// </summary>
        public override CommandState CanExecuteCommand(string name)
        {
            var state = new CommandState(true);
            try
            {
                if (name == "MHToolsN.AgentBridge")
                {
                    state.IsChecked = Commands.AgentBridgeCommand.IsRunning;
                    state.OverrideDisplayName = Commands.AgentBridgeCommand.IsRunning ? "Agent Bridge On" : "Agent Bridge Off";
                }
                else if (name == "MHToolsN.TrustedCode")
                {
                    string mode = Commands.AgentBridgeCommand.CodeModeName;
                    state.IsChecked = mode != "off";
                    state.OverrideDisplayName = mode == "ask" ? "Trusted C# On (Ask)" : mode == "auto" ? "Trusted C# On (Auto)" : "Trusted C# Off";
                }
            }
            catch (Exception) { }
            return state;
        }

        public override int ExecuteCommand(string name, params string[] parameters)
        {
            try
            {
                switch (name)
                {
                    case "MHToolsN.AgentBridge":
                        Commands.AgentBridgeCommand.Toggle(parameters);
                        break;
                    case "MHToolsN.TrustedCode":
                        Commands.AgentBridgeCommand.TrustedCode(parameters);
                        break;
                    case "MHToolsN.ConnectAi":
                        Commands.AgentBridgeCommand.ConnectAi(parameters);
                        break;
                    case "MHToolsN.ClashMatrix":
                        Commands.ClashMatrixCommand.Run(parameters);
                        break;
                    case "MHToolsN.ExportResults":
                        Commands.ClashMatrixCommand.ExportResults(parameters);
                        break;
                    case "MHToolsN.About":
                        Commands.ClashMatrixCommand.About(parameters);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into Navisworks.
                Watchdog.LogException("Command " + name, ex);
                MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "Log: " + Watchdog.LogPath,
                    "MHTools-N", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 0;
        }
    }
}
