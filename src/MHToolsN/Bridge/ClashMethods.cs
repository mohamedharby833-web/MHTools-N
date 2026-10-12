using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Clash Detective, read only: a summary for a model with hundreds of tests, the
    /// tests page by page, one test's clashes page by page, and a picture of one
    /// clash zoomed onto it (ClashImager, as Export Results uses).
    /// </summary>
    internal static class ClashMethods
    {
        private static readonly string[] Statuses = { "New", "Active", "Reviewed", "Approved", "Resolved" };

        private static DocumentClash Clash(Document doc)
        {
            try { return doc.GetClash(); }
            catch (Exception) { throw new BridgeException("NO_CLASH_DETECTIVE", "This Navisworks has no Clash Detective (Manage is needed)."); }
        }

        private static Dictionary<string, int> Counts(ClashTest test)
        {
            var counts = Statuses.ToDictionary(s => s, s => 0);
            foreach (ClashResult r in ClashResultsReader.Results(test))
            {
                string s = r.Status.ToString();
                counts[s] = counts.ContainsKey(s) ? counts[s] + 1 : 1;
            }
            return counts;
        }

        private static Dictionary<string, object> TestRow(Document doc, ClashTest test, HashSet<Guid> managed, double mmPerUnit)
        {
            var counts = Counts(test);
            int total = counts.Values.Sum();
            int open = counts["New"] + counts["Active"] + counts["Reviewed"];
            var row = new Dictionary<string, object>
            {
                { "name", test.DisplayName },
                { "status", test.Status.ToString() },
                { "type", test.TestType.ToString() },
                { "toleranceMm", Math.Round(test.Tolerance * mmPerUnit, 1) },
                { "lastRun", test.LastRun.HasValue ? test.LastRun.Value.ToString("yyyy-MM-dd HH:mm") : null },
                { "clashMatrix", NavisClashCreator.IsManagedTest(doc, test, managed) },
                { "total", total },
                { "open", open },
                { "byStatus", counts }
            };
            string priority = ClashResultsReader.PriorityText(test);
            if (!string.IsNullOrEmpty(priority)) row["priority"] = priority;
            return row;
        }

        // ---------------------------------------------------- get_clash_summary

        public static object Summary(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            Clash(doc);
            int top = BridgeWire.Int(args, "top", 15, 1, 100);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);
            HashSet<Guid> managed = NavisClashCreator.ManagedSetGuidsOf(doc);

            var tests = new List<Dictionary<string, object>>();
            foreach (ClashTest test in ClashResultsReader.AllTests(doc))
                tests.Add(TestRow(doc, test, managed, mmPerUnit));

            var byStatus = Statuses.ToDictionary(s => s, s => tests.Sum(t => ((Dictionary<string, int>)t["byStatus"])[s]));
            var testStatus = tests.GroupBy(t => (string)t["status"]).ToDictionary(g => g.Key, g => g.Count());
            return new Dictionary<string, object>
            {
                { "tests", tests.Count },
                { "clashMatrixTests", tests.Count(t => (bool)t["clashMatrix"]) },
                { "testsByStatus", testStatus },
                { "testsWithClashes", tests.Count(t => (int)t["total"] > 0) },
                { "testsNeverRun", tests.Count(t => t["lastRun"] == null) },
                { "clashes", tests.Sum(t => (int)t["total"]) },
                { "openClashes", tests.Sum(t => (int)t["open"]) },
                { "clashesByStatus", byStatus },
                { "topTestsByOpenClashes", tests.Where(t => (int)t["open"] > 0).OrderByDescending(t => (int)t["open"]).Take(top)
                    .Select(t => new Dictionary<string, object> { { "name", t["name"] }, { "open", t["open"] }, { "total", t["total"] }, { "status", t["status"] } })
                    .ToList() },
                { "note", "Open = New + Active + Reviewed. Use list_clash_tests to page through the tests, get_clash_results for one test's clashes." }
            };
        }

        // ----------------------------------------------------- list_clash_tests

        public static object Tests(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            Clash(doc);
            string filter = BridgeWire.Text(args, "filter");
            bool onlyClashes = BridgeWire.Bool(args, "withClashesOnly", false);
            bool onlyMatrix = BridgeWire.Bool(args, "clashMatrixOnly", false);
            int offset = BridgeWire.Int(args, "offset", 0, 0, int.MaxValue);
            int limit = BridgeWire.Int(args, "limit", 100, 1, 500);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);
            HashSet<Guid> managed = NavisClashCreator.ManagedSetGuidsOf(doc);

            var rows = new List<object>();
            int total = 0;
            foreach (ClashTest test in ClashResultsReader.AllTests(doc))
            {
                if (filter.Length > 0 && test.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (onlyMatrix && !NavisClashCreator.IsManagedTest(doc, test, managed)) continue;
                if (onlyClashes)
                {
                    bool any = false;
                    foreach (ClashResult r in ClashResultsReader.Results(test)) { any = true; break; }
                    if (!any) continue;
                }
                if (total++ < offset || rows.Count >= limit) continue;
                rows.Add(TestRow(doc, test, managed, mmPerUnit));
            }
            return ModelMethods.Page("tests", rows, total, offset, limit, null);
        }

        // ---------------------------------------------------- get_clash_results

        public static object Results(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            Clash(doc);
            ClashTest test = FindTest(doc, BridgeWire.Text(args, "test"));
            var statuses = new HashSet<string>(BridgeWire.Strings(args, "statuses"), StringComparer.OrdinalIgnoreCase);
            int offset = BridgeWire.Int(args, "offset", 0, 0, int.MaxValue);
            int limit = BridgeWire.Int(args, "limit", 50, 1, 200);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);

            var rows = new List<object>();
            int total = 0;
            foreach (SavedItem child in test.Children)
            {
                var group = child as ClashResultGroup;
                var results = new List<ClashResult>();
                if (group != null)
                {
                    foreach (SavedItem c in group.Children) if (c is ClashResult) results.Add((ClashResult)c);
                }
                else if (child is ClashResult)
                {
                    results.Add((ClashResult)child);
                }
                foreach (ClashResult r in results)
                {
                    if (statuses.Count > 0 && !statuses.Contains(r.Status.ToString())) continue;
                    if (total++ < offset || rows.Count >= limit) continue;
                    rows.Add(ResultRow(items, r, group, mmPerUnit));
                }
            }
            return ModelMethods.Page("clashes", rows, total, offset, limit, new Dictionary<string, object>
            {
                { "test", test.DisplayName },
                { "testStatus", test.Status.ToString() },
                { "toleranceMm", Math.Round(test.Tolerance * mmPerUnit, 1) }
            });
        }

        private static Dictionary<string, object> ResultRow(ItemRegistry items, ClashResult r, ClashResultGroup group, double mmPerUnit)
        {
            ModelItem one = r.CompositeItem1 ?? r.Item1, two = r.CompositeItem2 ?? r.Item2;
            var row = new Dictionary<string, object>
            {
                { "name", r.DisplayName },
                { "status", r.Status.ToString() },
                { "distanceMm", Math.Round(r.Distance * mmPerUnit, 1) },
                { "pointMeters", ModelMethods.Point(r.Center, mmPerUnit) },
                { "item1", one == null ? null : ModelMethods.Summary(items, one, true) },
                { "item2", two == null ? null : ModelMethods.Summary(items, two, true) },
                { "found", r.CreatedTime.HasValue ? r.CreatedTime.Value.ToString("yyyy-MM-dd") : null }
            };
            if (group != null) row["group"] = group.DisplayName;
            string assigned = ClashResultsReader.Who(r.AssignedTo);
            if (!string.IsNullOrEmpty(assigned)) row["assignedTo"] = assigned;
            if (!string.IsNullOrWhiteSpace(r.Description)) row["description"] = r.Description;
            string comments = ClashResultsReader.CommentsText(r);
            if (!string.IsNullOrEmpty(comments)) row["comments"] = comments;
            if (r.ApprovedTime.HasValue) row["approved"] = ClashResultsReader.Who(r.ApprovedBy) + " " + r.ApprovedTime.Value.ToString("yyyy-MM-dd");
            return row;
        }

        // ------------------------------------------------------ get_clash_image

        public static object Image(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            DocumentClash clash = Clash(doc);
            ClashTest test = FindTest(doc, BridgeWire.Text(args, "test"));
            string name = BridgeWire.Text(args, "clash");
            int width = BridgeWire.Int(args, "width", 800, 200, 1600);
            int height = BridgeWire.Int(args, "height", 600, 150, 1200);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);

            ClashResult found = null;
            ClashResultGroup inGroup = null;
            foreach (ClashResult r in ClashResultsReader.Results(test))
                if (string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase)) { found = r; inGroup = r.Parent as ClashResultGroup; break; }
            if (found == null) throw new BridgeException("NOT_FOUND", "Test '" + test.DisplayName + "' has no clash '" + name + "'. Names come from get_clash_results.");

            byte[] jpeg;
            int w, h;
            using (var imager = new ClashImager(doc, clash.TestsData, mmPerUnit))
            using (Bitmap bitmap = imager.Image(found, width, height))
            {
                if (bitmap == null) throw new BridgeException("FAILED", "Navisworks made no picture of this clash.");
                jpeg = ClashResultsReader.Jpeg(bitmap);
                w = bitmap.Width;
                h = bitmap.Height;
            }

            var result = ResultRow(items, found, inGroup, mmPerUnit);
            result["test"] = test.DisplayName;
            result["image"] = new Dictionary<string, object>
            {
                { "mimeType", "image/jpeg" }, { "width", w }, { "height", h }, { "base64", Convert.ToBase64String(jpeg) },
                { "legend", "Zoomed onto the clash from above a corner: item 1 red, item 2 green, the rest faded. A clash with its own saved viewpoint keeps Clash Detective's picture." }
            };
            return result;
        }

        // -------------------------------------------------------------- helpers

        internal static ClashTest FindTest(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("INVALID_ARGUMENT", "Give the test's name (from list_clash_tests).");
            var partial = new List<ClashTest>();
            foreach (ClashTest test in ClashResultsReader.AllTests(doc))
            {
                if (string.Equals(test.DisplayName, name, StringComparison.OrdinalIgnoreCase)) return test;
                if (test.DisplayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(test);
            }
            if (partial.Count == 1) return partial[0];
            if (partial.Count > 1)
                throw new BridgeException("AMBIGUOUS", partial.Count + " tests contain '" + name + "', e.g. " +
                    string.Join("; ", partial.Take(5).Select(t => t.DisplayName)) + ". Give the full name.");
            throw new BridgeException("NOT_FOUND", "No clash test '" + name + "'. Use list_clash_tests (filter) for the names.");
        }
    }
}
