using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MHToolsN.AgentServer
{
    public sealed class CompiledScript
    {
        public string Id;
        public string SessionId;
        public string Body;
        public string SourceHash;
        public byte[] Assembly;
    }

    /// <summary>
    /// Turns an agent's C# method body into an assembly in memory - nothing written
    /// to disk, nothing run here. The bytes go to Navisworks, which runs them on its
    /// UI thread (MHToolsN.Bridge.ScriptRunner). Ported from MHTools-R's ScriptCompiler.
    /// </summary>
    public sealed class ScriptCompiler
    {
        private const int MaxScripts = 200;

        /// <summary>The class and method the add-in looks for.</summary>
        public const string TypeName = "MHToolsN.Generated.Script";
        public const string MethodName = "Run";

        private readonly ConcurrentDictionary<string, CompiledScript> _scripts = new ConcurrentDictionary<string, CompiledScript>();
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);

        public CompiledScript Resolve(string id, string sessionId)
        {
            CompiledScript script;
            if (string.IsNullOrEmpty(id) || !_scripts.TryGetValue(id, out script) || script.SessionId != sessionId)
                throw new InvalidOperationException("Compiled script not found in this server and session. Compile it again; never replay an uncertain change automatically.");
            return script;
        }

        public async Task<object> CompileAsync(BridgeEndpoint endpoint, string body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body) || body.Length > 65536)
                throw new ArgumentException("The C# method body must have 1 to 65536 characters.");

            await _serial.WaitAsync(ct);
            try
            {
                if (_scripts.Count >= MaxScripts) throw new InvalidOperationException("Compiler capacity of " + MaxScripts + " scripts reached. Restart the AI app.");

                ReferenceSet refs = ReferenceSet.ForSession(endpoint);
                var result = Compile(body, refs);
                if (result.Bytes == null)
                {
                    ServerLog.Info("Compile failed for session " + endpoint.sessionId + ": " + FirstLine(result.Diagnostics));
                    return new { ok = false, diagnostics = result.Diagnostics };
                }

                string id = Guid.NewGuid().ToString("N");
                _scripts[id] = new CompiledScript { Id = id, SessionId = endpoint.sessionId, Body = body, SourceHash = result.SourceHash, Assembly = result.Bytes };
                ServerLog.Info("Compiled script " + id + " (" + result.Bytes.Length + " bytes) against " + refs.NavisworksFolder + ".");
                return new
                {
                    ok = true,
                    scriptId = id,
                    compiledAgainst = refs.NavisworksFolder,
                    takeoffApi = refs.HasTakeoff,
                    diagnostics = result.Diagnostics,
                    note = "Not run yet. execute_code runs it in Navisworks (Trusted C# must be on)."
                };
            }
            finally
            {
                _serial.Release();
            }
        }

        internal sealed class Result
        {
            public byte[] Bytes;
            public string Diagnostics;
            public string SourceHash;
        }

        internal static Result Compile(string body, ReferenceSet refs)
        {
            string source =
                "using System;\nusing System.Linq;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.IO;\n" +
                "using Autodesk.Navisworks.Api;\n" +
                (refs.HasClash ? "using Autodesk.Navisworks.Api.Clash;\n" : "") +
                (refs.HasTakeoff ? "using Autodesk.Navisworks.Api.Takeoff;\nusing Autodesk.Navisworks.Api.Data;\n" : "") +
                "namespace MHToolsN.Generated { public static class Script { " +
                "public static string Run(Document doc, string inputsJson, CancellationToken cancellationToken) {\n" +
                "#line 1 \"agent-code.cs\"\n" + body + "\n} } }";

            // Latest language version: a feature .NET Framework 4.8 cannot support is a
            // compile error against its assemblies here, not a failure in Navisworks.
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "agent-code.cs");
            var compilation = CSharpCompilation.Create(
                "AgentScript_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                refs.References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    platform: Platform.X64,
                    nullableContextOptions: NullableContextOptions.Disable));

            using (var output = new MemoryStream())
            {
                var emit = compilation.Emit(output);
                string text = string.Join("\n", emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
                    .Select(d => d.ToString()));
                if (text.Length > 16000) text = text.Substring(0, 16000);

                var result = new Result { Diagnostics = text, SourceHash = Hash(Encoding.UTF8.GetBytes(source)) };
                if (emit.Success)
                {
                    byte[] bytes = output.ToArray();
                    if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Generated assembly exceeds 1 MiB.");
                    result.Bytes = bytes;
                }
                return result;
            }
        }

        public static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(no diagnostics)";
            int i = text.IndexOf('\n');
            return i < 0 ? text : text.Substring(0, i);
        }
    }
}
