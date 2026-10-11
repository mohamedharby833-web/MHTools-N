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
    [Command("MHToolsN.About",
        Icon = "About16.png",
        LargeIcon = "About32.png",
        ToolTip = "About MHTools-N")]
    public class App : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string name, params string[] parameters)
        {
            try
            {
                switch (name)
                {
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
