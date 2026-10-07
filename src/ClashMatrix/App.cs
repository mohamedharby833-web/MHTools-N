using Autodesk.Navisworks.Api.Plugins;

namespace ClashMatrix
{
    /// <summary>
    /// Navisworks plugin entry point. The plugin is discovered automatically from
    /// the Plugins folder (no .addin manifest, unlike Revit).
    /// </summary>
    [Plugin("ClashMatrix.App", "MAHB",
        ToolTip = "Generate Navisworks clash tests from an Excel clash matrix",
        DisplayName = "Clash Matrix")]
    [RibbonLayout("AddinRibbon.xaml")]
    [RibbonTab("Clash Matrix")]
    [Command("ClashMatrix.Import",
        Icon = "ClashMatrix16.png",
        LargeIcon = "ClashMatrix32.png",
        ToolTip = "Import an Excel clash matrix and generate clash tests")]
    public class App : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string name, params string[] parameters)
        {
            switch (name)
            {
                case "ClashMatrix.Import":
                    Commands.ClashMatrixCommand.Run(parameters);
                    break;
            }
            return 0;
        }
    }
}
