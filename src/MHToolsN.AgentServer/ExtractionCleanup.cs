using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MHToolsN.AgentServer
{
    /// <summary>
    /// Ported from MHTools-R's ExtractionCleanup unchanged but for the names.
    ///
    /// The server is published to unpack itself at its first start
    /// (IncludeAllContentForSelfExtract), one folder per build under
    /// %TEMP%\.net\MHTools-N.AgentServer\, and to run from there. That is what
    /// lets an MSI update replace the exe while AI apps still have servers
    /// running: a server never reads its exe again after starting. Before, a
    /// running server that had not been called yet failed on its first call
    /// once its exe had been swapped (it read the new file).
    ///
    /// Each update leaves about 74 MB behind. This removes the folders of other
    /// builds, never one a running server uses - two guards:
    ///
    /// 1. Every server has its System.Private.CoreLib.dll loaded from its own
    ///    folder, so the folders in use are known, not guessed from dates.
    ///    (Not coreclr.dll: the single-file host has the runtime built in.)
    ///    Folders are compared by their own name, the build hash: %TEMP% can be
    ///    the short 8.3 form (MOHAME~1.HAR) in one path and the long form in
    ///    another - comparing whole paths once took a live folder for unused.
    /// 2. A folder is renamed before it is deleted. Windows refuses the rename
    ///    while anything inside is open, so a folder still in use is never
    ///    half deleted; only a renamed one is.
    /// 3. Only folders named like a finished build (a 12-character build id):
    ///    never the temporary folder a starting server is unpacking into.
    /// 4. Only folders untouched for an hour.
    /// </summary>
    internal static class ExtractionCleanup
    {
        private const string Removing = ".removing";

        // What .NET names a finished unpacked build: 12 characters, letters,
        // digits, '-' and '_' (seen: qpFqUl5eSZFu, 6B4q-0G3tBYc).
        private static readonly System.Text.RegularExpressions.Regex BuildId =
            new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]{12}$");

        // Old builds only pile up across updates; waiting an hour costs nothing
        // and keeps well clear of any start in progress.
        private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

        public static void RemoveUnused()
        {
            try
            {
                var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
                var root = current.Parent;

                // Only inside the .NET extraction folder of this app - never
                // anywhere else, whatever BaseDirectory turns out to be.
                if (root == null || !root.Name.Equals("MHTools-N.AgentServer", StringComparison.OrdinalIgnoreCase)) return;
                if (root.Parent == null || !root.Parent.Name.Equals(".net", StringComparison.OrdinalIgnoreCase)) return;

                var inUse = BuildsInUse();
                inUse.Add(current.Name);

                foreach (var folder in root.GetDirectories())
                {
                    if (inUse.Contains(folder.Name)) continue;

                    // Guards 3 and 4. A server starting for the first time
                    // unpacks into a temporary folder (seen: "2640", "9bb0") and
                    // renames it to its build id when done. Deleting such a
                    // folder crashed the server unpacking into it (2.12.0, when
                    // Claude started several servers at once). So only a
                    // finished build - a 12-character build id, or our own
                    // ".removing" leftover - and only one untouched for an hour.
                    bool leftover = folder.Name.EndsWith(Removing, StringComparison.OrdinalIgnoreCase);
                    if (!leftover && !BuildId.IsMatch(folder.Name)) continue;
                    if (!leftover && DateTime.UtcNow - folder.LastWriteTimeUtc < MinimumAge) continue;

                    var target = folder.Name.EndsWith(Removing, StringComparison.OrdinalIgnoreCase)
                        ? folder.FullName
                        : folder.FullName + Removing;
                    try
                    {
                        if (target != folder.FullName) Directory.Move(folder.FullName, target);
                    }
                    catch (Exception)
                    {
                        continue;   // something in it is open: in use after all, leave it whole
                    }

                    try
                    {
                        Directory.Delete(target, true);
                        ServerLog.Info("Removed the unpacked copy of an older build: " + folder.Name);
                    }
                    catch (Exception ex)
                    {
                        ServerLog.Error("Could not finish removing " + target + "; tried again next start", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                ServerLog.Error("Cleaning up older unpacked builds skipped", ex);
            }
        }

        /// <summary>
    /// Ported from MHTools-R's ExtractionCleanup unchanged but for the names.
    ///The build-hash folder names running servers use.</summary>
        private static HashSet<string> BuildsInUse()
        {
            var builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcessesByName("MHTools-N.AgentServer"))
            {
                using (process)
                {
                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            if (string.Equals(module.ModuleName, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
                                builds.Add(new DirectoryInfo(Path.GetDirectoryName(module.FileName)).Name);
                        }
                    }
                    catch (Exception)
                    {
                        // A server whose modules cannot be read: its folder is
                        // unknown, so delete nothing at all this time.
                        throw new InvalidOperationException("a running server could not be inspected");
                    }
                }
            }
            return builds;
        }
    }
}
