using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MHToolsN.Bridge;
using MHToolsN.Services;

namespace MHToolsN.UI
{
    /// <summary>
    /// Shows each AI app the bridge server finds on the PC with what can be done
    /// for it: Connect / Disconnect, Copy settings, or why it cannot use the
    /// bridge. The connected state is read back from the app's own file each time,
    /// so it is what the app will actually see. Ported from MHTools-R.
    /// </summary>
    public partial class ConnectAiWindow : Window
    {
        private const string Caption = "Connect AI App";

        private readonly string _serverExe = AgentServerExe.Path;
        private string _entryName = "mhtools-n-bridge";

        public ConnectAiWindow()
        {
            MhTheme.Apply(this);
            InitializeComponent();

            try { MaxHeight = SystemParameters.WorkArea.Height; }
            catch (Exception) { }

            Refresh();
        }

        private void Refresh()
        {
            bool installed = AgentServerExe.Installed;
            ServerText.Text = installed
                ? _serverExe
                : _serverExe + "\nNot found - install MHTools-N from its MSI; a development build has no server.";
            CopyPathButton.IsEnabled = installed;
            Rows.Children.Clear();

            if (!installed)
            {
                Footer.Status = "The bridge server is not installed.";
                return;
            }

            Dictionary<string, object> report;
            Cursor = Cursors.Wait;
            try
            {
                report = AgentServerExe.Run("--apps");
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Connect AI App: scan", ex);
                Footer.Status = "Could not look for AI apps: " + ex.Message;
                return;
            }
            finally
            {
                Cursor = null;
            }

            _entryName = BridgeWire.Text(report, "entryName", _entryName);
            int found = 0, oneClick = 0, connected = 0;
            var apps = report.ContainsKey("apps") ? report["apps"] as IEnumerable : null;
            if (apps != null)
            {
                foreach (object item in apps)
                {
                    var app = item as Dictionary<string, object>;
                    if (app == null) continue;
                    string name = BridgeWire.Text(app, "name");
                    string mode = BridgeWire.Text(app, "mode");
                    if (name != "Other app") found++;

                    if (mode == "OneClick")
                    {
                        oneClick++;
                        bool ours;
                        Rows.Children.Add(OneClickRow(app, out ours));
                        if (ours) connected++;
                    }
                    else if (mode == "CopySettings")
                    {
                        string detail = name == "Other app" ? BridgeWire.Text(app, "evidence") : "Found: " + BridgeWire.Text(app, "evidence");
                        string state = name == "Other app"
                            ? "Copy settings gives the common \"mcpServers\" entry most apps accept; an app that only asks for a command needs the path below."
                            : "Supported by hand: copy the entry and paste it into its settings.";
                        string snippet = BridgeWire.Text(app, "snippet");
                        string where = BridgeWire.Text(app, "whereToPaste");
                        string label = name == "Other app" ? "your AI app" : name;
                        Rows.Children.Add(Row(name, detail, state, "MhTextMutedBrush",
                            MakeButton("Copy settings", false, (s, e) => CopySettings(label, snippet, where))));
                    }
                    else
                    {
                        Rows.Children.Add(Row(name, "Found: " + BridgeWire.Text(app, "evidence"),
                            "Cannot use the bridge. " + BridgeWire.Text(app, "whyNot"), "MhTextMutedBrush", null));
                    }
                }
            }

            Footer.Status = found == 0
                ? "No known AI app found on this PC."
                : found + " found, " + connected + " of " + oneClick + " connected.";
        }

        // ------------------------------------------------------------ rows

