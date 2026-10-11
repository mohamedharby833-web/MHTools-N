using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MHToolsN.AgentServer
{
    /// <summary>
    /// The MCP tools an AI app sees. When Navisworks answers Ok = false (the bridge
    /// stopped, no model open, Navisworks busy...) the tool fails as an MCP error
    /// with the code and message - never a "successful" call whose payload says it
    /// failed, which an agent could miss. On success the payload is the data itself.
    /// </summary>
    [McpServerToolType]
    public sealed class NavisTools
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly SessionClient _client;

        public NavisTools(SessionClient client)
        {
            _client = client;
        }

        [McpServerTool(Name = "list_navisworks_sessions", ReadOnly = true, Idempotent = true),
         Description("List the Navisworks sessions on this PC whose MHTools-N Agent Bridge is on: sessionId, Navisworks version, process id. " +
                     "With one session, every other tool finds it by itself and sessionId can be left empty.")]
        public string ListSessions()
        {
            var sessions = _client.Endpoints();
            if (sessions.Count == 0)
                throw new McpException("No Navisworks with the Agent Bridge on. Ask the user to click MHTools-N > Agent Bridge in Navisworks.");
            return Wire.Json(sessions.Select(e => new
            {
                sessionId = e.sessionId,
                product = e.product,
                navisworksVersion = e.navisworksVersion,
                apiVersion = e.apiVersion,
                addinVersion = e.addinVersion,
                processId = e.processId
            }));
        }

        [McpServerTool(Name = "get_context", ReadOnly = true, Idempotent = true),
         Description("What is open in Navisworks: the file, units, the appended models (file name and source path), " +
                     "how many items are selected, the counts of clash tests, saved sets and viewpoints, and whether Trusted C# is on. " +
                     "Call this first. All lengths the bridge reports are in the document's units unless a tool says otherwise.")]
        public async Task<string> Context(CancellationToken cancellationToken,
            [Description("From list_navisworks_sessions. Leave empty when only one Navisworks has the bridge on.")] string sessionId = "")
        {
            return await Call(sessionId, "get_context", new JsonObject(), cancellationToken);
        }

        [McpServerTool(Name = "get_execution", ReadOnly = true, Idempotent = true),
         Description("State of an operation a tool returned as still running (state 'running' with an operationId): " +
                     "running, completed (with its result) or failed. A timeout does not mean a change failed - check here before trying again.")]
        public async Task<string> Execution(string operationId, CancellationToken cancellationToken,
            [Description("Leave empty when only one Navisworks has the bridge on.")] string sessionId = "")
        {
            return await Call(sessionId, "get_operation", new JsonObject { ["operationId"] = operationId }, cancellationToken);
        }

        // ------------------------------------------------------------ plumbing

        private async Task<string> Call(string sessionId, string method, JsonObject arguments, CancellationToken ct)
        {
            BridgeEndpoint endpoint;
            try { endpoint = _client.Resolve(sessionId); }
            catch (InvalidOperationException ex) { throw new McpException(ex.Message); }

            var response = await _client.SendAsync(endpoint, method, arguments, null, Timeout, ct);
            if (!response.Ok) throw new McpException(response.Code + ": " + response.Message);
            return response.Data == null ? "null" : response.Data.ToJsonString(Wire.Options);
        }
    }
}
