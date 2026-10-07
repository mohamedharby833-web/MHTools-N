using System;
using System.IO;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Appends a running diagnostic log to %TEMP%\ClashMatrix_Log.txt. Used to
    /// capture what the add-in found/did so issues can be shared and diagnosed.
    /// </summary>
    public static class Watchdog
    {
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "ClashMatrix_Log.txt"); }
        }

        public static void Clear()
        {
            try { File.WriteAllText(LogPath, ""); }
            catch { /* best effort */ }
        }

        public static void Log(string message)
        {
            try
            {
                lock (Gate)
                {
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
