using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MHToolsN.AgentServer
{
    internal static class Program
    {
        /// <summary>What an AI app is told about this server when it connects.</summary>
        private const string Instructions =
            "MHTools-N Agent Bridge: a live connection to Autodesk Navisworks Manage on this PC, through the MHTools-N add-in. " +
            "Start with get_context to see what is open. With one Navisworks running, sessionId can be left empty on every tool. " +
            "If no session is found, ask the user to click MHTools-N > Agent Bridge in Navisworks. " +
            "A tool that answers state 'running' with an operationId is still working in Navisworks: poll get_execution, " +
            "and never repeat a change because of a timeout. Navisworks runs one request at a time and is busy while a dialog is open. " +
            "For clash questions start with get_clash_summary; to look at things use capture_view and get_clash_image. " +
            "View tools (select_items, zoom_to_items, set_visibility, color_items, go_to_viewpoint) change only what the user sees. " +
            "Model changes (create_set, save_viewpoint, set_clash_status, run_clash_tests, apply_clash_matrix, apply_takeoff) preview by default (dryRun=true): " +
            "show the user the preview and make the change (dryRun=false) only when they agree. They need Trusted C# on in Navisworks; " +
            "in Ask mode the user confirms each one there. The bridge never saves the file.";

        private static async Task<int> Main(string[] args)
        {
            // The add-in's Connect AI App window runs these; each prints one JSON
            // value to stdout and exits.
            if (args.Length >= 1 && args[0].StartsWith("--", StringComparison.Ordinal))
                return Command(args);

            ServerLog.Info("Server started (" + typeof(Program).Assembly.GetName().Version + ").");
            _ = Task.Run(() => ExtractionCleanup.RemoveUnused());

            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            // stderr only: stdout carries MCP.
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSingleton<SessionClient>();
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "mhtools-n-bridge",
                        Title = "MHTools-N Agent Bridge (Navisworks)",
                        Version = typeof(Program).Assembly.GetName().Version.ToString(3)
                    };
                    options.ServerInstructions = Instructions;
                })
                .WithStdioServerTransport()
                .WithTools<NavisTools>();
            await builder.Build().RunAsync();

            ServerLog.Info("Server stopped.");
            return 0;
        }

        private static int Command(string[] args)
        {
            string serverExe = Environment.ProcessPath;
            try
            {
                switch (args[0])
                {
                    case "--apps":
                        Print(new JsonObject { ["ok"] = true, ["serverExe"] = serverExe, ["entryName"] = AiApps.EntryName, ["apps"] = AiApps.Report(serverExe) });
                        return 0;
                    case "--connect":
                        Print(new JsonObject { ["ok"] = true, ["afterChange"] = AiApps.Connect(Arg(args), serverExe) });
                        return 0;
                    case "--disconnect":
                        Print(new JsonObject { ["ok"] = true, ["afterChange"] = AiApps.Disconnect(Arg(args)) });
                        return 0;
                    case "--version":
                        Print(new JsonObject { ["ok"] = true, ["version"] = typeof(Program).Assembly.GetName().Version.ToString(3) });
                        return 0;
                    default:
                        Print(new JsonObject { ["ok"] = false, ["message"] = "Unknown option " + args[0] + "." });
                        return 2;
                }
            }
            catch (Exception ex)
            {
                ServerLog.Error(args[0] + " failed", ex);
                Print(new JsonObject { ["ok"] = false, ["message"] = ex.Message });
                return 1;
            }
        }

        private static string Arg(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1])) throw new ArgumentException(args[0] + " needs the app's name.");
            return args[1];
        }

        private static void Print(JsonNode value)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Console.Out.WriteLine(value.ToJsonString(Wire.Options));
        }
    }
}
