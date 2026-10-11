using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MHToolsN.AgentServer
{
    /// <summary>
    /// Finds the Navisworks sessions whose Agent Bridge is on (one file each in
    /// the session folder) and sends them requests over their pipe.
    /// </summary>
    public sealed class SessionClient
    {
        public static readonly string SessionFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MHTools-N", "sessions");

        private const string NotRunning =
            "No Navisworks with the Agent Bridge on. In Navisworks, click MHTools-N > Agent Bridge (it then reads 'Agent Bridge On'), then list sessions again.";

        public IReadOnlyList<BridgeEndpoint> Endpoints()
        {
            var found = new List<BridgeEndpoint>();
            if (!Directory.Exists(SessionFolder)) return found;

            foreach (var file in Directory.EnumerateFiles(SessionFolder, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > 16384) continue;
                    var endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(file));
                    if (endpoint == null) continue;

                    // A file whose Navisworks is gone - or whose process id now
                    // belongs to some other program - is not a session.
                    using (var process = Process.GetProcessById(endpoint.processId))
                    {
                        if (endpoint.protocolVersion == Wire.ProtocolVersion &&
                            process.StartTime.ToUniversalTime().Ticks == endpoint.processStartUtcTicks &&
                            process.ProcessName.Equals("Roamer", StringComparison.OrdinalIgnoreCase))
                            found.Add(endpoint);
                    }
                }
                catch (Exception) { }
            }
            return found;
        }

        /// <summary>
        /// The session asked for - or, with no id, the only one there is. Asking for
        /// an id first is a step most conversations never need: there is almost
        /// always one Navisworks open.
        /// </summary>
        public BridgeEndpoint Resolve(string sessionId)
        {
            var all = Endpoints();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (all.Count == 1) return all[0];
                if (all.Count == 0) throw new InvalidOperationException(NotRunning);
                throw new InvalidOperationException(all.Count + " Navisworks sessions have the bridge on: call list_navisworks_sessions and pass a sessionId.");
            }
            var endpoint = all.SingleOrDefault(e => e.sessionId == sessionId);
            if (endpoint == null)
                throw new InvalidOperationException("Session not found - Navisworks was closed or its bridge stopped. " + NotRunning);
            return endpoint;
        }

        public async Task<BridgeResponse> SendAsync(BridgeEndpoint endpoint, string method, JsonObject arguments, string operationId,
            TimeSpan timeout, CancellationToken ct)
        {
            var request = new JsonObject
            {
                ["protocolVersion"] = Wire.ProtocolVersion,
                ["sessionId"] = endpoint.sessionId,
                ["token"] = endpoint.token,
                ["operationId"] = operationId ?? Guid.NewGuid().ToString(),
                ["method"] = method,
                ["arguments"] = arguments ?? new JsonObject()
            };

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", endpoint.pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await pipe.ConnectAsync(deadline.Token);
                        await Wire.WriteAsync(pipe, request, deadline.Token);
                        return BridgeResponse.FromJson(await Wire.ReadAsync(pipe, deadline.Token));
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return BridgeResponse.Error("TIMEOUT",
                        "Navisworks did not answer in " + (int)timeout.TotalSeconds + " s. It may be busy (a dialog open, a long command). " +
                        "A change asked for may still happen: check with get_execution before trying again.");
                }
            }
        }
    }
}
