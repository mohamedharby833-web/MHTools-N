using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using MHToolsN.Services;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace MHToolsN.Bridge
{
    /// <summary>Trusted C#: off, on with a question before each change, or on without asking.</summary>
    internal enum CodeMode { Off, Ask, Auto }

    /// <summary>
    /// One running Agent Bridge in this Navisworks: the session file the server
    /// finds it by, the pipe it listens on, and the dispatcher that carries each
    /// request to Navisworks' UI thread. Created by the Agent Bridge button,
    /// disposed by it again or when Navisworks closes.
    /// </summary>
    internal sealed class BridgeHost : IDisposable
    {
        public static readonly string SessionFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MHTools-N", "sessions");

        /// <summary>How long a request may wait for Navisworks before it is abandoned (or reported as still running).</summary>
        private static readonly TimeSpan UiWait = TimeSpan.FromSeconds(20);

        /// <summary>Finished operations are kept this long for get_execution, then forgotten.</summary>
        private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(30);

        private readonly Dictionary<string, object> _endpoint;
        private readonly string _sessionId;
        private readonly string _token;
        private readonly string _endpointFile;
        private readonly UiDispatcher _dispatcher;
        private readonly BridgePipe _pipe;
        private readonly ConcurrentDictionary<string, UiDispatcher.Work> _operations = new ConcurrentDictionary<string, UiDispatcher.Work>();

        private volatile bool _disposed;
        private volatile CodeMode _codeMode = CodeMode.Off;

        public BridgeHost()
        {
            Process process = Process.GetCurrentProcess();
            _sessionId = Guid.NewGuid().ToString("N");
            _token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

            var version = NavisApp.Version;
            _endpoint = new Dictionary<string, object>
            {
                { "protocolVersion", BridgeWire.ProtocolVersion },
                { "sessionId", _sessionId },
                { "pipeName", "MHToolsN.AgentBridge." + _sessionId },
                { "token", _token },
                { "processId", process.Id },
                { "processStartUtcTicks", process.StartTime.ToUniversalTime().Ticks },
                { "product", string.IsNullOrWhiteSpace(version.RuntimeProductName) ? "Navisworks" : version.RuntimeProductName },
                { "navisworksVersion", NavisworksYear.ToString() },
                { "apiVersion", version.ApiMajor + "." + version.ApiMinor },
                { "addinVersion", UI.MhTheme.Version },
                { "framework", "net48" }
            };

            string folder = PrepareSessionFolder();
            _endpointFile = Path.Combine(folder, _sessionId + ".json");

            _dispatcher = new UiDispatcher();
            _pipe = new BridgePipe((string)_endpoint["pipeName"], Handle);
            try
            {
                File.WriteAllText(_endpointFile + ".tmp", BridgeWire.Json(_endpoint));
                File.Move(_endpointFile + ".tmp", _endpointFile);
                _pipe.Start();
            }
            catch
            {
                Dispose();
                throw;
            }

            Watchdog.Log("Bridge started. Session " + _sessionId + ", " + _endpoint["product"] + " " + _endpoint["navisworksVersion"] +
                         " (API " + _endpoint["apiVersion"] + "), add-in " + _endpoint["addinVersion"] + ".");
        }

        /// <summary>2024..2027: the API's major version is 21 for 2024.</summary>
        public static int NavisworksYear
        {
            get { return NavisApp.Version.ApiMajor + 2003; }
        }

        public string SessionId { get { return _sessionId; } }

        /// <summary>Ids for the model items handed to agents; used on the UI thread only.</summary>
        public ItemRegistry Items { get; } = new ItemRegistry();

        public CodeMode Mode
        {
            get { return _codeMode; }
            set
            {
                _codeMode = value;
                Watchdog.Log(value == CodeMode.Off ? "Bridge: Trusted C# switched off."
                    : value == CodeMode.Ask ? "Bridge: Trusted C# switched ON - ask before each change."
                    : "Bridge: Trusted C# switched ON - Auto (changes kept without asking).");
            }
        }

        public string ModeName
        {
            get { return _codeMode == CodeMode.Ask ? "ask" : _codeMode == CodeMode.Auto ? "auto" : "off"; }
        }

        // ---------------------------------------------------------------- setup

        /// <summary>
        /// The session folder, readable by this Windows user only (it holds the
        /// pipe's token), cleared of files left by Navisworks sessions that are gone.
        /// </summary>
        private static string PrepareSessionFolder()
        {
            var info = Directory.CreateDirectory(SessionFolder);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new InvalidOperationException("Missing Windows identity.");
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);

            // A crashed Navisworks never deletes its file; this is the one place
            // that knows to clear them.
            foreach (string file in Directory.GetFiles(SessionFolder, "*.json"))
            {
                try
                {
                    var old = BridgeWire.Parse(File.ReadAllText(file));
                    if (!IsAlive(old)) File.Delete(file);
                }
                catch (Exception) { }
            }
            return SessionFolder;
        }

        private static bool IsAlive(Dictionary<string, object> endpoint)
        {
            try
            {
                int pid = Convert.ToInt32(endpoint["processId"]);
                long ticks = Convert.ToInt64(endpoint["processStartUtcTicks"]);
                using (var process = Process.GetProcessById(pid))
                    return process.StartTime.ToUniversalTime().Ticks == ticks;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------- pipe thread (no API)

        private Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            if (_disposed) return BridgeWire.Error("STOPPED", "The Agent Bridge was stopped in Navisworks.");

            if (Convert.ToInt32(request.ContainsKey("protocolVersion") ? request["protocolVersion"] : 0) != BridgeWire.ProtocolVersion)
                return BridgeWire.Error("PROTOCOL", "The bridge server and the MHTools-N add-in are different versions. Install one MHTools-N MSI for both.");
            if (BridgeWire.Text(request, "sessionId") != _sessionId || !FixedTimeEquals(BridgeWire.Text(request, "token"), _token))
                return BridgeWire.Error("UNAUTHORIZED", "Wrong session or token.");

            string method = BridgeWire.Text(request, "method");
            string operationId = BridgeWire.Text(request, "operationId");
            Dictionary<string, object> args = BridgeWire.Object(request, "arguments");
            Forget();

            // Answered here, without Navisworks: they only read the bridge's own state.
            if (method == "ping")
                return BridgeWire.Success(new Dictionary<string, object> { { "sessionId", _sessionId }, { "codeMode", ModeName } });
            if (method == "get_operation")
                return Operation(BridgeWire.Text(args, "operationId"));

            Func<object> body = BridgeMethods.Find(method, this, args);
            if (body == null) return BridgeWire.Error("UNKNOWN_METHOD", "This MHTools-N add-in has no method '" + method + "'. Install the same MHTools-N MSI for the server and the add-in.");

            UiDispatcher.Work work;
            try { work = _dispatcher.Run(body, UiWait); }
            catch (BridgeException ex) { return BridgeWire.Error(ex.Code, ex.Message); }

            if (!work.IsDone)
            {
                if (string.IsNullOrEmpty(operationId)) operationId = Guid.NewGuid().ToString();
                _operations[operationId] = work;
                return BridgeWire.Success(new Dictionary<string, object>
                {
                    { "state", "running" },
                    { "operationId", operationId },
                    { "nextTool", "get_execution" },
                    { "message", "Still working in Navisworks. Poll get_execution with this operationId; do not repeat the request." }
                });
            }
            return Outcome(work);
        }

        private Dictionary<string, object> Operation(string operationId)
        {
            UiDispatcher.Work work;
            if (string.IsNullOrEmpty(operationId) || !_operations.TryGetValue(operationId, out work))
                return BridgeWire.Error("NOT_FOUND", "No operation " + operationId + " in this Navisworks session. Only work that was still running is kept, for " +
                                                     (int)KeepFinished.TotalMinutes + " minutes.");
            if (!work.IsDone)
                return BridgeWire.Success(new Dictionary<string, object>
                {
                    { "state", "running" },
                    { "operationId", operationId },
                    { "seconds", (int)(DateTime.UtcNow - work.StartedUtc).TotalSeconds }
                });

            var outcome = Outcome(work);
            if (!(bool)outcome["ok"]) return outcome;
            return BridgeWire.Success(new Dictionary<string, object>
            {
                { "state", "completed" },
                { "operationId", operationId },
                { "result", outcome["data"] }
            });
        }

        private static Dictionary<string, object> Outcome(UiDispatcher.Work work)
        {
            if (work.Error == null) return BridgeWire.Success(work.Result);
            var known = work.Error as BridgeException;
            if (known != null) return BridgeWire.Error(known.Code, known.Message);
            Watchdog.LogException("Bridge method", work.Error);
            return BridgeWire.Error("FAILED", work.Error.GetType().Name + ": " + work.Error.Message);
        }

        private void Forget()
        {
            DateTime cutoff = DateTime.UtcNow - KeepFinished;
            foreach (var pair in _operations.ToList())
                if (pair.Value.IsDone && pair.Value.FinishedUtc < cutoff)
                {
                    UiDispatcher.Work removed;
                    _operations.TryRemove(pair.Key, out removed);
                }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ------------------------------------------------------------- shutdown

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _codeMode = CodeMode.Off;
            try { if (_pipe != null) _pipe.Dispose(); } catch (Exception) { }
            try { if (_endpointFile != null && File.Exists(_endpointFile)) File.Delete(_endpointFile); } catch (Exception) { }
            try { if (_dispatcher != null) _dispatcher.Dispose(); } catch (Exception) { }
            Watchdog.Log("Bridge stopped. Session " + _sessionId + ".");
        }
    }
}
