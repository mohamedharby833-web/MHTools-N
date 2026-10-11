using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MHToolsN.AgentServer
{
    /// <summary>How an AI app found on the PC can be given the bridge.</summary>
    internal enum AiAppMode { OneClick, CopySettings, NotPossible }

    /// <summary>The shape an app's MCP entry takes - they differ from app to app.</summary>
    internal enum SnippetFormat { McpServers, VsCodeServers, OpenCode, ContinueYaml, ZedContextServers }

    internal sealed class AiAppInfo
    {
        public string Name;
        public AiAppMode Mode;
        public SnippetFormat Format;
        public string WhereToPaste;
        public string WhyNot;

        // Evidence that it is installed - any one is enough.
        public string UninstallName;
        public string PackagePrefix;
        public string[] Commands = new string[0];
        public string[] Folders = new string[0];
        public string ExtensionPrefix;
    }

    /// <summary>
    /// The AI apps that can use the Agent Bridge and how to add or remove ours in
    /// their settings. Ported from MHTools-R (AgentClients + AiAppScanner), whose
    /// formats were each read from a real install; an app whose file could not be
    /// checked is offered as "copy settings" rather than written to by guess.
    ///
    /// Every change backs the file up first (beside it, timestamped) and touches
    /// only the "mhtools-n-bridge" entry: the user's other servers - MHTools-R's
    /// bridge among them - and settings are written back as they were read.
    /// </summary>
    internal static class AiApps
    {
        public const string EntryName = "mhtools-n-bridge";

        private enum Format { McpServersJson, ClaudeCodeJson, CodexToml }

        private sealed class Client
        {
            public string Name;
            public Format Format;
            public string ConfigPath;
            public string AfterChange;
        }

        private static readonly AiAppInfo[] Catalog =
        {
            new AiAppInfo { Name = "Claude Desktop", Mode = AiAppMode.OneClick,
                UninstallName = @"^Claude$", PackagePrefix = "Claude_", Folders = new[] { @"%AppData%\Claude" } },
            new AiAppInfo { Name = "Claude Code", Mode = AiAppMode.OneClick,
                Commands = new[] { "claude" }, Folders = new[] { @"~\.claude" } },
            new AiAppInfo { Name = "Cursor", Mode = AiAppMode.OneClick,
                UninstallName = @"^Cursor\b", Commands = new[] { "cursor" }, Folders = new[] { @"~\.cursor" } },
            new AiAppInfo { Name = "Codex", Mode = AiAppMode.OneClick,
                PackagePrefix = "OpenAI.Codex_", Commands = new[] { "codex" }, Folders = new[] { @"~\.codex" } },

            new AiAppInfo { Name = "VS Code (GitHub Copilot)", Mode = AiAppMode.CopySettings, Format = SnippetFormat.VsCodeServers,
                UninstallName = @"^Microsoft Visual Studio Code", Commands = new[] { "code" }, Folders = new[] { @"%AppData%\Code" },
                WhereToPaste = "VS Code: Command Palette (Ctrl+Shift+P) > \"MCP: Open User Configuration\" - the file %AppData%\\Code\\User\\mcp.json. Then use Copilot Chat in Agent mode." },
            new AiAppInfo { Name = "Cline (VS Code extension)", Mode = AiAppMode.CopySettings, Format = SnippetFormat.McpServers,
                ExtensionPrefix = "saoudrizwan.claude-dev",
                WhereToPaste = "Cline panel > MCP Servers > Installed > Configure MCP Servers (cline_mcp_settings.json)." },
            new AiAppInfo { Name = "Continue (VS Code extension)", Mode = AiAppMode.CopySettings, Format = SnippetFormat.ContinueYaml,
                ExtensionPrefix = "continue.continue", Folders = new[] { @"~\.continue" },
                WhereToPaste = "%UserProfile%\\.continue\\config.yaml - under mcpServers." },
            new AiAppInfo { Name = "Windsurf", Mode = AiAppMode.CopySettings, Format = SnippetFormat.McpServers,
                UninstallName = @"^Windsurf\b", Commands = new[] { "windsurf" }, Folders = new[] { @"~\.codeium\windsurf" },
                WhereToPaste = "Windsurf Settings > Cascade > MCP Servers > View raw config (%UserProfile%\\.codeium\\windsurf\\mcp_config.json)." },
            new AiAppInfo { Name = "Gemini CLI", Mode = AiAppMode.CopySettings, Format = SnippetFormat.McpServers,
                Commands = new[] { "gemini" }, Folders = new[] { @"~\.gemini" },
                WhereToPaste = "%UserProfile%\\.gemini\\settings.json." },
            new AiAppInfo { Name = "OpenCode", Mode = AiAppMode.CopySettings, Format = SnippetFormat.OpenCode,
                Commands = new[] { "opencode" }, Folders = new[] { @"~\.config\opencode" },
                WhereToPaste = "%UserProfile%\\.config\\opencode\\opencode.json." },
            new AiAppInfo { Name = "Zed", Mode = AiAppMode.CopySettings, Format = SnippetFormat.ZedContextServers,
                UninstallName = @"^Zed\b", Commands = new[] { "zed" }, Folders = new[] { @"%AppData%\Zed" },
                WhereToPaste = "Zed settings (Ctrl+,) - the settings.json file, under context_servers." },
            new AiAppInfo { Name = "LM Studio", Mode = AiAppMode.CopySettings, Format = SnippetFormat.McpServers,
                UninstallName = @"^LM Studio\b", Folders = new[] { @"~\.lmstudio" },
                WhereToPaste = "LM Studio: Program tab > Install > Edit mcp.json (%UserProfile%\\.lmstudio\\mcp.json)." },

            new AiAppInfo { Name = "ChatGPT desktop", Mode = AiAppMode.NotPossible,
                UninstallName = @"^ChatGPT\b", PackagePrefix = "OpenAI.ChatGPT",
                WhyNot = "ChatGPT connects to remote MCP servers only; it cannot start a program on this PC, and the bridge is deliberately local to it." }
        };

        private static IReadOnlyList<Client> Clients()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                new Client { Name = "Claude Desktop", Format = Format.McpServersJson,
                    ConfigPath = Path.Combine(appData, "Claude", "claude_desktop_config.json"),
                    AfterChange = "Quit Claude Desktop (tray icon > Quit) and start it again." },
                new Client { Name = "Claude Code", Format = Format.ClaudeCodeJson,
                    ConfigPath = Path.Combine(home, ".claude.json"),
                    AfterChange = "Takes effect in new Claude Code sessions. Close running ones first, or they may write their old settings back." },
                new Client { Name = "Cursor", Format = Format.McpServersJson,
                    ConfigPath = Path.Combine(home, ".cursor", "mcp.json"),
                    AfterChange = "Restart Cursor, or reload MCP servers in its settings." },
                new Client { Name = "Codex", Format = Format.CodexToml,
                    ConfigPath = Path.Combine(home, ".codex", "config.toml"),
                    AfterChange = "Restart Codex." }
            };
        }

        // ================================================================ --apps

        /// <summary>Every known app found on the PC, with what can be done for it.</summary>
        public static JsonArray Report(string serverExe)
        {
            var uninstall = UninstallNames();
            var packages = StorePackages();
            var path = PathFolders();
            var extensions = ExtensionFolders();
            var clients = Clients();

            var list = new JsonArray();
            foreach (var app in Catalog)
            {
                var evidence = new List<string>();
                if (app.UninstallName != null && uninstall.Any(n => Regex.IsMatch(n, app.UninstallName, RegexOptions.IgnoreCase)))
                    evidence.Add("installed program");
                if (app.PackagePrefix != null && packages.Any(p => p.StartsWith(app.PackagePrefix, StringComparison.OrdinalIgnoreCase)))
                    evidence.Add("Store app");
                foreach (var command in app.Commands)
                    if (OnPath(path, command)) evidence.Add("'" + command + "' command");
                foreach (var folder in app.Folders)
                    if (Directory.Exists(Expand(folder))) { evidence.Add("settings folder"); break; }
                if (app.ExtensionPrefix != null && extensions.Any(e => e.StartsWith(app.ExtensionPrefix, StringComparison.OrdinalIgnoreCase)))
                    evidence.Add("editor extension");
                if (evidence.Count == 0) continue;

                var row = new JsonObject
                {
                    ["name"] = app.Name,
                    ["mode"] = app.Mode.ToString(),
                    ["evidence"] = string.Join(", ", evidence)
                };
                if (app.Mode == AiAppMode.OneClick)
                {
                    var client = clients.First(c => c.Name == app.Name);
                    row["configPath"] = client.ConfigPath;
                    row["afterChange"] = client.AfterChange;
                    try
                    {
                        string command = ConnectedCommand(client);
                        row["connectedCommand"] = command;
                        row["connected"] = command != null && SamePath(command, serverExe);
                    }
                    catch (Exception ex)
                    {
                        row["readError"] = ex.Message;
                    }
                }
                else if (app.Mode == AiAppMode.CopySettings)
                {
                    row["snippet"] = Snippet(app.Format, serverExe);
                    row["whereToPaste"] = app.WhereToPaste;
                }
                else
                {
                    row["whyNot"] = app.WhyNot;
                }
                list.Add(row);
            }

            list.Add(new JsonObject
            {
                ["name"] = "Other app",
                ["mode"] = "CopySettings",
                ["evidence"] = "Any AI app with MCP support that is not listed above",
                ["snippet"] = Snippet(SnippetFormat.McpServers, serverExe),
                ["whereToPaste"] = "the app's MCP settings - see its documentation for the file or menu."
            });

            ServerLog.Info("AI app scan: " + (list.Count - 1) + " app(s) found.");
            return list;
        }

        // ============================================== --connect / --disconnect

        public static string Connect(string appName, string serverExe)
        {
            var client = Clients().FirstOrDefault(c => c.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (client == null) throw new ArgumentException("No one-click setup for '" + appName + "'.");

            Backup(client);
            if (client.Format == Format.CodexToml)
            {
                var lines = File.Exists(client.ConfigPath) ? RemoveTomlEntry(File.ReadAllLines(client.ConfigPath)) : new List<string>();
                while (lines.Count > 0 && lines[lines.Count - 1].Trim() == "") lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                // A literal (single-quoted) TOML string: backslashes stay as they are.
                lines.Add("[mcp_servers." + EntryName + "]");
                lines.Add("command = '" + serverExe + "'");
                lines.Add("args = []");
                WriteText(client, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            else
            {
                var root = File.Exists(client.ConfigPath) ? ReadJson(client) : new JsonObject();
                var servers = root["mcpServers"] as JsonObject;
                if (servers == null)
                {
                    servers = new JsonObject();
                    root["mcpServers"] = servers;
                }

                var entry = new JsonObject();
                if (client.Format == Format.ClaudeCodeJson) entry["type"] = "stdio";
                entry["command"] = serverExe;
                entry["args"] = new JsonArray();
                if (client.Format == Format.ClaudeCodeJson) entry["env"] = new JsonObject();
                servers[EntryName] = entry;
                WriteJson(client, root);
            }
            ServerLog.Info("Connected " + client.Name + " (" + client.ConfigPath + ").");
            return client.AfterChange;
        }

        public static string Disconnect(string appName)
        {
            var client = Clients().FirstOrDefault(c => c.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (client == null) throw new ArgumentException("No one-click setup for '" + appName + "'.");
            if (!File.Exists(client.ConfigPath)) return client.AfterChange;

            Backup(client);
            if (client.Format == Format.CodexToml)
            {
                WriteText(client, string.Join(Environment.NewLine, RemoveTomlEntry(File.ReadAllLines(client.ConfigPath))) + Environment.NewLine);
            }
            else
            {
                var root = ReadJson(client);
                if (root["mcpServers"] is JsonObject servers) servers.Remove(EntryName);
                WriteJson(client, root);
            }
            ServerLog.Info("Disconnected " + client.Name + " (" + client.ConfigPath + ").");
            return client.AfterChange;
        }

        // ================================================================ files

        private static string ConnectedCommand(Client client)
        {
            if (!File.Exists(client.ConfigPath)) return null;
            if (client.Format == Format.CodexToml) return TomlCommand(File.ReadAllLines(client.ConfigPath));
            var root = ReadJson(client);
            var server = (root["mcpServers"] as JsonObject)?[EntryName] as JsonObject;
            return server == null ? null : (string)server["command"];
        }

        private static void Backup(Client client)
        {
            if (!File.Exists(client.ConfigPath)) return;
            File.Copy(client.ConfigPath, client.ConfigPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
        }

        private static JsonObject ReadJson(Client client)
        {
            var text = File.ReadAllText(client.ConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = node as JsonObject;
            if (root == null) throw new InvalidDataException(client.ConfigPath + " does not hold a JSON object.");
            return root;
        }

        // Numbers are written back exactly as they were read, and text is escaped
        // as little as JSON allows - the user's other settings come back as written.
        private static void WriteJson(Client client, JsonObject root)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            WriteText(client, root.ToJsonString(options) + Environment.NewLine);
        }

        // Written beside, then swapped in, so a crash half way never leaves a broken
        // settings file; retried while the app has its file open, then overwritten
        // in place (the backup is the safety net for that path).
        private static void WriteText(Client client, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(client.ConfigPath));
            var temp = client.ConfigPath + ".mhtools-n.tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));

            if (!File.Exists(client.ConfigPath))
            {
                File.Move(temp, client.ConfigPath);
                return;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Replace(temp, client.ConfigPath, null);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
            File.Copy(temp, client.ConfigPath, true);
            File.Delete(temp);
        }

        // ================================================================= TOML

        private static bool IsOurHeader(string line)
        {
            var t = line.Trim();
            return t == "[mcp_servers." + EntryName + "]" || t.StartsWith("[mcp_servers." + EntryName + ".", StringComparison.Ordinal);
        }

        private static List<string> RemoveTomlEntry(string[] lines)
        {
            var kept = new List<string>();
            bool inOurs = false;
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("[", StringComparison.Ordinal)) inOurs = IsOurHeader(line);
                if (!inOurs) kept.Add(line);
            }
            return kept;
        }

        private static string TomlCommand(string[] lines)
        {
            bool inOurs = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal)) { inOurs = t == "[mcp_servers." + EntryName + "]"; continue; }
                if (!inOurs || !t.StartsWith("command", StringComparison.Ordinal)) continue;
                var eq = t.IndexOf('=');
                if (eq < 0) continue;
                var value = t.Substring(eq + 1).Trim();
                if (value.Length < 2) return value;
                if (value[0] == '\'') return value.Substring(1, value.Length - 2);
                if (value[0] == '"') return value.Substring(1, value.Length - 2).Replace("\\\\", "\\");
                return value;
            }
            return null;
        }

        // ============================================================= snippets

        private static string Snippet(SnippetFormat format, string serverExe)
        {
            var json = serverExe.Replace("\\", "\\\\");
            var name = EntryName;
            switch (format)
            {
                case SnippetFormat.VsCodeServers:
                    return "{\n  \"servers\": {\n    \"" + name + "\": {\n      \"type\": \"stdio\",\n      \"command\": \"" + json + "\",\n      \"args\": []\n    }\n  }\n}";
                case SnippetFormat.OpenCode:
                    return "{\n  \"mcp\": {\n    \"" + name + "\": {\n      \"type\": \"local\",\n      \"command\": [\"" + json + "\"],\n      \"enabled\": true\n    }\n  }\n}";
                case SnippetFormat.ContinueYaml:
                    return "mcpServers:\n  - name: " + name + "\n    command: '" + serverExe + "'\n    args: []";
                case SnippetFormat.ZedContextServers:
                    return "\"context_servers\": {\n  \"" + name + "\": {\n    \"command\": \"" + json + "\",\n    \"args\": []\n  }\n}";
                default:
                    return "{\n  \"mcpServers\": {\n    \"" + name + "\": {\n      \"command\": \"" + json + "\",\n      \"args\": []\n    }\n  }\n}";
            }
        }

        // ============================================================== looking

        private static List<string> UninstallNames()
        {
            var names = new List<string>();
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var hiveName in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hive = RegistryKey.OpenBaseKey(hiveName, view))
                    using (var uninstall = hive.OpenSubKey(key))
                    {
                        if (uninstall == null) continue;
                        foreach (var sub in uninstall.GetSubKeyNames())
                        {
                            using (var app = uninstall.OpenSubKey(sub))
                            {
                                var display = app?.GetValue("DisplayName") as string;
                                if (!string.IsNullOrWhiteSpace(display)) names.Add(display.Trim());
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            return names;
        }

        private static List<string> StorePackages()
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                return Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).ToList() : new List<string>();
            }
            catch (Exception) { return new List<string>(); }
        }

        // PATH as given to this process, plus the user's PATH as it is now.
        private static List<string> PathFolders()
        {
            var text = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";";
            try { text += Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? ""; }
            catch (Exception) { }
            return text.Split(';').Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')))
                .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool OnPath(List<string> folders, string command)
        {
            foreach (var folder in folders)
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                try { if (File.Exists(Path.Combine(folder, command + ext))) return true; }
                catch (Exception) { }
            }
            return false;
        }

        private static List<string> ExtensionFolders()
        {
            var names = new List<string>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var folder in new[] { @".vscode\extensions", @".cursor\extensions", @".windsurf\extensions" })
            {
                try
                {
                    var path = Path.Combine(home, folder);
                    if (Directory.Exists(path)) names.AddRange(Directory.GetDirectories(path).Select(Path.GetFileName));
                }
                catch (Exception) { }
            }
            return names;
        }

        private static string Expand(string folder)
        {
            if (folder.StartsWith("~", StringComparison.Ordinal))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + folder.Substring(1);
            return Environment.ExpandEnvironmentVariables(folder);
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
    }
}
