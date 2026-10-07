using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Creates search sets (from the model's own categories) and clash tests in the
    /// open Navisworks document.
    ///
    /// Search sets are flat (no folders), one per unique category present across all
    /// appended files, named by the plain category name. Clash tests are linked to
    /// those sets by name.
    /// </summary>
    public static class NavisClashCreator
    {
        /// <summary>
        /// Categories that should be folded into their parent category. E.g. "Cable
        /// Tray Fittings" items become part of the "Cable Trays" search set. Add more
        /// entries here as needed.
        /// </summary>
        private static readonly Dictionary<string, string> CategoryMergeMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Cable Tray Fittings", "Cable Trays" },
                { "Pipe Fittings", "Pipes" },
                { "Flex Pipes", "Pipes" },
                { "Duct Fittings", "Ducts" },
                { "Conduit Fittings", "Conduits" },
            };

        /// <summary>Maps a category to its main (parent) category, or itself if not merged.</summary>
        public static string GetMainCategory(string category)
        {
            string c = string.IsNullOrEmpty(category) ? "" : category.Trim();
            return CategoryMergeMap.TryGetValue(c, out string parent) ? parent : c;
        }

        /// <summary>
        /// Enumerates the unique model categories (the Revit category names, e.g.
        /// "Doors", "Walls", "Ducts") across every appended file. Categories are the
        /// tree nodes whose ClassDisplayName is "Category"; their DisplayName is the
        /// real category name. Non-clash annotation categories are filtered out.
        /// </summary>
        public static List<string> GetModelCategories(Document document)
        {
            var categories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int modelIndex = 0;
            foreach (Autodesk.Navisworks.Api.Model model in document.Models)
            {
                Watchdog.Log("MODEL[" + modelIndex + "] file=" + model.FileName);
                CollectCategories(model.RootItem, categories);
                modelIndex++;
            }

            // Annotation / non-clash categories we never want in a clash matrix.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "<Room Separation>", "Center line", "Center Line", "Lines",
                "Grids", "Levels", "Reference Points", "Reference Planes", "Scope Boxes"
            };
            var result = categories.Where(c => !skip.Contains(c)).ToList();

            Watchdog.Log("Model categories found: " + result.Count);
            foreach (string c in result)
                Watchdog.Log("  CAT: " + c);

            return result;
        }

        private static void CollectCategories(ModelItem root, SortedSet<string> categories)
        {
            if (root == null)
                return;

            var queue = new Queue<Tuple<ModelItem, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            int visited = 0;

            while (queue.Count > 0 && visited < 2000000)
            {
                var t = queue.Dequeue();
                ModelItem item = t.Item1;
                int depth = t.Item2;
                visited++;

                // A "Category" node's DisplayName is the actual category name
                // (e.g. "Doors"). ClassDisplayName is just the generic "Category".
                if (item.ClassDisplayName == "Category" && !string.IsNullOrWhiteSpace(item.DisplayName))
                    categories.Add(GetMainCategory(item.DisplayName));

                // Log a small tree sample so the hierarchy/depth can be inspected.
                if (visited <= 120)
                {
                    Watchdog.Log("TREE[" + visited + "] depth=" + depth
                        + " classDisplay='" + item.ClassDisplayName + "'"
                        + " className='" + item.ClassName + "'"
                        + " display='" + item.DisplayName + "'");
                }

                // Categories sit in the top few levels of the tree, above the leaves.
                if (depth < 4)
                {
                    foreach (ModelItem child in item.Children)
                        queue.Enqueue(Tuple.Create(child, depth + 1));
                }
            }
        }

        /// <summary>
        /// Builds a full (all-pairs) clash matrix result from a list of category
        /// names - one test per category pair. Used when the add-in generates the
        /// matrix itself from the model.
        /// </summary>
        public static MatrixParseResult BuildFullMatrix(IList<string> categories, double toleranceMm)
        {
            var result = new MatrixParseResult { BatchName = "Clash Matrix" };
            result.Categories.AddRange(categories);
            for (int i = 0; i < categories.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    result.Tests.Add(new ClashTestDefinition
                    {
                        LeftName = categories[i],
                        RightName = categories[j],
                        Name = categories[i] + "-vs-" + categories[j],
                        ToleranceMm = toleranceMm,
                        Priority = 1
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Creates one flat search set per category name, matching that category
        /// across all appended files. Sets that already exist are left alone.
        /// Returns the number created.
        /// </summary>
        public static int CreateSearchSets(Document document, IEnumerable<string> categoryNames,
            List<string> warnings)
        {
            // Build a map of category name -> its tree nodes (one per appended file).
            var nodes = new Dictionary<string, List<ModelItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (Autodesk.Navisworks.Api.Model model in document.Models)
                CollectCategoryNodes(model.RootItem, nodes);

            int created = 0;
            int logged = 0;
            foreach (string name in categoryNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (FindSet(document, name) != null)
                    continue; // already exists

                if (!nodes.TryGetValue(name, out var list) || list.Count == 0)
                {
                    warnings.Add("Category not found in model: " + name);
                    continue;
                }

                // Create a static selection set holding the category nodes themselves;
                // Navisworks expands them to their geometry when the set is used.
                var collection = new ModelItemCollection();
                foreach (ModelItem node in list)
                    collection.Add(node);

                var set = new SelectionSet(collection) { DisplayName = name };
                document.SelectionSets.AddCopy(set); // flat - no folder
                created++;

                if (logged < 20)
                {
                    long descendants = 0;
                    foreach (ModelItem node in list)
                        descendants += CountDescendants(node);
                    Watchdog.Log("SET '" + name + "' files=" + list.Count + " descendant-items=" + descendants);
                    logged++;
                }
            }
            return created;
        }

        private static void CollectCategoryNodes(ModelItem root, Dictionary<string, List<ModelItem>> map)
        {
            if (root == null)
                return;

            var queue = new Queue<ModelItem>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                ModelItem item = queue.Dequeue();
                if (item.ClassDisplayName == "Category" && !string.IsNullOrWhiteSpace(item.DisplayName))
                {
                    string key = GetMainCategory(item.DisplayName);
                    if (!map.TryGetValue(key, out var list))
                    {
                        list = new List<ModelItem>();
                        map[key] = list;
                    }
                    list.Add(item);
                }
                foreach (ModelItem child in item.Children)
                    queue.Enqueue(child);
            }
        }

        private static long CountDescendants(ModelItem node)
        {
            long count = 0;
            var queue = new Queue<ModelItem>();
            foreach (ModelItem child in node.Children)
                queue.Enqueue(child);
            while (queue.Count > 0)
            {
                ModelItem item = queue.Dequeue();
                count++;
                foreach (ModelItem child in item.Children)
                    queue.Enqueue(child);
            }
            return count;
        }

        /// <summary>Removes every clash test currently in the document.</summary>
        public static void ClearTests(Document document)
        {
            document.GetClash().TestsData.TestsClear();
        }

        /// <summary>Returns the names of every flat (root-level) search set currently in the document.</summary>
        public static List<string> GetExistingSetNames(Document document)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedItem item in document.SelectionSets.Value)
            {
                var set = item as SelectionSet;
                if (set != null && !string.IsNullOrWhiteSpace(set.DisplayName))
                    names.Add(set.DisplayName);
            }
            return names.ToList();
        }

        /// <summary>
        /// Re-packs the search sets: removes the existing sets for the given categories
        /// and recreates them from the current model. Use after appending new files.
        /// Returns the number of sets (re)created.
        /// </summary>
        public static int UpdateSearchSets(Document document, IEnumerable<string> categoryNames,
            List<string> warnings)
        {
            int removed = 0;
            foreach (string name in categoryNames)
            {
                SelectionSet existing = FindSet(document, name);
                if (existing != null)
                {
                    document.SelectionSets.Remove(existing);
                    removed++;
                }
            }
            Watchdog.Log("Search sets removed (for update): " + removed);
            return CreateSearchSets(document, categoryNames, warnings);
        }

        /// <summary>
        /// Creates every clash test whose left/right search sets exist in the
        /// document. Returns the number created; missing sets are appended to
        /// <paramref name="warnings"/>.
        /// </summary>
        public static int CreateTests(Document document, IList<ClashTestDefinition> tests,
            List<string> warnings)
        {
            DocumentClash clash = document.GetClash();
            DocumentClashTests testsData = clash.TestsData;
            string units = GetUnitString(document);

            int created = 0;

#if !NAVIS2026 && !NAVIS2027
            // Priority was added to the API in Navisworks 2026; on 2024/2025 the
            // matrix values cannot be applied, so say so once rather than silently.
            bool anyPriority = false;
            foreach (ClashTestDefinition td in tests) { if (td.Priority != 0) { anyPriority = true; break; } }
            if (anyPriority)
                warnings.Add("Clash test priority is ignored in this Navisworks version (Priority was added in Navisworks 2026).");
#endif

            foreach (ClashTestDefinition t in tests)
            {
                SelectionSet left = FindSet(document, t.LeftName);
                SelectionSet right = FindSet(document, t.RightName);

                if (left == null)
                {
                    warnings.Add("Search set not found: " + t.LeftName);
                    continue;
                }
                if (right == null)
                {
                    warnings.Add("Search set not found: " + t.RightName);
                    continue;
                }

                var test = new ClashTest
                {
                    DisplayName = t.Name,
                    Tolerance = UnitConverter.MmToUnit(t.ToleranceMm, units),
                    MergeComposites = true,
                    TestType = ClashTestType.Hard
                };

#if NAVIS2026 || NAVIS2027
                // ClashTest.Priority was added to the API in Navisworks 2026.
                test.Priority = t.Priority;
#endif

                test.SelectionA.SelfIntersect = false;
                test.SelectionA.PrimitiveTypes = PrimitiveTypes.Triangles;
                test.SelectionA.Selection.SelectionSources.Add(
                    document.SelectionSets.CreateSelectionSource(left));

                test.SelectionB.SelfIntersect = false;
                test.SelectionB.PrimitiveTypes = PrimitiveTypes.Triangles;
                test.SelectionB.Selection.SelectionSources.Add(
                    document.SelectionSets.CreateSelectionSource(right));

                // TestsAddCopy takes the parent folder + the new test; top-level
                // tests hang off the clash tests root folder.
                testsData.TestsAddCopy(testsData.Value.TestsRoot, test);
                created++;
            }

            return created;
        }

        /// <summary>Returns the category names referenced by the tests but missing from the document.</summary>
        public static List<string> FindMissingSets(Document document, IList<ClashTestDefinition> tests)
        {
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ClashTestDefinition t in tests)
            {
                if (FindSet(document, t.LeftName) == null)
                    missing.Add(t.LeftName);
                if (FindSet(document, t.RightName) == null)
                    missing.Add(t.RightName);
            }
            return missing.ToList();
        }

        /// <summary>
        /// Re-runs each search set's search (across all appended models) and returns
        /// a per-set item count. Use this after appending new models. Returns one
        /// report line per set, with the total last.
        /// </summary>
        public static List<string> RefreshSets(Document document, IEnumerable<string> categoryNames)
        {
            var report = new List<string>();
            int total = 0;
            foreach (string name in categoryNames)
            {
                SelectionSet set = FindSet(document, name);
                if (set == null || !set.HasSearch)
                {
                    report.Add(name + ": not found");
                    continue;
                }

                ModelItemCollection found = set.Search.FindAll(document, false);
                int count = found != null ? found.Count() : 0;
                total += count;
                report.Add(name + ": " + count + " items");
            }
            report.Add("TOTAL: " + total + " items across " + categoryNames.Count() + " sets");
            return report;
        }

        /// <summary>Finds a flat (root-level) search set by display name, case-insensitively.</summary>
        public static SelectionSet FindSet(Document document, string name)
        {
            foreach (SavedItem item in document.SelectionSets.Value)
            {
                var set = item as SelectionSet;
                if (set != null && string.Equals(set.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return set;
            }
            return null;
        }

        public static string GetUnitString(Document document)
        {
            switch (document.Units)
            {
                case Autodesk.Navisworks.Api.Units.Meters: return "m";
                case Autodesk.Navisworks.Api.Units.Centimeters: return "cm";
                case Autodesk.Navisworks.Api.Units.Millimeters: return "mm";
                case Autodesk.Navisworks.Api.Units.Inches: return "in";
                case Autodesk.Navisworks.Api.Units.Feet: return "ft";
                default: return "ft";
            }
        }
    }
}
