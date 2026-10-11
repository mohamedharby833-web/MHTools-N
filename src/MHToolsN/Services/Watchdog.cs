using System;
using System.IO;

namespace MHToolsN.Services
{
    /// <summary>
    /// Appends a running diagnostic log to %TEMP%\MHTools-N_Log.txt. Used to
    /// capture what the add-in found/did so issues can be shared and diagnosed.
    /// The log is started afresh once it passes <see cref="MaxBytes"/>.
    /// </summary>
    public static class Watchdog
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "MHTools-N_Log.txt"); }
        }

        public static void Clear()
        {
            try { lock (Gate) File.WriteAllText(LogPath, ""); }
            catch { /* best effort */ }
        }

        public static void Log(string message)
        {
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.WriteAllText(LogPath, "");

                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* best effort */ }
        }

        public static void LogException(string context, Exception ex)
        {
            Log(context + " FAILED: " + ex.GetType().Name + ": " + ex.Message);
            Log(ex.ToString());
        }
    }
}
