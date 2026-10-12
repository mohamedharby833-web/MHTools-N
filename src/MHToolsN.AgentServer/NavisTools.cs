using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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

        // ================================================================ model

        [McpServerTool(Name = "get_model_tree", ReadOnly = true, Idempotent = true),
         Description("Browse the selection tree. No itemId: the appended models (their root items). With an itemId: that item's children. " +
                     "Each item has an id (e.g. 'n12') for the other tools, its name, node type (File, Layer, Category, Family, Type, Instance...), " +
                     "Revit category when it has one, and hasChildren. Paged: offset / limit (default 100, max 500). " +
                     "To find elements by category or property, find_items is much faster than walking the tree.")]
        public async Task<string> ModelTree(CancellationToken cancellationToken,
            [Description("An item id from this or another tool; empty for the models.")] string itemId = "",
            int offset = 0, int limit = 100, string sessionId = "")
        {
            return await Call(sessionId, "get_model_tree", new JsonObject { ["itemId"] = itemId, ["offset"] = offset, ["limit"] = limit }, cancellationToken);
        }

        [McpServerTool(Name = "find_items", ReadOnly = true, Idempotent = true),
         Description("Find model elements, as Navisworks' Find Items does. category: a Revit category (Walls, Ducts, Pipes, Cable Trays, Structural Framing...) - " +
                     "matched on Element > Category in any Navisworks language. conditions: extra property tests, ANDed, each " +
                     "{\"category\":\"Element\",\"property\":\"Type\",\"value\":\"...\",\"match\":\"equals|notEquals|contains|wildcard|exists\"} " +
                     "with category/property as display names in the Properties window (e.g. Item/Name, Element/Level, Element ID/Value); text matches ignore case. " +
                     "model: only appended files whose name contains this (e.g. '1M0' for mechanical). " +
                     "Returns the total found and up to limit items (default 100, max 1000) with id, name, category, type, Revit element ID, level and file.")]
        public async Task<string> FindItems(CancellationToken cancellationToken,
            string category = "",
            [Description("JSON array of conditions, see the tool description. Empty for none.")] string conditions = "",
            string model = "", int limit = 100, string sessionId = "")
        {
            var args = new JsonObject { ["category"] = category, ["model"] = model, ["limit"] = limit };
            if (!string.IsNullOrWhiteSpace(conditions))
            {
                JsonNode parsed;
                try { parsed = JsonNode.Parse(conditions); }
                catch (Exception ex) { throw new McpException("conditions is not valid JSON: " + ex.Message); }
                args["conditions"] = parsed is JsonArray ? parsed : new JsonArray(parsed);
            }
            return await Call(sessionId, "find_items", args, cancellationToken);
        }

        [McpServerTool(Name = "get_item_properties", ReadOnly = true, Idempotent = true),
         Description("All properties of up to 20 items, grouped by property category (Item, Element, Element ID, Revit Type, Phase...), " +
                     "with the tree path and the bounding box in meters. categories: only these property categories (display names), to keep it short.")]
        public async Task<string> ItemProperties(
            [Description("Item ids, e.g. [\"n12\",\"n40\"] or \"n12,n40\".")] string[] itemIds,
            CancellationToken cancellationToken,
            [Description("Optional property category names to keep.")] string[] categories = null,
            string sessionId = "")
        {
            return await Call(sessionId, "get_item_properties", new JsonObject
            {
                ["itemIds"] = new JsonArray((itemIds ?? new string[0]).Select(i => (JsonNode)i).ToArray()),
                ["categories"] = new JsonArray((categories ?? new string[0]).Select(i => (JsonNode)i).ToArray())
            }, cancellationToken);
        }

        [McpServerTool(Name = "get_selection", ReadOnly = true, Idempotent = true),
         Description("The items the user has selected in Navisworks now (total, and up to limit with id, name, category, type, element ID, level, file). " +
                     "Use it when the user says 'this', 'these' or 'what I selected'.")]
        public async Task<string> Selection(CancellationToken cancellationToken, int limit = 100, string sessionId = "")
        {
            return await Call(sessionId, "get_selection", new JsonObject { ["limit"] = limit }, cancellationToken);
        }

        [McpServerTool(Name = "list_sets", ReadOnly = true, Idempotent = true),
         Description("The saved selection and search sets (Sets window): name, path (Folder/Name), kind (search or selection), and whether it is " +
                     "one of the Clash Matrix sets the MHTools-N Clash Matrix tool manages. countItems=true also counts what each set finds (slower). " +
                     "folder: only the sets under this folder path.")]
        public async Task<string> Sets(CancellationToken cancellationToken, bool countItems = false, string folder = "", string sessionId = "")
        {
            return await Call(sessionId, "list_sets", new JsonObject { ["countItems"] = countItems, ["folder"] = folder }, cancellationToken);
        }

        [McpServerTool(Name = "get_set_items", ReadOnly = true, Idempotent = true),
         Description("The items a saved set selects (a search set is run now): total, and up to limit with ids and details. set: its path from list_sets, or its name when unique.")]
        public async Task<string> SetItems(string set, CancellationToken cancellationToken, int limit = 100, string sessionId = "")
        {
            return await Call(sessionId, "get_set_items", new JsonObject { ["set"] = set, ["limit"] = limit }, cancellationToken);
        }

        [McpServerTool(Name = "list_viewpoints", ReadOnly = true, Idempotent = true),
         Description("The saved viewpoints: name and folder path. filter: only those whose path contains this text.")]
        public async Task<string> Viewpoints(CancellationToken cancellationToken, string filter = "", int limit = 300, string sessionId = "")
        {
            return await Call(sessionId, "list_viewpoints", new JsonObject { ["filter"] = filter, ["limit"] = limit }, cancellationToken);
        }

        // ================================================================ clash

        [McpServerTool(Name = "get_clash_summary", ReadOnly = true, Idempotent = true),
         Description("Clash Detective at a glance - start here for any clash question: the number of tests (and how many the MHTools-N Clash Matrix made), " +
                     "tests by status, clashes by status (New, Active, Reviewed, Approved, Resolved), open clashes (New+Active+Reviewed) " +
                     "and the tests with the most open clashes. Reads every result once: a few seconds on a large model.")]
        public async Task<string> ClashSummary(CancellationToken cancellationToken, int top = 15, string sessionId = "")
        {
            return await Call(sessionId, "get_clash_summary", new JsonObject { ["top"] = top }, cancellationToken);
        }

        [McpServerTool(Name = "list_clash_tests", ReadOnly = true, Idempotent = true),
         Description("Clash tests page by page: name, status, type, tolerance (mm), last run, priority (Navisworks 2026+), Clash Matrix or not, " +
                     "and clash counts by status. filter: name contains (e.g. 'MEC DUCTS'). withClashesOnly / clashMatrixOnly narrow the list. " +
                     "offset / limit (default 100, max 500).")]
        public async Task<string> ClashTests(CancellationToken cancellationToken, string filter = "", bool withClashesOnly = false,
            bool clashMatrixOnly = false, int offset = 0, int limit = 100, string sessionId = "")
        {
            return await Call(sessionId, "list_clash_tests", new JsonObject
            {
                ["filter"] = filter, ["withClashesOnly"] = withClashesOnly, ["clashMatrixOnly"] = clashMatrixOnly, ["offset"] = offset, ["limit"] = limit
            }, cancellationToken);
        }

        [McpServerTool(Name = "get_clash_results", ReadOnly = true, Idempotent = true),
         Description("The clashes of one test, page by page: name, status, distance (mm, negative = penetration), clash point (meters), group, " +
                     "both items (id, name, category, type, Revit element ID, level, file), found date, assigned to, description, comments. " +
                     "test: full name, or a part of it that matches one test. statuses: e.g. [\"New\",\"Active\"]. offset / limit (default 50, max 200).")]
        public async Task<string> ClashResults(string test, CancellationToken cancellationToken,
            string[] statuses = null, int offset = 0, int limit = 50, string sessionId = "")
        {
            return await Call(sessionId, "get_clash_results", new JsonObject
            {
                ["test"] = test,
                ["statuses"] = new JsonArray((statuses ?? new string[0]).Select(s => (JsonNode)s).ToArray()),
                ["offset"] = offset, ["limit"] = limit
            }, cancellationToken);
        }

        [McpServerTool(Name = "get_clash_image", ReadOnly = true, Idempotent = true),
         Description("A picture of one clash, zoomed onto it from above a corner, item 1 red and item 2 green, the rest of the model faded - " +
                     "look at it to judge the clash. Also returns the clash's details. Uses the Navisworks view for a moment and puts it back. " +
                     "test and clash names from get_clash_results.")]
        public async Task<CallToolResult> ClashImage(string test, string clash, CancellationToken cancellationToken,
            int width = 800, int height = 600, string sessionId = "")
        {
            return Picture(await Call(sessionId, "get_clash_image", new JsonObject
            {
                ["test"] = test, ["clash"] = clash, ["width"] = width, ["height"] = height
            }, cancellationToken));
        }

        // ================================================================= view

        [McpServerTool(Name = "capture_view", ReadOnly = true, Idempotent = true),
         Description("A picture of what the Navisworks view shows right now (as the user sees it), to look at the model with the user.")]
        public async Task<CallToolResult> CaptureView(CancellationToken cancellationToken, int width = 1024, int height = 768, string sessionId = "")
        {
            return Picture(await Call(sessionId, "capture_view", new JsonObject { ["width"] = width, ["height"] = height }, cancellationToken));
        }

        /// <summary>Turns a payload with an "image" (base64) into an MCP image plus the rest as text.</summary>
        private static CallToolResult Picture(string json)
        {
            var node = JsonNode.Parse(json) as JsonObject;
            var result = new CallToolResult { Content = new List<ContentBlock>() };
            if (node == null || !(node["image"] is JsonObject image) || image["base64"] == null)
            {
                // Still running in Navisworks (state 'running'), or no picture: hand back what came.
                result.Content.Add(new TextContentBlock { Text = json });
                return result;
            }
            byte[] bytes = Convert.FromBase64String((string)image["base64"]);
            string mime = (string)image["mimeType"] ?? "image/jpeg";
            image.Remove("base64");
            result.Content.Add(ImageContentBlock.FromBytes(bytes, mime));
            result.Content.Add(new TextContentBlock { Text = node.ToJsonString(Wire.Options) });
            return result;
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
