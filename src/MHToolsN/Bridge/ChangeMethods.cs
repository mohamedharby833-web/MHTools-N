using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using MHToolsN.Model;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// The tools that change the model: saved sets and viewpoints, clash results,
    /// running tests, and the Clash Matrix. Every one previews by default
    /// (dryRun = true) and changes nothing then; with dryRun = false it goes through
    /// <see cref="ChangeGate"/> (Trusted C# on; in Ask mode the user says yes in
    /// Navisworks) and runs as one undo step. Nothing here saves the file.
    /// </summary>
    internal static class ChangeMethods
    {
        public const string AiSetsFolder = "AI Sets";
        public const string AiViewpointsFolder = "AI Viewpoints";
        private const int MaxResults = 2000;

        private static readonly string[] ResultStatuses = { "New", "Active", "Reviewed", "Approved", "Resolved" };

        private static bool DryRun(Dictionary<string, object> args)
        {
            return BridgeWire.Bool(args, "dryRun", true);
        }

        private static Dictionary<string, object> Preview(Dictionary<string, object> result, string what)
        {
            result["dryRun"] = true;
            result["wouldDo"] = what;
            result["next"] = "Nothing was changed. Show the user what would change; call again with dryRun=false to do it.";
            return result;
        }

        // ----------------------------------------------------------- create_set

        public static object CreateSet(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            string name = BridgeWire.Text(args, "name").Trim();
            string folder = BridgeWire.Text(args, "folder", AiSetsFolder).Trim().Trim('/');
            bool replace = BridgeWire.Bool(args, "replace", false);
            if (name.Length == 0) throw new BridgeException("INVALID_ARGUMENT", "Give the set a name.");
            if (folder.Split('/')[0].Equals(NavisClashCreator.FolderName, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("PROTECTED",
                    "The '" + NavisClashCreator.FolderName + "' folder belongs to the Clash Matrix tool (its tests are linked to those sets). " +
                    "Use another folder, or preview_clash_matrix / apply_clash_matrix to change it.");

            bool search = BridgeWire.Text(args, "category").Length > 0 || args.ContainsKey("conditions");
            SelectionSet set;
            int count;
            if (search)
            {
                Search s = ModelMethods.BuildSearch(doc, args);
                count = s.FindAll(doc, false).Count;
                set = new SelectionSet(s);
            }
            else
            {
                ModelItemCollection targets = ViewMethods.Targets(host.Items, doc, args);
                count = targets.Count;
                set = new SelectionSet(targets);
            }
            set.DisplayName = name;

            GroupItem parent = FindFolder(doc.SelectionSets.RootItem, folder);
            SelectionSet existing = parent == null ? null : Child<SelectionSet>(parent, name);
            if (existing != null && !replace)
                throw new BridgeException("EXISTS", "'" + folder + "/" + name + "' already exists. Give another name, or replace=true to overwrite it.");

            string kind = search ? "search set (finds new items as models are updated)" : "selection set of fixed items";
            string what = (existing != null ? "Replace" : "Create") + " the " + kind + " '" + folder + "/" + name + "' - it selects " + count + " item(s) now.";
            var result = new Dictionary<string, object> { { "set", folder + "/" + name }, { "kind", search ? "search" : "selection" }, { "items", count } };
            if (DryRun(args)) return Preview(result, what);

            ChangeGate.Allow(host, what);
            using (Transaction tx = doc.BeginTransaction("MHTools-N AI: set " + name))
            {
                parent = EnsureFolder(() => doc.SelectionSets.RootItem, folder, f => doc.SelectionSets.AddCopy(f), (g, f) => doc.SelectionSets.AddCopy(g, f));
                existing = Child<SelectionSet>(parent, name);
                if (existing != null)
                    doc.SelectionSets.ReplaceWithCopy(parent, parent.Children.IndexOf(existing), set);
                else
                    doc.SelectionSets.AddCopy(parent, set);
                tx.Commit();
            }
            result["done"] = true;
            return result;
        }

        // ------------------------------------------------------- save_viewpoint

        public static object SaveViewpoint(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            string name = BridgeWire.Text(args, "name").Trim();
            string folder = BridgeWire.Text(args, "folder", AiViewpointsFolder).Trim().Trim('/');
            if (name.Length == 0) throw new BridgeException("INVALID_ARGUMENT", "Give the viewpoint a name.");

            string what = "Save the current view as the viewpoint '" + folder + "/" + name + "'.";
            var result = new Dictionary<string, object> { { "viewpoint", folder + "/" + name } };
            if (DryRun(args)) return Preview(result, what);

            ChangeGate.Allow(host, what);
            using (Transaction tx = doc.BeginTransaction("MHTools-N AI: viewpoint " + name))
            {
                GroupItem parent = EnsureFolder(() => doc.SavedViewpoints.RootItem, folder, f => doc.SavedViewpoints.AddCopy(f), (g, f) => doc.SavedViewpoints.AddCopy(g, f));
                var vp = new SavedViewpoint(doc.CurrentViewpoint.CreateCopy()) { DisplayName = name };
                vp.MakeDisplayNameUnique(parent);
                doc.SavedViewpoints.AddCopy(parent, vp);
                result["viewpoint"] = folder + "/" + vp.DisplayName;
                tx.Commit();
            }
            result["done"] = true;
            return result;
        }

        // ----------------------------------------------------- set_clash_status

        public static object SetClashStatus(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            ClashTest test = ClashMethods.FindTest(doc, BridgeWire.Text(args, "test"));
            List<string> names = BridgeWire.Strings(args, "clashes");
            string fromStatus = BridgeWire.Text(args, "whereStatus");
            string status = BridgeWire.Text(args, "status");
            string assignTo = BridgeWire.Text(args, "assignTo");
            string comment = BridgeWire.Text(args, "comment");
            string description = BridgeWire.Text(args, "description");

            if (names.Count == 0 && fromStatus.Length == 0)
                throw new BridgeException("INVALID_ARGUMENT", "Give clashes (names from get_clash_results) or whereStatus (e.g. New) to act on all clashes with that status.");
            ClashResultStatus newStatus = default(ClashResultStatus);
            if (status.Length > 0 && !TryStatus(status, out newStatus))
                throw new BridgeException("INVALID_ARGUMENT", "status must be one of " + string.Join(", ", ResultStatuses) + ".");
            if (status.Length == 0 && assignTo.Length == 0 && comment.Length == 0 && description.Length == 0)
                throw new BridgeException("INVALID_ARGUMENT", "Give at least one of status, assignTo, comment, description.");

            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<ClashResult>();
            foreach (ClashResult r in ClashResultsReader.Results(test))
            {
                bool match = wanted.Count > 0 ? wanted.Contains(r.DisplayName) : string.Equals(r.Status.ToString(), fromStatus, StringComparison.OrdinalIgnoreCase);
                if (match) chosen.Add(r);
            }
            var missing = wanted.Where(n => !chosen.Any(r => string.Equals(r.DisplayName, n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (chosen.Count == 0) throw new BridgeException("NOT_FOUND", "No clash in '" + test.DisplayName + "' matches. Names come from get_clash_results.");
            if (chosen.Count > MaxResults) throw new BridgeException("TOO_MANY", chosen.Count + " clashes match; at most " + MaxResults + " per call.");

            var changes = new List<string>();
            if (status.Length > 0) changes.Add("status to " + newStatus);
            if (assignTo.Length > 0) changes.Add("assigned to " + assignTo);
            if (comment.Length > 0) changes.Add("a comment \"" + Short(comment) + "\"");
            if (description.Length > 0) changes.Add("description \"" + Short(description) + "\"");
            string what = "In clash test '" + test.DisplayName + "', set " + string.Join(", ", changes) + " on " + chosen.Count + " clash(es)" +
                          (chosen.Count <= 8 ? ": " + string.Join(", ", chosen.Select(r => r.DisplayName)) : "") + ".";

            var result = new Dictionary<string, object>
            {
                { "test", test.DisplayName },
                { "clashes", chosen.Count },
                { "byCurrentStatus", chosen.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()) }
            };
            if (missing.Count > 0) result["notFound"] = missing;
            if (DryRun(args)) return Preview(result, what);

            ChangeGate.Allow(host, what);
            DocumentClashTests tests = doc.GetClash().TestsData;
            using (Transaction tx = doc.BeginTransaction("MHTools-N AI: clash results"))
            {
                foreach (ClashResult r in chosen)
                {
                    if (status.Length > 0)
                    {
#if NAVIS2026 || NAVIS2027
                        tests.TestsEditResultStatus(r, newStatus, new Assignee(Environment.UserName));
#else
                        tests.TestsEditResultStatus(r, newStatus);
#endif
                    }
                    if (assignTo.Length > 0)
                    {
#if NAVIS2026 || NAVIS2027
                        tests.TestsEditResultAssignedTo(r, new Assignee(assignTo));
#else
                        tests.TestsEditResultAssignedTo(r, assignTo);
#endif
                    }
                    if (description.Length > 0) tests.TestsEditResultDescription(r, description);
                    if (comment.Length > 0)
                    {
                        var comments = new CommentCollection();
                        foreach (Comment c in r.Comments) comments.Add(c);
                        comments.Add(new Comment("AI: " + comment, CommentStatus.New));
                        tests.TestsEditResultComments(r, comments);
                    }
                }
                tx.Commit();
            }
            result["done"] = true;
            return result;
        }

        private static bool TryStatus(string text, out ClashResultStatus status)
        {
            foreach (ClashResultStatus s in Enum.GetValues(typeof(ClashResultStatus)))
                if (string.Equals(s.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase)) { status = s; return true; }
            status = default(ClashResultStatus);
            return false;
        }

        // ------------------------------------------------------ run_clash_tests

        public static object RunTests(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            List<string> names = BridgeWire.Strings(args, "tests");
            bool all = BridgeWire.Bool(args, "all", false);
            if (names.Count == 0 && !all) throw new BridgeException("INVALID_ARGUMENT", "Give tests (names from list_clash_tests) or all=true.");

            var tests = all ? ClashResultsReader.AllTests(doc).ToList() : names.Select(n => ClashMethods.FindTest(doc, n)).Distinct().ToList();
            string what = "Run " + tests.Count + " clash test(s)" + (tests.Count <= 8 ? ": " + string.Join(", ", tests.Select(t => t.DisplayName)) : "") +
                          ". Results are updated: new clashes appear, fixed ones become Resolved.";
            var result = new Dictionary<string, object> { { "tests", tests.Count } };
            if (DryRun(args)) return Preview(result, what);

            ChangeGate.Allow(host, what);
            DocumentClashTests data = doc.GetClash().TestsData;
            var started = DateTime.UtcNow;
            using (var progress = new NavisProgress("MHTools-N: running clash tests"))
            {
                int done = 0;
                foreach (ClashTest test in tests)
                {
                    progress.Report(done++ / (double)Math.Max(1, tests.Count));
                    data.TestsRunTest(test);
                }
            }
            result["done"] = true;
            result["seconds"] = (int)(DateTime.UtcNow - started).TotalSeconds;
            result["next"] = "get_clash_summary or list_clash_tests show the new results.";
            return result;
        }

        // ------------------------------------------- preview / apply_clash_matrix

        public static object PreviewMatrix(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            var plan = Plan(doc, args);
            return plan.Report(BridgeWire.Int(args, "limit", 60, 0, 2000));
        }

        public static object ApplyMatrix(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            var plan = Plan(doc, args);
            bool removeStale = BridgeWire.Bool(args, "removeStale", false);
            string what = "Clash Matrix (" + (plan.BestPractice ? "best practice" : "all categories") + ", default tolerance " + plan.ToleranceMm + " mm): " +
                          "create or update " + plan.Matrix.Categories.Count + " search sets in the '" + NavisClashCreator.FolderName + "' folder, and " +
                          "add " + plan.Tests.ToAdd + ", update " + plan.Tests.ToUpdate + ", rename " + plan.Tests.ToRename + " clash test(s)" +
                          (removeStale && plan.Tests.Stale.Count > 0 ? ", and remove " + plan.Tests.Stale.Count + " Clash Matrix test(s) the matrix no longer has" : "") +
                          ". Other tests and sets are not touched.";
            var report = plan.Report(0);
            if (DryRun(args)) return Preview(report, what);

            ChangeGate.Allow(host, what);
            var warnings = new List<string>(plan.Warnings);
            SetSyncResult sets;
            TestApplyResult tests;
            using (var progress = new NavisProgress("MHTools-N: Clash Matrix search sets"))
                sets = NavisClashCreator.EnsureSearchSets(doc, plan.Matrix, true, warnings, progress);
            using (var progress = new NavisProgress("MHTools-N: Clash Matrix tests"))
                tests = NavisClashCreator.ApplyTests(doc, plan.Matrix, plan.ToleranceMm, removeStale, warnings, progress);

            return new Dictionary<string, object>
            {
                { "done", true },
                { "sets", new Dictionary<string, object> { { "created", sets.Created }, { "updated", sets.Updated }, { "unchanged", sets.Unchanged }, { "removed", tests.SetsRemoved } } },
                { "tests", new Dictionary<string, object> { { "added", tests.Added }, { "updated", tests.Updated }, { "renamed", tests.Renamed }, { "unchanged", tests.Unchanged }, { "removed", tests.Removed }, { "skipped", tests.Skipped } } },
                { "warnings", warnings.Take(30).ToList() },
                { "next", "Sets and tests are two undo steps (Ctrl+Z twice). run_clash_tests runs the new tests." }
            };
        }

        private sealed class MatrixPlan
        {
            public bool BestPractice;
            public double ToleranceMm;
            public List<ModelInfo> Models;
            public MatrixParseResult Matrix;
            public TestPlan Tests;
            public List<string> Warnings;

            public Dictionary<string, object> Report(int limit)
            {
                return new Dictionary<string, object>
                {
                    { "mode", BestPractice ? "best practice" : "all categories" },
                    { "defaultToleranceMm", ToleranceMm },
                    { "models", Models.Select(m => new Dictionary<string, object> { { "file", m.FileName }, { "discipline", m.Discipline }, { "categories", m.Categories.Count } }).ToList() },
                    { "sets", Matrix.Categories.Count },
                    { "setList", Matrix.Categories.Take(limit).Select(s => new Dictionary<string, object>
                        {
                            { "name", s },
                            { "discipline", Matrix.Disciplines.ContainsKey(s) ? Matrix.Disciplines[s] : "" },
                            { "categories", Matrix.SetMembers.ContainsKey(s) ? string.Join(", ", Matrix.SetMembers[s]) : s }
                        }).ToList() },
                    { "tests", Matrix.Tests.Count },
                    { "testList", Matrix.Tests.Take(limit).Select(t => (object)new Dictionary<string, object>
                        {
                            { "name", t.Name }, { "priority", t.Priority }, { "toleranceMm", t.ToleranceFromSheet ? (object)t.ToleranceMm : "default" }
                        }).ToList() },
                    { "plan", new Dictionary<string, object>
                        {
                            { "add", Tests.ToAdd }, { "update", Tests.ToUpdate }, { "rename", Tests.ToRename }, { "unchanged", Tests.Unchanged },
                            { "staleClashMatrixTests", Tests.Stale.Count }, { "nameConflicts", Tests.Conflicts.Count }, { "otherTestsNotTouched", Tests.OtherTests }
                        } },
                    { "warnings", Warnings.Take(30).ToList() }
                };
            }
        }

        /// <summary>What the Clash Matrix window's Scan Model + Create would do, as it does it.</summary>
        private static MatrixPlan Plan(Document doc, Dictionary<string, object> args)
        {
            try { doc.GetClash(); }
            catch (Exception) { throw new BridgeException("NO_CLASH_DETECTIVE", "This Navisworks has no Clash Detective (Manage is needed)."); }

            var plan = new MatrixPlan
            {
                BestPractice = BridgeWire.Bool(args, "bestPractice", true),
                ToleranceMm = BridgeWire.Int(args, "toleranceMm", 10, 0, 10000),
                Warnings = new List<string>()
            };
            using (var progress = new NavisProgress("MHTools-N: scanning model categories"))
                plan.Models = NavisClashCreator.ScanModels(doc, plan.Warnings, progress);
            plan.Matrix = NavisClashCreator.BuildFullMatrix(plan.Models, plan.ToleranceMm, plan.BestPractice).Effective();
            plan.Warnings.AddRange(plan.Matrix.Warnings);
            plan.Tests = NavisClashCreator.PlanTests(doc, plan.Matrix.Tests, plan.ToleranceMm);
            return plan;
        }

        // -------------------------------------------------------------- folders

        private static T Child<T>(GroupItem parent, string name) where T : SavedItem
        {
            foreach (SavedItem item in parent.Children)
                if (item is T && string.Equals(item.DisplayName, name, StringComparison.OrdinalIgnoreCase)) return (T)item;
            return null;
        }

        private static GroupItem FindFolder(GroupItem root, string path)
        {
            GroupItem current = root;
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Child<FolderItem>(current, part.Trim());
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>
        /// The folder at this path (Folder/Sub), made where missing. AddCopy adds a
        /// COPY, and a tree read before it does not show it - so every level is found
        /// again from a freshly read root, as NavisClashCreator does for its folder.
        /// </summary>
        private static GroupItem EnsureFolder(Func<GroupItem> root, string path, Action<FolderItem> addAtRoot, Action<GroupItem, FolderItem> addIn)
        {
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            for (int depth = 0; depth < parts.Length; depth++)
            {
                GroupItem parent = FindFolder(root(), string.Join("/", parts.Take(depth)));
                if (parent == null) throw new BridgeException("FAILED", "Navisworks did not create the folder '" + string.Join("/", parts.Take(depth)) + "'.");
                if (Child<FolderItem>(parent, parts[depth]) != null) continue;
                var folder = new FolderItem { DisplayName = parts[depth] };
                if (depth == 0) addAtRoot(folder);
                else addIn(parent, folder);
            }
            GroupItem made = FindFolder(root(), path);
            if (made == null) throw new BridgeException("FAILED", "Navisworks did not create the folder '" + path + "'.");
            return made;
        }

        private static string Short(string text)
        {
            return text.Length <= 80 ? text : text.Substring(0, 80) + "...";
        }
    }
}
