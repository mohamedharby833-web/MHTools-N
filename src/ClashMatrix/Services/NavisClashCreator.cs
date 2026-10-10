using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Creates search sets and clash tests in the open Navisworks document.
    ///
    /// The add-in's search sets live in a folder named <see cref="FolderName"/> in the
    /// Sets window (one set per category / matrix group). Clash Detective has no test
    /// folders, so its tests (one per matrix pair) sit at the top level, and a test is
    /// the add-in's when both its sides are linked to sets in that folder. Sets
    /// elsewhere and tests linked to anything else are never changed.
    ///
    /// The sets are real search sets on the Revit "Element > Category" property, so
    /// a newly appended file is picked up without recreating anything.
    /// </summary>
    public static class NavisClashCreator
    {
        /// <summary>Name of the selection-set folder the add-in owns.</summary>
        public const string FolderName = "Clash Matrix";

        // Revit "Element > Category" property. Internal names do not change with the
        // Navisworks UI language; the display names are a fallback for exporters that
        // write only those.
        private const string ElementCategoryInternal = "LcRevitData_Element";
        private const string CategoryPropertyInternal = "LcRevitPropertyElementCategory";
        private const string ElementCategoryDisplay = "Element";
        private const string CategoryPropertyDisplay = "Category";

        // Revit NWC tree: File > [Link] > Category > Family > Type > Instance. Category
        // nodes are the first LcRevitCollection on the way down.
        private const string RevitCollectionClass = "LcRevitCollection";
        private const string CategoryClassDisplay = "Category";
        private const int MaxScanDepth = 8;

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
                { "Pipe Accessories", "Pipes" },
                { "Pipe Insulations", "Pipes" },
                { "Flex Pipes", "Pipes" },
                { "Duct Fittings", "Ducts" },
                { "Duct Accessories", "Ducts" },
                { "Duct Insulations", "Ducts" },
                { "Flex Ducts", "Ducts" },
                { "Conduit Fittings", "Conduits" },
                { "Wall Sweeps", "Walls" },
                { "Curtain Panels", "Curtain Walls" },
                { "Curtain Wall Mullions", "Curtain Walls" },
                { "Curtain Systems", "Curtain Walls" },
            };

        /// <summary>Annotation / non-clash categories we never want in a clash matrix.</summary>
        private static readonly HashSet<string> SkipCategories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "<Room Separation>", "<Space Separation>", "<Area Boundary>", "Center line", "Lines",
                "Grids", "Levels", "Reference Points", "Reference Planes", "Scope Boxes", "Cameras"
            };

        /// <summary>Set names that group categories but are not a Revit category themselves.</summary>
        private static readonly HashSet<string> GroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Curtain Walls"
        };

        /// <summary>Maps a category to its main (parent) category, or itself if not merged.</summary>
        public static string GetMainCategory(string category)
        {
            string c = string.IsNullOrEmpty(category) ? "" : category.Trim();
            string parent;
            return CategoryMergeMap.TryGetValue(c, out parent) ? parent : c;
        }

        /// <summary>A main category plus every category folded into it (e.g. Pipes, Pipe Fittings, Flex Pipes).</summary>
        public static List<string> GetMergedCategories(string mainCategory)
        {
            // A group name that is not a Revit category itself ("Curtain Walls") is not
            // searched for - only its members are.
            var list = GroupNames.Contains(mainCategory) ? new List<string>() : new List<string> { mainCategory };
            foreach (KeyValuePair<string, string> kv in CategoryMergeMap)
            {
                if (string.Equals(kv.Value, mainCategory, StringComparison.OrdinalIgnoreCase))
                    list.Add(kv.Key);
            }
            return list;
        }

        // ================================================================ model scan

        /// <summary>
        /// Reads every appended file: its Revit categories (fittings, accessories and
        /// insulation folded into their parents) and its discipline (guessed, or the
        /// user's earlier choice). The scan stops at each category node, so it never
        /// walks families, types or geometry. Files with no Revit categories (IFC,
        /// DWG, ...) are reported in <paramref name="warnings"/>.
        /// </summary>
        public static List<ModelInfo> ScanModels(Document document, List<string> warnings, NavisProgress progress)
        {
            var result = new List<ModelInfo>();
            var noCategoryFiles = new List<string>();

            // foreach, not ToList(): Navisworks collections have a broken CopyTo.
            var models = new List<Autodesk.Navisworks.Api.Model>();
            foreach (Autodesk.Navisworks.Api.Model model in document.Models)
                models.Add(model);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < models.Count; i++)
            {
                Autodesk.Navisworks.Api.Model model = models[i];
                string file = SafeFileName(model);
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // The English class name is exact. If a model yields nothing that way
                // (a non-English Navisworks), fall back to the tree structure.
                CollectCategories(model.RootItem, found, true);
                if (found.Count == 0)
                    CollectCategories(model.RootItem, found, false);
                found.RemoveWhere(c => SkipCategories.Contains(c));

                if (found.Count == 0)
                    noCategoryFiles.Add(file);
                if (seen.Add(file))
                {
                    var info = new ModelInfo(file, found);
                    result.Add(info);
                    Watchdog.Log("MODEL[" + i + "] " + file + " discipline=" + info.Discipline +
                        (info.Discipline != info.Guessed ? " (guessed " + info.Guessed + ")" : "") +
                        " categories=" + found.Count + " (" + string.Join(", ", found.OrderBy(c => c)) + ")");
                }

                if (progress != null)
                    progress.Report((i + 1) / (double)models.Count);
            }

            if (noCategoryFiles.Count > 0)
            {
                warnings.Add("No Revit categories found in " + noCategoryFiles.Count +
                    " file(s) - category sets will not include them: " + string.Join(", ", noCategoryFiles));
            }
            return result;
        }

        /// <summary>Every Revit category in the scanned models.</summary>
        public static List<string> AllCategories(IEnumerable<ModelInfo> models)
        {
            var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelInfo m in models)
                all.UnionWith(m.Categories);
            return all.ToList();
        }

        private static void CollectCategories(ModelItem root, HashSet<string> categories, bool byClassDisplayName)
        {
            if (root == null)
                return;

            var queue = new Queue<KeyValuePair<ModelItem, int>>();
            queue.Enqueue(new KeyValuePair<ModelItem, int>(root, 0));

            while (queue.Count > 0)
            {
                KeyValuePair<ModelItem, int> entry = queue.Dequeue();
                ModelItem item = entry.Key;
                int depth = entry.Value;

                if (IsCategoryNode(item, byClassDisplayName))
                {
                    if (!string.IsNullOrWhiteSpace(item.DisplayName))
                        categories.Add(GetMainCategory(item.DisplayName));
                    continue; // categories never nest - nothing below is needed
                }

                if (depth < MaxScanDepth)
                {
                    foreach (ModelItem child in item.Children)
                        queue.Enqueue(new KeyValuePair<ModelItem, int>(child, depth + 1));
                }
            }
        }

        private static bool IsCategoryNode(ModelItem item, bool byClassDisplayName)
        {
            if (byClassDisplayName)
                return item.ClassDisplayName == CategoryClassDisplay;

            if (item.ClassName != RevitCollectionClass)
                return false;
            ModelItem parent = item.Parent;
            return parent == null || parent.ClassName != RevitCollectionClass;
        }

        private static string SafeFileName(Autodesk.Navisworks.Api.Model model)
        {
            try
            {
                string path = !string.IsNullOrEmpty(model.FileName) ? model.FileName : model.SourceFileName;
                return string.IsNullOrEmpty(path) ? "(unnamed)" : Path.GetFileName(path);
            }
            catch
            {
                return "(unnamed)";
            }
        }

        /// <summary>The name of the set of one discipline's elements of one category: "AR Walls".</summary>
        public static string SetName(string discipline, string category)
        {
            return discipline + " " + category;
        }

        /// <summary>
        /// Builds the default clash matrix for the scanned models. There is one set per
        /// discipline and category ("AR Walls", "ST Walls", "MEC Ducts", "EL Cable
        /// Trays"), searching only that discipline's models, and one test per pair of
        /// sets that the built-in clash standard tests, with the standard's priority and
        /// tolerance for those disciplines' rows. Each set also matches the categories
        /// folded into it.
        ///
        /// A pair the standard does not test is left out. A pair with a category the
        /// standard does not list is kept, with the standard's priority rule (weightings,
        /// the unknown side counting as <see cref="ClashStandard.UnknownWeight"/>) and the
        /// dialog's tolerance. A pair the standard tests without a tolerance also takes
        /// the dialog's tolerance.
        /// </summary>
        public static MatrixParseResult BuildFullMatrix(IList<ModelInfo> models, double toleranceMm, bool bestPractice = true)
        {
            ClashStandard standard = ClashStandard.Default;
            var result = new MatrixParseResult { BatchName = "Clash Matrix", BestPractice = bestPractice };

            // The sets: (discipline, category) as found in the models.
            var setDiscipline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Categories of another discipline in a file (pipes in the electrical model),
            // left out of every set: file -> categories.
            var leftOut = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelInfo m in models)
            {
                foreach (string category in m.Categories.Select(GetMainCategory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string code = Disciplines.ForCategory(m.Discipline, category, bestPractice);
                    if (code == null)
                    {
                        SortedSet<string> cats;
                        if (!leftOut.TryGetValue(m.FileName, out cats)) leftOut[m.FileName] = cats = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        cats.Add(category);
                        continue;
                    }
                    string name = SetName(code, category);
                    if (setDiscipline.ContainsKey(name))
                        continue;
                    setDiscipline[name] = code;

                    List<string> members = GetMergedCategories(category);
                    result.SetMembers[name] = members;
                    result.Disciplines[name] = Disciplines.Heading(code);
                    int? weight = standard.Weight(members, Disciplines.StandardGroup(code));
                    if (weight.HasValue) result.Weights[name] = weight.Value;

                    // Every file that sends this category to this discipline - also one
                    // that holds none of it yet - and no other, so an element is in one set.
                    result.SetModels[name] = models.Where(x => Disciplines.Routes(x.Discipline, category, code, bestPractice))
                        .Select(x => x.FileName).ToList();
                }
            }

            // Pairs are worked out in alphabetical order; the display / export order -
            // and the test numbers - follow discipline, then the standard's row order.
            List<string> sorted = setDiscipline.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            result.Categories.AddRange(sorted
                .OrderBy(c => standard.RowOrder(result.SetMembers[c], Disciplines.StandardGroup(setDiscipline[c])))
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase));

            int notTested = 0;
            var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    string left = sorted[i];
                    string right = sorted[j];

                    int priority;
                    double? tolerance;
                    ClashStandard.PairKind kind = standard.Lookup(
                        result.SetMembers[left], Disciplines.StandardGroup(setDiscipline[left]),
                        result.SetMembers[right], Disciplines.StandardGroup(setDiscipline[right]),
                        out priority, out tolerance);

                    if (kind == ClashStandard.PairKind.NotTested)
                    {
                        notTested++;
                        continue;
                    }
                    if (kind == ClashStandard.PairKind.Unknown)
                    {
                        int wl, wr;
                        if (!result.Weights.TryGetValue(left, out wl)) { wl = ClashStandard.UnknownWeight; unknown.Add(left); }
                        if (!result.Weights.TryGetValue(right, out wr)) { wr = ClashStandard.UnknownWeight; unknown.Add(right); }
                        priority = ClashStandard.PriorityFromWeights(wl, wr);
                        tolerance = null;
                    }

                    result.Tests.Add(new ClashTestDefinition
                    {
                        LeftName = left,
                        RightName = right,
                        Name = left + "-vs-" + right,
                        ToleranceMm = tolerance ?? toleranceMm,
                        ToleranceFromSheet = tolerance.HasValue,
                        Priority = priority
                    });
                }
            }

            if (leftOut.Count > 0)
            {
                result.Warnings.Add("Left out - a category of another discipline in that file (no EL Pipes, MEC Cable Trays, AR Plumbing Fixtures, ...): " +
                    string.Join("; ", leftOut.Select(kv => kv.Key + " (" + models.First(x => x.FileName == kv.Key).Discipline + "): " + string.Join(", ", kv.Value))));
            }
            result.Warnings.Add("Priority and tolerance from the built-in clash standard" +
                (notTested > 0 ? "; " + notTested + " pair(s) the standard does not test were left out." : "."));
            if (unknown.Count > 0)
                result.Warnings.Add("Not in the built-in standard (weighting " + ClashStandard.UnknownWeight +
                    ", default tolerance): " + string.Join(", ", unknown));
            MatrixLayout.ApplyTestNames(result);
            return result;
        }

        /// <summary>
        /// Limits each set of an imported matrix to the models of its discipline (taken
        /// from the set's discipline heading: AR, ST, MEC, EL, ICT, or BS for all services).
        /// A set whose discipline is not one of those searches every model.
        /// </summary>
        public static void AssignModelScopes(MatrixParseResult matrix, IList<ModelInfo> models)
        {
            foreach (string name in matrix.Categories)
            {
                string code = MatrixLayout.DisciplineCode(MatrixLayout.DisciplineOf(matrix, name));
                if (code != Disciplines.Architecture && code != Disciplines.Structure && code != Disciplines.Mechanical &&
                    code != Disciplines.Electrical && code != Disciplines.Ict && code != "BS")
                {
                    matrix.SetModels.Remove(name);
                    continue;
                }
                IList<string> members = matrix.GetMembers(name);
                matrix.SetModels[name] = models
                    .Where(m => members.Any(c => Disciplines.Routes(m.Discipline, c, code, matrix.BestPractice)))
                    .Select(m => m.FileName).ToList();
            }
        }

        // ================================================================ search sets

        /// <summary>The add-in's folder in the Sets window, or null when there is none yet.</summary>
        public static FolderItem FindSetFolder(Document document)
        {
            foreach (SavedItem item in document.SelectionSets.RootItem.Children)
            {
                var folder = item as FolderItem;
                if (folder != null && string.Equals(folder.DisplayName, FolderName, StringComparison.OrdinalIgnoreCase))
                    return folder;
            }
            return null;
        }

        private static FolderItem GetOrCreateSetFolder(Document document)
        {
            FolderItem folder = FindSetFolder(document);
            if (folder != null)
                return folder;

            document.SelectionSets.AddCopy(new FolderItem { DisplayName = FolderName });
            return FindSetFolder(document);
        }

        /// <summary>The search sets in the add-in's folder, by name. Sets anywhere else are not included.</summary>
        public static Dictionary<string, SelectionSet> GetManagedSets(Document document)
        {
            var sets = new Dictionary<string, SelectionSet>(StringComparer.OrdinalIgnoreCase);
            FolderItem folder = FindSetFolder(document);
            if (folder == null)
                return sets;

            foreach (SavedItem item in folder.Children)
            {
                var set = item as SelectionSet;
                if (set != null && !string.IsNullOrWhiteSpace(set.DisplayName) && !sets.ContainsKey(set.DisplayName))
                    sets.Add(set.DisplayName, set);
            }
            return sets;
        }

        /// <summary>
        /// Creates (or rewrites) one search set per matrix set in the add-in's folder.
        /// With <paramref name="rewriteExisting"/> false an existing search set is left
        /// alone; a non-search (static) set of that name is always upgraded. A rewrite
        /// keeps the set's GUID and position, so clash tests stay linked to it.
        /// </summary>
        public static SetSyncResult EnsureSearchSets(Document document, MatrixParseResult matrix,
            bool rewriteExisting, List<string> warnings, NavisProgress progress)
        {
            var result = new SetSyncResult();
            bool internalNames = ResolveCategoryPropertyNames(document, matrix, warnings);
            Dictionary<string, ModelItem> roots = ModelRoots(document);
            var noModels = new List<string>();

            using (Transaction tx = TryBeginTransaction(document, "Clash Matrix: search sets"))
            {
                GetOrCreateSetFolder(document);

                int done = 0;
                foreach (string name in matrix.Categories)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // A set limited to some models ("AR Walls": the architectural files)
                    // searches within their roots only.
                    List<ModelItem> scope = null;
                    List<string> files;
                    if (matrix.SetModels.TryGetValue(name, out files))
                    {
                        scope = files.Where(roots.ContainsKey).Select(f => roots[f]).ToList();
                        if (scope.Count == 0) noModels.Add(name);
                    }

                    var set = new SelectionSet(BuildCategorySearch(matrix.GetMembers(name), internalNames, scope))
                    {
                        DisplayName = name
                    };

                    FolderItem folder = FindSetFolder(document);
                    int index = IndexOfSet(folder, name);
                    if (index < 0)
                    {
                        document.SelectionSets.AddCopy(folder, set);
                        result.Created++;
                    }
                    else
                    {
                        var existing = (SelectionSet)folder.Children[index];
                        if (rewriteExisting || !existing.HasSearch)
                        {
                            set.Guid = existing.Guid;
                            document.SelectionSets.ReplaceWithCopy(folder, index, set);
                            result.Updated++;
                        }
                        else
                        {
                            result.Unchanged++;
                        }
                    }

                    done++;
                    if (progress != null)
                        progress.Report(done / (double)Math.Max(1, matrix.Categories.Count));
                }

                if (noModels.Count > 0)
                    warnings.Add("No model of their discipline is loaded, so these sets find nothing for now: " + string.Join(", ", noModels));

                if (tx != null)
                    tx.Commit();
            }

            Watchdog.Log("Search sets: created=" + result.Created + " updated=" + result.Updated +
                " unchanged=" + result.Unchanged);
            return result;
        }

        private static int IndexOfSet(FolderItem folder, string name)
        {
            for (int i = 0; i < folder.Children.Count; i++)
            {
                var set = folder.Children[i] as SelectionSet;
                if (set != null && string.Equals(set.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>The root item of each loaded model, by file name.</summary>
        private static Dictionary<string, ModelItem> ModelRoots(Document document)
        {
            var roots = new Dictionary<string, ModelItem>(StringComparer.OrdinalIgnoreCase);
            foreach (Autodesk.Navisworks.Api.Model model in document.Models)
            {
                string file = SafeFileName(model);
                if (!roots.ContainsKey(file) && model.RootItem != null)
                    roots.Add(file, model.RootItem);
            }
            return roots;
        }

        /// <param name="scope">The model roots to search within; null = every model.</param>
        private static Search BuildCategorySearch(IList<string> categories, bool internalNames, IList<ModelItem> scope = null)
        {
            var search = new Search();
            if (scope == null) search.Selection.SelectAll();
            else search.Selection.CopyFrom(scope);
            search.Locations = SearchLocations.DescendantsAndSelf;
            search.PruneBelowMatch = true;

            if (categories.Count == 1)
            {
                search.SearchConditions.Add(CategoryCondition(categories[0], internalNames));
            }
            else
            {
                // Each group is OR-ed with the others: "Category = Pipes" OR "Category = Pipe Fittings" ...
                foreach (string category in categories)
                    search.SearchConditions.AddGroup(new[] { CategoryCondition(category, internalNames) });
            }
            return search;
        }

        private static SearchCondition CategoryCondition(string category, bool internalNames)
        {
            SearchCondition has = internalNames
                ? SearchCondition.HasPropertyByName(ElementCategoryInternal, CategoryPropertyInternal)
                : SearchCondition.HasPropertyByDisplayName(ElementCategoryDisplay, CategoryPropertyDisplay);
            return has.EqualValue(VariantData.FromDisplayString(category));
        }

        /// <summary>
        /// Picks how the category property is addressed: by internal name (language
        /// independent) when that finds anything in this model, else by display name.
        /// </summary>
        private static bool ResolveCategoryPropertyNames(Document document, MatrixParseResult matrix,
            List<string> warnings)
        {
            foreach (string name in matrix.Categories.Take(3))
            {
                IList<string> members = matrix.GetMembers(name);
                if (BuildCategorySearch(members, true).FindFirst(document, false) != null)
                    return true;
                if (BuildCategorySearch(members, false).FindFirst(document, false) != null)
                {
                    Watchdog.Log("Category property resolved by display name (Element > Category).");
                    return false;
                }
            }

            // Nothing matched either way (e.g. no Revit model loaded yet): internal names
            // are the right choice for when Revit files are appended later.
            return true;
        }

        /// <summary>
        /// Re-runs each managed search set and returns a per-set item count, total last.
        /// </summary>
        public static List<string> CountSetItems(Document document, IList<string> names, NavisProgress progress)
        {
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            var report = new List<string>();
            long total = 0;
            int empty = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                SelectionSet set;
                if (!sets.TryGetValue(name, out set) || !set.HasSearch)
                {
                    report.Add(name + ": no search set");
                    continue;
                }

                ModelItemCollection found = set.Search.FindAll(document, false);
                int count = found != null ? found.Count : 0;
                if (count == 0) empty++;
                total += count;
                report.Add(name + ": " + count + " items");

                if (progress != null)
                    progress.Report((i + 1) / (double)names.Count);
            }
            report.Add("TOTAL: " + total + " items across " + names.Count + " sets (" + empty + " empty)");
            return report;
        }

        // ================================================================ clash tests
        //
        // Clash Detective has no test folders (the API rejects a ClashTestFolder
        // under the tests root), so the tests sit at the top level. A test belongs
        // to the add-in when BOTH its sides are linked to sets in the add-in's
        // selection-set folder, and it is identified by that PAIR of sets - not by its
        // name, which carries a number that shifts when categories come and go. So a
        // renumbered test is renamed in place and keeps its results. Any other test -
        // the user's own, or one that only shares a name - is never edited or removed.

        /// <summary>The GUID of the set a clash side is linked to, if it is one of the add-in's sets.</summary>
        private static Guid? LinkedSet(Document document, ClashSelection selection, HashSet<Guid> managedSets)
        {
            try
            {
                if (selection.Selection.SelectionSources.Count != 1)
                    return null;
                SavedItem resolved = document.SelectionSets.ResolveSelectionSource(selection.Selection.SelectionSources[0]);
                return resolved != null && managedSets.Contains(resolved.Guid) ? resolved.Guid : (Guid?)null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Order-independent key of two sets.</summary>
        private static string PairKey(Guid a, Guid b)
        {
            return a.CompareTo(b) <= 0 ? a.ToString("N") + b.ToString("N") : b.ToString("N") + a.ToString("N");
        }

        /// <summary>The test's pair key when both its sides are linked to the add-in's sets, else null.</summary>
        private static string ManagedPair(Document document, ClashTest test, HashSet<Guid> managedSets)
        {
            if (managedSets.Count == 0)
                return null;
            Guid? a = LinkedSet(document, test.SelectionA, managedSets);
            Guid? b = a.HasValue ? LinkedSet(document, test.SelectionB, managedSets) : null;
            return a.HasValue && b.HasValue ? PairKey(a.Value, b.Value) : null;
        }

        /// <summary>A snapshot of the top-level tests: the add-in's by set pair, the others' names.</summary>
        private sealed class TestIndex
        {
            public readonly Dictionary<string, ClashTest> ByPair = new Dictionary<string, ClashTest>();
            public readonly HashSet<string> OtherNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ClashTest> Managed = new List<ClashTest>();
            public readonly Dictionary<ClashTest, string> PairOf = new Dictionary<ClashTest, string>();
            public int OtherCount;

            public TestIndex(Document document, DocumentClashTests testsData, HashSet<Guid> managedSets)
            {
                foreach (SavedItem item in testsData.Value.TestsRoot.Children)
                {
                    var test = item as ClashTest;
                    if (test == null)
                        continue;
                    string pair = ManagedPair(document, test, managedSets);
                    if (pair == null)
                    {
                        OtherCount++;
                        if (!string.IsNullOrEmpty(test.DisplayName))
                            OtherNames.Add(test.DisplayName);
                        continue;
                    }
                    Managed.Add(test);
                    PairOf[test] = pair;
                    if (!ByPair.ContainsKey(pair))
                        ByPair.Add(pair, test);
                }
            }
        }

        /// <summary>Order-independent key of two set names.</summary>
        public static string SetPairKey(string a, string b)
        {
            return ClashTestDefinition.PairKey(a, b);
        }

        /// <summary>
        /// The add-in's existing tests, by the names of the two sets they link
        /// (<see cref="SetPairKey"/>) -> the test's current name. For the Clash Tests tab.
        /// </summary>
        public static Dictionary<string, string> ExistingTestsBySets(Document document)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            HashSet<Guid> managed = ManagedSetGuids(sets);
            var nameOf = new Dictionary<Guid, string>();
            foreach (KeyValuePair<string, SelectionSet> kv in sets)
                nameOf[kv.Value.Guid] = kv.Key;

            foreach (SavedItem item in document.GetClash().TestsData.Value.TestsRoot.Children)
            {
                var test = item as ClashTest;
                if (test == null) continue;
                Guid? a = LinkedSet(document, test.SelectionA, managed);
                Guid? b = a.HasValue ? LinkedSet(document, test.SelectionB, managed) : null;
                if (!a.HasValue || !b.HasValue) continue;
                string key = SetPairKey(nameOf[a.Value], nameOf[b.Value]);
                if (!result.ContainsKey(key)) result.Add(key, test.DisplayName);
            }
            return result;
        }

        /// <summary>Item count of each named Clash Matrix set that exists (a set that does not is left out).</summary>
        public static Dictionary<string, int> CountItems(Document document, IList<string> names, NavisProgress progress)
        {
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                SelectionSet set;
                if (sets.TryGetValue(names[i], out set) && set.HasSearch)
                {
                    ModelItemCollection found = set.Search.FindAll(document, false);
                    counts[names[i]] = found != null ? found.Count : 0;
                }
                if (progress != null)
                    progress.Report((i + 1) / (double)Math.Max(1, names.Count));
            }
            return counts;
        }

        /// <summary>
        /// Deletes the named Clash Matrix sets. A set some clash test still uses is kept
        /// (its name goes to <paramref name="inUse"/>): deleting it would leave that test
        /// with an empty side. One undo step.
        /// </summary>
        public static int RemoveSets(Document document, IEnumerable<string> names, List<string> inUse)
        {
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            HashSet<Guid> managed = ManagedSetGuids(sets);
            var used = new HashSet<Guid>();
            foreach (SavedItem item in document.GetClash().TestsData.Value.TestsRoot.Children)
            {
                var test = item as ClashTest;
                if (test == null) continue;
                Guid? a = LinkedSet(document, test.SelectionA, managed);
                Guid? b = LinkedSet(document, test.SelectionB, managed);
                if (a.HasValue) used.Add(a.Value);
                if (b.HasValue) used.Add(b.Value);
            }

            int removed = 0;
            using (Transaction tx = TryBeginTransaction(document, "Clash Matrix: remove search sets"))
            {
                foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    SelectionSet set;
                    if (!sets.TryGetValue(name, out set)) continue;
                    if (used.Contains(set.Guid))
                    {
                        if (inUse != null) inUse.Add(name);
                        continue;
                    }
                    FolderItem folder = FindSetFolder(document);
                    int index = folder != null ? IndexOfSet(folder, name) : -1;
                    if (index < 0) continue;
                    document.SelectionSets.RemoveAt(folder, index);
                    removed++;
                }
                if (tx != null) tx.Commit();
            }
            Watchdog.Log("Search sets removed: " + removed + (inUse != null && inUse.Count > 0 ? " (kept, in use: " + string.Join(", ", inUse) + ")" : ""));
            return removed;
        }

        /// <summary>The GUIDs of the add-in's sets (those in the Clash Matrix folder).</summary>
        public static HashSet<Guid> ManagedSetGuidsOf(Document document)
        {
            return ManagedSetGuids(GetManagedSets(document));
        }

        /// <summary>True when both sides of the test are linked to one of the given sets (see <see cref="ManagedSetGuidsOf"/>).</summary>
        public static bool IsManagedTest(Document document, ClashTest test, HashSet<Guid> managedSets)
        {
            return ManagedPair(document, test, managedSets) != null;
        }

        private static HashSet<Guid> ManagedSetGuids(Dictionary<string, SelectionSet> sets)
        {
            var guids = new HashSet<Guid>();
            foreach (SelectionSet set in sets.Values)
                guids.Add(set.Guid);
            return guids;
        }

        /// <summary>
        /// Works out what <see cref="ApplyTests"/> would do, without changing anything,
        /// so the user can confirm first. Only the add-in's own tests are considered.
        /// </summary>
        public static TestPlan PlanTests(Document document, IList<ClashTestDefinition> tests, double defaultToleranceMm)
        {
            DocumentClashTests testsData = document.GetClash().TestsData;
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            var index = new TestIndex(document, testsData, ManagedSetGuids(sets));

            var plan = new TestPlan();
            var wanted = new HashSet<string>();
            foreach (ClashTestDefinition t in tests)
            {
                SelectionSet left, right;
                if (!sets.TryGetValue(t.LeftName, out left) || !sets.TryGetValue(t.RightName, out right))
                {
                    // Its sets are made first; a test whose sets do not exist yet is new.
                    if (index.OtherNames.Contains(t.Name)) plan.Conflicts.Add(t.Name);
                    else plan.ToAdd++;
                    continue;
                }

                string pair = PairKey(left.Guid, right.Guid);
                wanted.Add(pair);
                ClashTest current;
                if (!index.ByPair.TryGetValue(pair, out current))
                {
                    if (index.OtherNames.Contains(t.Name)) plan.Conflicts.Add(t.Name);
                    else plan.ToAdd++;
                }
                else if (!IsSame(document, current, t, left, right, defaultToleranceMm))
                    plan.ToUpdate++;
                else if (!string.Equals(current.DisplayName, t.Name, StringComparison.Ordinal))
                    plan.ToRename++;
                else
                    plan.Unchanged++;
            }

            foreach (ClashTest test in index.Managed)
            {
                string pair = index.PairOf[test];
                if (!wanted.Contains(pair) || !ReferenceEquals(index.ByPair[pair], test))
                    plan.Stale.Add(test.DisplayName);
            }
            plan.Stale.Sort(StringComparer.OrdinalIgnoreCase);
            plan.OtherTests = index.OtherCount;
            return plan;
        }

        /// <summary>
        /// Adds, updates or renames one top-level clash test per definition, linking each
        /// side to its managed search set. A test that is already right is left alone, and
        /// one that only needs its new name is renamed, so their results are kept. A test
        /// of the same name that is not the add-in's is left alone and reported. With
        /// <paramref name="removeStale"/>, the add-in's tests that the matrix no longer
        /// has are removed, and so are its sets that nothing uses any more. The whole run
        /// is one undo step.
        /// </summary>
        public static TestApplyResult ApplyTests(Document document, MatrixParseResult matrix,
            double defaultToleranceMm, bool removeStale, List<string> warnings, NavisProgress progress)
        {
            IList<ClashTestDefinition> tests = matrix.Tests;
            DocumentClashTests testsData = document.GetClash().TestsData;
            var result = new TestApplyResult();

#if !NAVIS2026 && !NAVIS2027
            if (tests.Select(t => t.Priority).Distinct().Count() > 1)
                warnings.Add("Clash test priority is not available before Navisworks 2026 - the matrix priorities were not applied.");
#endif

            using (Transaction tx = TryBeginTransaction(document, "Clash Matrix: clash tests"))
            {
                Dictionary<string, SelectionSet> sets = GetManagedSets(document);
                HashSet<Guid> managed = ManagedSetGuids(sets);
                var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var conflicts = new List<string>();
                var wanted = new HashSet<string>();

                // One snapshot of the top level. Each test is edited at most once and its
                // reference is not used again afterwards.
                var index = new TestIndex(document, testsData, managed);

                for (int i = 0; i < tests.Count; i++)
                {
                    ClashTestDefinition t = tests[i];

                    SelectionSet left, right;
                    bool hasLeft = sets.TryGetValue(t.LeftName, out left);
                    bool hasRight = sets.TryGetValue(t.RightName, out right);
                    if (!hasLeft) missing.Add(t.LeftName);
                    if (!hasRight) missing.Add(t.RightName);
                    if (!hasLeft || !hasRight)
                    {
                        result.Skipped++;
                        continue;
                    }

                    string pair = PairKey(left.Guid, right.Guid);
                    wanted.Add(pair);


                    ClashTest current;
                    index.ByPair.TryGetValue(pair, out current);

                    if (current == null)
                    {
                        if (index.OtherNames.Contains(t.Name))
                        {
                            conflicts.Add(t.Name);
                            result.Skipped++;
                        }
                        else
                        {
                            testsData.TestsAddCopy(testsData.Value.TestsRoot, BuildTest(document, t, left, right, defaultToleranceMm));
                            result.Added++;
                        }
                    }
                    else if (!IsSame(document, current, t, left, right, defaultToleranceMm))
                    {
                        testsData.TestsEditTestFromCopy(current, BuildTest(document, t, left, right, defaultToleranceMm));
                        result.Updated++;
                    }
                    else if (!string.Equals(current.DisplayName, t.Name, StringComparison.Ordinal))
                    {
                        testsData.TestsEditDisplayName(current, t.Name);
                        result.Renamed++;
                    }
                    else
                    {
                        result.Unchanged++;
                    }

                    if (progress != null)
                        progress.Report((i + 1) / (double)tests.Count);
                }

                if (removeStale)
                {
                    // Found once (by GUID), then each one looked up afresh as it is
                    // removed: a removal can invalidate the references taken before it.
                    index = new TestIndex(document, testsData, managed);
                    List<Guid> stale = index.Managed
                        .Where(test => !wanted.Contains(index.PairOf[test]) || !ReferenceEquals(index.ByPair[index.PairOf[test]], test))
                        .Select(test => test.Guid)
                        .ToList();
                    foreach (Guid guid in stale)
                    {
                        ClashTest test = null;
                        foreach (SavedItem item in testsData.Value.TestsRoot.Children)
                        {
                            if (item is ClashTest && item.Guid == guid)
                            {
                                test = (ClashTest)item;
                                break;
                            }
                        }
                        if (test == null)
                            continue;
                        testsData.TestsRemove(testsData.Value.TestsRoot, test);
                        result.Removed++;
                    }
                    result.SetsRemoved = RemoveUnusedSets(document, testsData, matrix.Categories);
                }

                if (missing.Count > 0)
                    warnings.Add("No search set in the '" + FolderName + "' folder for: " + string.Join(", ", missing));
                if (conflicts.Count > 0)
                    warnings.Add(conflicts.Count + " test name(s) already used by clash tests that are not linked to the '" +
                        FolderName + "' sets - those were left alone: " + string.Join(", ", conflicts.Take(10)) +
                        (conflicts.Count > 10 ? ", ..." : ""));

                if (tx != null)
                    tx.Commit();
            }

            Watchdog.Log("Clash tests: added=" + result.Added + " updated=" + result.Updated + " renamed=" + result.Renamed +
                " unchanged=" + result.Unchanged + " removed=" + result.Removed + " skipped=" + result.Skipped +
                " sets removed=" + result.SetsRemoved);
            return result;
        }

        /// <summary>
        /// Removes the add-in's sets that are not in the matrix and that no clash test
        /// uses any more (e.g. "Duct Accessories" once it is part of "Ducts").
        /// </summary>
        private static int RemoveUnusedSets(Document document, DocumentClashTests testsData, IList<string> matrixSets)
        {
            var keep = new HashSet<string>(matrixSets, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SelectionSet> sets = GetManagedSets(document);
            HashSet<Guid> managed = ManagedSetGuids(sets);

            var used = new HashSet<Guid>();
            foreach (SavedItem item in testsData.Value.TestsRoot.Children)
            {
                var test = item as ClashTest;
                if (test == null) continue;
                Guid? a = LinkedSet(document, test.SelectionA, managed);
                Guid? b = LinkedSet(document, test.SelectionB, managed);
                if (a.HasValue) used.Add(a.Value);
                if (b.HasValue) used.Add(b.Value);
            }

            int removed = 0;
            foreach (string name in sets.Keys.Where(n => !keep.Contains(n)).ToList())
            {
                if (used.Contains(sets[name].Guid))
                    continue;
                FolderItem folder = FindSetFolder(document);
                int index = folder != null ? IndexOfSet(folder, name) : -1;
                if (index < 0)
                    continue;
                document.SelectionSets.RemoveAt(folder, index);
                removed++;
            }
            return removed;
        }

        private static double ToleranceMm(ClashTestDefinition t, double defaultToleranceMm)
        {
            return t.ToleranceFromSheet ? t.ToleranceMm : defaultToleranceMm;
        }

        /// <summary>The priority written to Navisworks: always one it can show (0 none, 1 High, 2 Medium, 3 Low).</summary>
        private static int SafePriority(int priority)
        {
            return Math.Max(0, Math.Min(3, priority));
        }

        private static ClashTest BuildTest(Document document, ClashTestDefinition t, SelectionSet left,
            SelectionSet right, double defaultToleranceMm)
        {
            var test = new ClashTest
            {
                DisplayName = t.Name,
                Tolerance = UnitConverter.MmToUnit(ToleranceMm(t, defaultToleranceMm), document.Units),
                MergeComposites = true,
                TestType = ClashTestType.Hard
            };

#if NAVIS2026 || NAVIS2027
            // ClashTest.Priority was added to the API in Navisworks 2026. Clash
            // Detective only knows 0..3 and crashes on anything else.
            test.Priority = SafePriority(t.Priority);
#endif

            test.SelectionA.SelfIntersect = false;
            test.SelectionA.PrimitiveTypes = PrimitiveTypes.Triangles;
            test.SelectionA.Selection.SelectionSources.Add(document.SelectionSets.CreateSelectionSource(left));

            test.SelectionB.SelfIntersect = false;
            test.SelectionB.PrimitiveTypes = PrimitiveTypes.Triangles;
            test.SelectionB.Selection.SelectionSources.Add(document.SelectionSets.CreateSelectionSource(right));
            return test;
        }

        /// <summary>
        /// True when an existing test already has the definition's settings and is
        /// linked to the same two sets (either way round), so its results can be kept.
        /// The name is not compared - a different name is a rename.
        /// </summary>
        private static bool IsSame(Document document, ClashTest current, ClashTestDefinition t,
            SelectionSet left, SelectionSet right, double defaultToleranceMm)
        {
            double tolerance = UnitConverter.MmToUnit(ToleranceMm(t, defaultToleranceMm), document.Units);
            if (Math.Abs(current.Tolerance - tolerance) > 1e-9 || current.TestType != ClashTestType.Hard)
                return false;

#if NAVIS2026 || NAVIS2027
            if (current.Priority != SafePriority(t.Priority))
                return false;
#endif

            var both = new HashSet<Guid> { left.Guid, right.Guid };
            Guid? a = LinkedSet(document, current.SelectionA, both);
            Guid? b = LinkedSet(document, current.SelectionB, both);
            return a.HasValue && b.HasValue && (a.Value != b.Value || left.Guid == right.Guid);
        }

        // ================================================================ helpers

        /// <summary>Opens an undo group; a failure to open one is logged and the work carries on without it.</summary>
        private static Transaction TryBeginTransaction(Document document, string name)
        {
            try
            {
                return document.BeginTransaction(name);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("BeginTransaction", ex);
                return null;
            }
        }
    }

    public class SetSyncResult
    {
        public int Created;
        public int Updated;
        public int Unchanged;
    }

    public class TestPlan
    {
        public int ToAdd;
        public int ToUpdate;
        public int Unchanged;

        /// <summary>The add-in's tests that only need their new name (results kept).</summary>
        public int ToRename;
        /// <summary>The add-in's tests that the matrix no longer has.</summary>
        public List<string> Stale = new List<string>();

        /// <summary>Matrix test names already used by a test that is not the add-in's (left alone).</summary>
        public List<string> Conflicts = new List<string>();

        /// <summary>Clash tests that are not the add-in's (never touched; reported for reassurance).</summary>
        public int OtherTests;
    }

    public class TestApplyResult
    {
        public int Added;
        public int Updated;
        public int Unchanged;
        public int Removed;
        public int Skipped;
        public int Renamed;

        /// <summary>The add-in's sets removed because the matrix and every test stopped using them.</summary>
        public int SetsRemoved;
    }

    /// <summary>
    /// The Navisworks progress bar for a long operation. Throws
    /// <see cref="OperationCanceledException"/> from <see cref="Report"/> once the user cancels.
    /// </summary>
    public sealed class NavisProgress : IDisposable
    {
        private readonly Progress _progress;

        public NavisProgress(string message)
        {
            try { _progress = Application.BeginProgress("Clash Matrix", message); }
            catch (Exception ex) { Watchdog.LogException("BeginProgress", ex); }
        }

        public void Report(double fraction)
        {
            if (_progress == null)
                return;
            _progress.Update(Math.Max(0.0, Math.Min(1.0, fraction)));
            if (_progress.IsCanceled)
                throw new OperationCanceledException("Cancelled.");
        }

        public void Dispose()
        {
            if (_progress == null)
                return;
            try { Application.EndProgress(); }
            catch { /* best effort */ }
        }
    }
}