        private UIElement OneClickRow(Dictionary<string, object> app, out bool ours)
        {
            string name = BridgeWire.Text(app, "name");
            string readError = BridgeWire.Text(app, "readError", null);
            string command = BridgeWire.Text(app, "connectedCommand", null);
            object connected;
            ours = app.TryGetValue("connected", out connected) && connected is bool && (bool)connected;

            string state, brush = "MhTextMutedBrush";
            if (!string.IsNullOrEmpty(readError)) { state = "Its settings file could not be read: " + readError; brush = "MhDangerBrush"; }
            else if (ours) { state = "Connected"; brush = "MhSuccessBrush"; }
            else if (!string.IsNullOrEmpty(command)) { state = "Connected to another copy: " + command; brush = "MhWarningBrush"; }
            else state = "Not connected";

            Button button = null;
            if (string.IsNullOrEmpty(readError))
            {
                bool isOurs = ours;
                string afterChange = BridgeWire.Text(app, "afterChange");
                button = MakeButton(isOurs ? "Disconnect" : (!string.IsNullOrEmpty(command) ? "Update" : "Connect"), !isOurs,
                    (s, e) => Change(name, !isOurs, afterChange));
            }
            return Row(name, "Found: " + BridgeWire.Text(app, "evidence") + "   -   settings: " + BridgeWire.Text(app, "configPath"),
                state, brush, button);
        }

        private UIElement Row(string name, string detail, string state, string brush, Button button)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(Text(name, "MhStrongText"));
            var stateText = Text(state, "MhMutedText");
            stateText.SetResourceReference(TextBlock.ForegroundProperty, brush);
            text.Children.Add(stateText);
            text.Children.Add(Text(detail, "MhMutedText"));
            row.Children.Add(text);

            if (button != null)
            {
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
            }

            var wrap = new StackPanel();
            wrap.Children.Add(row);
            var line = new Border { Margin = new Thickness(0, 0, 0, 10) };
            line.SetResourceReference(StyleProperty, "MhDivider");
            wrap.Children.Add(line);
            return wrap;
        }

        private static Button MakeButton(string content, bool primary, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = content,
                MinWidth = 112,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 0, 0)
            };
            button.SetResourceReference(StyleProperty, primary ? "MhPrimaryButton" : "MhButton");
            button.Click += click;
            return button;
        }

        private static TextBlock Text(string value, string style)
        {
            var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(StyleProperty, style);
            return block;
        }

        // --------------------------------------------------------- actions

        private void Change(string appName, bool connect, string afterChange)
        {
            try
            {
                Cursor = Cursors.Wait;
                var result = AgentServerExe.Run(connect ? "--connect" : "--disconnect", appName);
                Cursor = null;
                Watchdog.Log((connect ? "Connect AI App: connected " : "Connect AI App: disconnected ") + appName + ".");
                MessageBox.Show(this,
                    (connect ? "MHTools-N bridge added to " : "MHTools-N bridge removed from ") + appName + ".\n\n" +
                    BridgeWire.Text(result, "afterChange", afterChange) +
                    "\n\nThe previous settings file is kept beside it as .bak-<date>.",
                    Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Cursor = null;
                Watchdog.LogException("Connect AI App: changing " + appName, ex);
                MessageBox.Show(this, "Could not change " + appName + "'s settings.\n\n" + ex.Message,
                    Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Refresh();
        }

        private void CopySettings(string appName, string snippet, string where)
        {
            if (!ToClipboard(snippet)) return;
            Watchdog.Log("Connect AI App: copied the bridge settings for " + appName + ".");
            MessageBox.Show(this,
                "The bridge entry for " + appName + " is on the clipboard:\n\n" + snippet + "\n\n" +
                "Paste it into " + where + "\n\n" +
                "If the file already has that section, add only the \"" + _entryName + "\" part inside it. " +
                "Formats change between app versions: if the app's own documentation shows a different shape, follow it - " +
                "what matters is the program path. Then restart the app.",
                Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnCopyPath(object sender, RoutedEventArgs e)
        {
            if (ToClipboard(_serverExe))
                MessageBox.Show(this, "The bridge server's path is on the clipboard:\n\n" + _serverExe,
                    Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // The clipboard can be held for a moment by another program; a second try usually gets it.
        private bool ToClipboard(string text)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            MessageBox.Show(this, "The clipboard is busy - try again.\n\n" + text, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void OnRescan(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
