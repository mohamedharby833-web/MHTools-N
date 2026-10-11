using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// The MCP server the MSI installs beside the add-in builds:
    /// MHTools-N.bundle\Contents\Server\MHTools-N.AgentServer.exe. The add-in runs it
    /// with --apps / --connect / --disconnect for the Connect AI App window, so the
    /// AI apps' settings files are read and written by .NET 10's JSON (which keeps
    /// their order and numbers), not by anything inside Navisworks.
    /// </summary>
    internal static class AgentServerExe
    {
        public static string Path
        {
            get
            {
                // The add-in DLL sits in Contents\<NN>; the server beside those folders.
                string addinFolder = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(addinFolder, "..", "Server", "MHTools-N.AgentServer.exe"));
            }
        }

        public static bool Installed { get { return File.Exists(Path); } }

        /// <summary>Runs the server with one option and returns the JSON object it prints.</summary>
        public static Dictionary<string, object> Run(params string[] args)
        {
            if (!Installed) throw new FileNotFoundException("The bridge server is not installed: " + Path);

            var start = new ProcessStartInfo(Path, Quote(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    throw new TimeoutException("The bridge server did not answer in 30 s.");
                }
                var result = BridgeWire.Parse(output.Trim());
                object ok;
                if (!result.TryGetValue("ok", out ok) || !(ok is bool) || !(bool)ok)
                    throw new InvalidOperationException(BridgeWire.Text(result, "message", "The bridge server reported an error."));
                return result;
            }
        }

        private static string Quote(string[] args)
        {
            var parts = new List<string>();
            foreach (string a in args) parts.Add("\"" + a.Replace("\"", "\\\"") + "\"");
            return string.Join(" ", parts);
        }
    }
}
