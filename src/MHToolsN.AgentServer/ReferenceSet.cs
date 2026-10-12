using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MHToolsN.AgentServer
{
    /// <summary>
    /// What a script is compiled against: the Navisworks API assemblies of the very
    /// Navisworks that will run it (read from its process), and the .NET Framework 4.8
    /// Windows carries - Navisworks 2024-2027 all run on .NET Framework 4.8. Everything
    /// is already on the PC: nothing is downloaded. (As MHTools-R's ReferenceSet does for Revit.)
    /// </summary>
    internal sealed class ReferenceSet
    {
        private static readonly ConcurrentDictionary<string, ReferenceSet> Cache =
            new ConcurrentDictionary<string, ReferenceSet>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Navisworks API assemblies a script may use, when the install has them.</summary>
        private static readonly string[] NavisworksAssemblies =
        {
            "Autodesk.Navisworks.Api.dll", "Autodesk.Navisworks.Clash.dll", "Autodesk.Navisworks.Takeoff.dll",
            "Autodesk.Navisworks.Timeliner.dll", "Autodesk.Navisworks.ComApi.dll", "Autodesk.Navisworks.Interop.ComApi.dll"
        };

        private static readonly string[] FrameworkAssemblies =
        {
            "mscorlib.dll", "System.dll", "System.Core.dll", "System.Data.dll", "System.Xml.dll", "System.Xml.Linq.dll",
            "System.Drawing.dll", "System.Numerics.dll", "System.Windows.Forms.dll", "System.Xaml.dll", "Microsoft.CSharp.dll",
            "System.Web.Extensions.dll", "netstandard.dll",
            @"WPF\PresentationCore.dll", @"WPF\PresentationFramework.dll", @"WPF\WindowsBase.dll"
        };

        public IReadOnlyList<MetadataReference> References { get; private set; }
        public string NavisworksFolder { get; private set; }
        public bool HasTakeoff { get; private set; }
        public bool HasClash { get; private set; }

        /// <summary>For a live session: the Navisworks folder is read from its process; else the standard install folder.</summary>
        public static ReferenceSet ForSession(BridgeEndpoint endpoint)
        {
            string folder = null;
            try
            {
                using (var process = Process.GetProcessById(endpoint.processId))
                    folder = Path.GetDirectoryName(process.MainModule.FileName);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Could not read Navisworks' process for its folder; using the standard install folder", ex);
            }
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk",
                    "Navisworks Manage " + endpoint.navisworksVersion);
            return Cache.GetOrAdd(folder, Build);
        }

        private static ReferenceSet Build(string folder)
        {
            if (!File.Exists(Path.Combine(folder, "Autodesk.Navisworks.Api.dll")))
                throw new FileNotFoundException("Navisworks API assembly not found in " + folder);

            var files = new List<string>();
            foreach (string name in NavisworksAssemblies)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) files.Add(path);
            }
            string fx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319");
            foreach (string name in FrameworkAssemblies)
            {
                string path = Path.Combine(fx, name);
                if (File.Exists(path)) files.Add(path);
            }

            return new ReferenceSet
            {
                NavisworksFolder = folder,
                HasTakeoff = files.Any(f => f.EndsWith("Autodesk.Navisworks.Takeoff.dll", StringComparison.OrdinalIgnoreCase)),
                HasClash = files.Any(f => f.EndsWith("Autodesk.Navisworks.Clash.dll", StringComparison.OrdinalIgnoreCase)),
                References = files.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList()
            };
        }
    }
}
