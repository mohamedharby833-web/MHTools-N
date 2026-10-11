using System;
using System.IO;

namespace MHToolsN.AgentServer
{
    /// <summary>
    /// %LocalAppData%\MHTools-N\AgentServer.log - stdout is MCP, so nothing may be
    /// written there. Kept under 1 MiB: past that it starts again.
    /// </summary>
    internal static class ServerLog
    {
        private static readonly object Gate = new object();

        public static string Path
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MHTools-N", "AgentServer.log");
            }
        }

        public static void Info(string message)
        {
            Write(message);
        }

        public static void Error(string message, Exception ex)
        {
            Write(message + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        private static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    var info = new FileInfo(Path);
                    if (info.Exists && info.Length > 1024 * 1024) File.WriteAllText(Path, "");
                    File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }
    }
}
