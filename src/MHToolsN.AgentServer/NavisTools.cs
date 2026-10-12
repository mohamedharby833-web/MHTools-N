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
            string model = "", int limit = 100,
            [Description("Full rows (type, level, file...). Default: full up to 50 items, compact (id, name, category, element ID) above.")] bool? details = null,
            string sessionId = "")
        {
            var args = new JsonObject { ["category"] = category, ["model"] = model, ["limit"] = limit };
            if (details.HasValue) args["details"] = details.Value;
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
        public async Task<string> Selection(CancellationToken cancellationToken, int limit = 100, bool? details = null, string sessionId = "")
        {
            var args = new JsonObject { ["limit"] = limit };
            if (details.HasValue) args["details"] = details.Value;
            return await Call(sessionId, "get_selection", args, cancellationToken);
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
        public async Task<string> SetItems(string set, CancellationToken cancellationToken, int limit = 100, bool? details = null, string sessionId = "")
        {
            var args = new JsonObject { ["set"] = set, ["limit"] = limit };
            if (details.HasValue) args["details"] = details.Value;
            return await Call(sessionId, "get_set_items", args, cancellationToken);
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

        // ========================================================= view changes

        private const string TargetsHelp =
            "What to act on - exactly one of: itemIds (ids from the read tools), set (a saved set's path or name), " +
            "useSelection=true (what the user has selected), or clashTest + clash (that clash's two items).";

        private static JsonObject Targets(string[] itemIds, string set, bool useSelection, string clashTest, string clash)
        {
            return new JsonObject
            {
                ["itemIds"] = new JsonArray((itemIds ?? new string[0]).Select(i => (JsonNode)i).ToArray()),
                ["set"] = set ?? "",
                ["useSelection"] = useSelection,
                ["clashTest"] = clashTest ?? "",
                ["clash"] = clash ?? ""
            };
        }

        [McpServerTool(Name = "select_items", Idempotent = true),
         Description("Select items in Navisworks, so the user sees them highlighted (and Navisworks' own tools act on them). " + TargetsHelp +
                     " add=true adds to the current selection instead of replacing it. Changes the view only.")]
        public async Task<string> SelectItems(CancellationToken cancellationToken, string[] itemIds = null, string set = "", bool useSelection = false,
            string clashTest = "", string clash = "", bool add = false, string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["add"] = add;
            return await Call(sessionId, "select_items", args, cancellationToken);
        }

        [McpServerTool(Name = "zoom_to_items", Idempotent = true),
         Description("Zoom the Navisworks view onto items, keeping the view's direction, and select them (select=false not to). " + TargetsHelp +
                     " Follow with capture_view to look. Changes the view only.")]
        public async Task<string> ZoomToItems(CancellationToken cancellationToken, string[] itemIds = null, string set = "", bool useSelection = false,
            string clashTest = "", string clash = "", bool select = true, string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["select"] = select;
            return await Call(sessionId, "zoom_to_items", args, cancellationToken);
        }

        [McpServerTool(Name = "set_visibility"),
         Description("Hide and show model items. action: isolate (hide everything else), hide, show, or show_all (unhide everything; needs no targets). " +
                     TargetsHelp + " Changes what the view shows; the coordination data (sets, tests, results) is not touched.")]
        public async Task<string> SetVisibility(string action, CancellationToken cancellationToken, string[] itemIds = null, string set = "",
            bool useSelection = false, string clashTest = "", string clash = "", string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["action"] = action;
            return await Call(sessionId, "set_visibility", args, cancellationToken);
        }

        [McpServerTool(Name = "color_items"),
         Description("Colour items and/or make them see-through in the view - temporary, not saved with the file. color: #RRGGBB or a name " +
                     "(red, green, blue, yellow, orange, purple, cyan, magenta, white, black, grey, teal). transparency: 0 (solid) to 100. " +
                     TargetsHelp + " reset_appearance takes it off.")]
        public async Task<string> ColorItems(CancellationToken cancellationToken, string color = "", int transparency = -1, string[] itemIds = null,
            string set = "", bool useSelection = false, string clashTest = "", string clash = "", string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["color"] = color;
            args["transparency"] = transparency;
            return await Call(sessionId, "color_items", args, cancellationToken);
        }

        [McpServerTool(Name = "reset_appearance"),
         Description("Take temporary colours and transparency off: all=true for the whole model, or the given targets. " + TargetsHelp)]
        public async Task<string> ResetAppearance(CancellationToken cancellationToken, bool all = false, string[] itemIds = null, string set = "",
            bool useSelection = false, string clashTest = "", string clash = "", string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["all"] = all;
            return await Call(sessionId, "reset_appearance", args, cancellationToken);
        }

        [McpServerTool(Name = "go_to_viewpoint", Idempotent = true),
         Description("Show a saved viewpoint in Navisworks (its camera, and its hidden items and colours if it saved them). viewpoint: path from list_viewpoints, or the name.")]
        public async Task<string> GoToViewpoint(string viewpoint, CancellationToken cancellationToken, string sessionId = "")
        {
            return await Call(sessionId, "go_to_viewpoint", new JsonObject { ["viewpoint"] = viewpoint }, cancellationToken);
        }

        // ======================================================== model changes

        private const string ChangeHelp =
            " dryRun=true (the default) only previews: nothing changes - show the user what would happen. dryRun=false makes the change, " +
            "which needs Trusted C# switched on in Navisworks; in Ask mode Navisworks asks the user first (the call then reports 'running': " +
            "poll get_execution, and on REJECTED_BY_USER do not try again without asking). Each change is one Ctrl+Z step; the file is never saved.";

        [McpServerTool(Name = "create_set"),
         Description("Save a set in the Sets window. Search set (keeps finding new items as models are updated): give category and/or conditions " +
                     "and model, exactly as for find_items. Selection set (fixed items): give targets instead. " + TargetsHelp +
                     " folder: default 'AI Sets' (Folder/Sub allowed; never the 'Clash Matrix' folder, which the Clash Matrix tool owns). " +
                     "replace=true overwrites a set of the same name." + ChangeHelp)]
        public async Task<string> CreateSet(string name, CancellationToken cancellationToken, string folder = "AI Sets",
            string category = "", string conditions = "", string model = "",
            string[] itemIds = null, string set = "", bool useSelection = false, string clashTest = "", string clash = "",
            bool replace = false, bool dryRun = true, string sessionId = "")
        {
            var args = Targets(itemIds, set, useSelection, clashTest, clash);
            args["name"] = name;
            args["folder"] = folder;
            args["replace"] = replace;
            args["dryRun"] = dryRun;
            if (!string.IsNullOrWhiteSpace(category)) args["category"] = category;
            if (!string.IsNullOrWhiteSpace(model)) args["model"] = model;
            if (!string.IsNullOrWhiteSpace(conditions))
            {
                JsonNode parsed;
                try { parsed = JsonNode.Parse(conditions); }
                catch (Exception ex) { throw new McpException("conditions is not valid JSON: " + ex.Message); }
                args["conditions"] = parsed is JsonArray ? parsed : new JsonArray(parsed);
            }
            return await Call(sessionId, "create_set", args, cancellationToken);
        }

        [McpServerTool(Name = "save_viewpoint"),
         Description("Save the current Navisworks view as a viewpoint, in folder (default 'AI Viewpoints'). Set the view up first " +
                     "(zoom_to_items, set_visibility, color_items)." + ChangeHelp)]
        public async Task<string> SaveViewpoint(string name, CancellationToken cancellationToken, string folder = "AI Viewpoints",
            bool dryRun = true, string sessionId = "")
        {
            return await Call(sessionId, "save_viewpoint", new JsonObject { ["name"] = name, ["folder"] = folder, ["dryRun"] = dryRun }, cancellationToken);
        }

        [McpServerTool(Name = "set_clash_status"),
         Description("Change clash results in one test: status (New, Active, Reviewed, Approved, Resolved), assignTo (a person), " +
                     "comment (added, marked 'AI:'), description. Which clashes: clashes (names from get_clash_results), or whereStatus " +
                     "to take every clash with that status (e.g. whereStatus=New). At most 2000 per call. Judge clashes before approving them - " +
                     "get_clash_image shows each one." + ChangeHelp)]
        public async Task<string> SetClashStatus(string test, CancellationToken cancellationToken, string[] clashes = null, string whereStatus = "",
            string status = "", string assignTo = "", string comment = "", string description = "", bool dryRun = true, string sessionId = "")
        {
            return await Call(sessionId, "set_clash_status", new JsonObject
            {
                ["test"] = test,
                ["clashes"] = new JsonArray((clashes ?? new string[0]).Select(c => (JsonNode)c).ToArray()),
                ["whereStatus"] = whereStatus, ["status"] = status, ["assignTo"] = assignTo,
                ["comment"] = comment, ["description"] = description, ["dryRun"] = dryRun
            }, cancellationToken);
        }

        [McpServerTool(Name = "run_clash_tests"),
         Description("Run clash tests in Clash Detective: tests (names from list_clash_tests) or all=true. Updates their results - new clashes appear, " +
                     "fixed ones become Resolved. A large run takes minutes and holds Navisworks: the call then reports 'running' - poll get_execution." + ChangeHelp)]
        public async Task<string> RunClashTests(CancellationToken cancellationToken, string[] tests = null, bool all = false,
            bool dryRun = true, string sessionId = "")
        {
            return await Call(sessionId, "run_clash_tests", new JsonObject
            {
                ["tests"] = new JsonArray((tests ?? new string[0]).Select(t => (JsonNode)t).ToArray()),
                ["all"] = all, ["dryRun"] = dryRun
            }, cancellationToken);
        }

        [McpServerTool(Name = "preview_clash_matrix", ReadOnly = true, Idempotent = true),
         Description("What the MHTools-N Clash Matrix tool would build from the open model, without changing anything: the appended files and their " +
                     "disciplines (AR, ST, MEC, EL, ICT...), the search sets (one per discipline and Revit category), the clash tests " +
                     "('No - DISC CATEGORY - DISC CATEGORY', priority and tolerance from the built-in clash standard), and the plan against the model " +
                     "(tests to add, update, rename, unchanged). bestPractice=true keeps each discipline to its own categories; false gives every " +
                     "category in every file a set. toleranceMm: the default tolerance. limit: how many sets and tests to list.")]
        public async Task<string> PreviewClashMatrix(CancellationToken cancellationToken, bool bestPractice = true, int toleranceMm = 10,
            int limit = 60, string sessionId = "")
        {
            return await Call(sessionId, "preview_clash_matrix", new JsonObject
            {
                ["bestPractice"] = bestPractice, ["toleranceMm"] = toleranceMm, ["limit"] = limit
            }, cancellationToken);
        }

        [McpServerTool(Name = "apply_clash_matrix"),
         Description("Do what preview_clash_matrix showed - the Clash Matrix window's Create Clash Tests: create or update the search sets in the " +
                     "'Clash Matrix' folder and add, update or rename the Clash Matrix tests (results are kept). removeStale=true also removes " +
                     "Clash Matrix tests the matrix no longer has. Tests that are not the Clash Matrix's are never touched. Then run_clash_tests." + ChangeHelp)]
        public async Task<string> ApplyClashMatrix(CancellationToken cancellationToken, bool bestPractice = true, int toleranceMm = 10,
            bool removeStale = false, bool dryRun = true, string sessionId = "")
        {
            return await Call(sessionId, "apply_clash_matrix", new JsonObject
            {
                ["bestPractice"] = bestPractice, ["toleranceMm"] = toleranceMm, ["removeStale"] = removeStale, ["dryRun"] = dryRun
            }, cancellationToken);
        }

        // ======================================================= Quantification

        private static JsonObject TakeoffArgs(string[] categories, string[] disciplines, string model)
        {
            return new JsonObject
            {
                ["categories"] = new JsonArray((categories ?? new string[0]).Select(c => (JsonNode)c).ToArray()),
                ["disciplines"] = new JsonArray((disciplines ?? new string[0]).Select(d => (JsonNode)d).ToArray()),
                ["model"] = model ?? ""
            };
        }

        [McpServerTool(Name = "get_takeoff_info", ReadOnly = true, Idempotent = true),
         Description("Navisworks Quantification (quantity takeoff) in this file: whether it is set up (Project Setup done - needed before anything can be " +
                     "taken off), its tables with row counts, and the variables each holds (Length, Area, Volume, Count..., input and calculated).")]
        public async Task<string> TakeoffInfo(CancellationToken cancellationToken, string sessionId = "")
        {
            return await Call(sessionId, "get_takeoff_info", new JsonObject(), cancellationToken);
        }

        [McpServerTool(Name = "preview_takeoff", ReadOnly = true, Idempotent = true),
         Description("What apply_takeoff would add to Quantification, without changing anything. The structure: an item group per discipline " +
                     "(from the file: AR, ST, MEC, EL, ICT...), one per Revit category under it, one item per Revit type, holding that type's elements. " +
                     "Navisworks works out Length, Area, Volume and Count from the elements. Elements already taken off are counted, not added again. " +
                     "categories: only these Revit categories (e.g. [\"Ducts\",\"Pipes\"]); disciplines: only files of these (e.g. [\"MEC\"]); " +
                     "model: only files whose name contains this. Non-physical categories (rooms, grids, levels, annotation) are left out. " +
                     "limit: how many items (types) to list.")]
        public async Task<string> PreviewTakeoff(CancellationToken cancellationToken, string[] categories = null, string[] disciplines = null,
            string model = "", int limit = 80, string sessionId = "")
        {
            var args = TakeoffArgs(categories, disciplines, model);
            args["limit"] = limit;
            return await Call(sessionId, "preview_takeoff", args, cancellationToken);
        }

        [McpServerTool(Name = "apply_takeoff"),
         Description("Fill in Quantification from the model, as preview_takeoff showed: create the item groups and items that are missing (existing ones " +
                     "with the same name are reused) and take off the elements not yet taken off. Quantification must be set up first (get_takeoff_info). " +
                     "Same filters as preview_takeoff. A large takeoff takes a while: the call may report 'running' - poll get_execution." + ChangeHelp)]
        public async Task<string> ApplyTakeoff(CancellationToken cancellationToken, string[] categories = null, string[] disciplines = null,
            string model = "", bool dryRun = true, string sessionId = "")
        {
            var args = TakeoffArgs(categories, disciplines, model);
            args["dryRun"] = dryRun;
            return await Call(sessionId, "apply_takeoff", args, cancellationToken);
        }

        [McpServerTool(Name = "get_takeoff_summary", ReadOnly = true, Idempotent = true),
         Description("The quantities in Quantification now: per item, its group path (Discipline / Category), the number of objects and the sums of " +
                     "its calculated quantities (Length, Area, Volume, Count...), in the units chosen at Project Setup. filter: group or item contains this.")]
        public async Task<string> TakeoffSummary(CancellationToken cancellationToken, string filter = "", int limit = 200, string sessionId = "")
        {
            return await Call(sessionId, "get_takeoff_summary", new JsonObject { ["filter"] = filter, ["limit"] = limit }, cancellationToken);
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
