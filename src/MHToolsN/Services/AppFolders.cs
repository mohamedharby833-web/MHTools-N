using System;
using System.IO;

namespace MHToolsN.Services
{
    /// <summary>
    /// Where MHTools-N keeps what it remembers between sessions:
    /// %AppData%\MHTools-N. Up to 1.8 the add-in was called ClashMatrix and used
    /// %AppData%\ClashMatrix; the first time the new folder is asked for, the
    /// files from the old one are copied across (the old folder is left as it was).
    /// </summary>
    public static class AppFolders
    {
        private static readonly object Gate = new object();
        private static bool _migrated;

        public static string Settings
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string folder = Path.Combine(appData, "MHTools-N");
                lock (Gate)
                {
                    if (!_migrated)
                    {
                        _migrated = true;
                        CopyOldSettings(Path.Combine(appData, "ClashMatrix"), folder);
                    }
                }
                return folder;
            }
        }

        /// <summary>A file in <see cref="Settings"/>.</summary>
        public static string SettingsFile(string name)
        {
            return Path.Combine(Settings, name);
        }

        private static void CopyOldSettings(string oldFolder, string newFolder)
        {
            try
            {
                if (!Directory.Exists(oldFolder)) return;
                Directory.CreateDirectory(newFolder);
                foreach (string file in Directory.GetFiles(oldFolder))
                {
                    string target = Path.Combine(newFolder, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                }
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Copy settings from %AppData%\\ClashMatrix", ex);
            }
        }
    }
}
