using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashMatrix.Model
{
    /// <summary>
    /// A clash matrix: the search sets it references, the Revit categories each set
    /// matches, and the clash tests between sets, plus any parse warnings. The window
    /// edits it (exclude, rename, change categories, edit tests); <see cref="Effective"/>
    /// is what is then created in Navisworks.
    /// </summary>
    public class MatrixParseResult
    {
        public string BatchName { get; set; }

        /// <summary>
        /// True: "Best practice" - each discipline's sets keep only its own categories.
        /// False: "All categories" - every category in every file gets a set.
        /// </summary>
        public bool BestPractice { get; set; }

        /// <summary>Unique search-set names (the matrix's Description column).</summary>
        public List<string> Categories { get; set; }

        /// <summary>
        /// Set name -> the Revit categories that set matches. A matrix row group
        /// ("Description") can cover several Revit categories ("Revit Category"
        /// column); a set with no entry matches the Revit category of its own name.
        /// </summary>
        public Dictionary<string, List<string>> SetMembers { get; set; }

        /// <summary>Set name -> its weighting (1 = most important), when known. Shown in the exported workbook.</summary>
        public Dictionary<string, int> Weights { get; set; }

        /// <summary>Set name -> its discipline heading ("ARCHITECTURE - AR", ...), when known. Groups the exported workbook.</summary>
        public Dictionary<string, string> Disciplines { get; set; }

        /// <summary>
        /// Set name -> the model files (file names) its search is limited to, so "AR
        /// Walls" finds walls in the architectural models only. A set with no entry
        /// searches every model.
        /// </summary>
        public Dictionary<string, List<string>> SetModels { get; set; }

        /// <summary>Sets the user unticked: not created, and their tests are not either.</summary>
        public HashSet<string> ExcludedSets { get; private set; }

        public List<ClashTestDefinition> Tests { get; set; }
        public List<string> Warnings { get; set; }

        public MatrixParseResult()
        {
            BestPractice = true;
            Categories = new List<string>();
            SetMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Disciplines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SetModels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ExcludedSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Tests = new List<ClashTestDefinition>();
            Warnings = new List<string>();
        }

        /// <summary>The Revit categories the named set matches (never empty).</summary>
        public IList<string> GetMembers(string setName)
        {
            List<string> members;
            if (SetMembers.TryGetValue(setName, out members) && members.Count > 0)
                return members;
            return new[] { setName };
        }

        public bool IsIncluded(string setName)
        {
            return !ExcludedSets.Contains(setName);
        }

        /// <summary>True when the test is ticked and both its sets are.</summary>
        public bool IsIncluded(ClashTestDefinition test)
        {
            return test.Include && IsIncluded(test.LeftName) && IsIncluded(test.RightName);
        }

        // ------------------------------------------------------------- editing

        /// <summary>
        /// Renames a set everywhere: its entries, and the tests on either side of it.
        /// Returns false (nothing changed) when the new name is empty or already used.
        /// </summary>
        public bool RenameSet(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || string.Equals(oldName, newName, StringComparison.Ordinal))
                return false;
            if (Categories.Any(c => string.Equals(c, newName, StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return false;

            int i = Categories.FindIndex(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            Categories[i] = newName;
            Move(SetMembers, oldName, newName);
            Move(Weights, oldName, newName);
            Move(Disciplines, oldName, newName);
            Move(SetModels, oldName, newName);
            if (ExcludedSets.Remove(oldName)) ExcludedSets.Add(newName);

            foreach (ClashTestDefinition t in Tests)
            {
                if (string.Equals(t.LeftName, oldName, StringComparison.OrdinalIgnoreCase)) t.LeftName = newName;
                if (string.Equals(t.RightName, oldName, StringComparison.OrdinalIgnoreCase)) t.RightName = newName;
            }
            return true;
        }

        /// <summary>Sets the Revit categories a set matches (comma / semicolon separated text accepted).</summary>
        public void SetCategories(string setName, IEnumerable<string> categories)
        {
            List<string> list = categories
                .SelectMany(c => (c ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(c => c.Trim()).Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 0) SetMembers[setName] = list;
        }

        private static void Move<T>(Dictionary<string, T> map, string from, string to)
        {
            T value;
            if (map.TryGetValue(from, out value))
            {
                map.Remove(from);
                map[to] = value;
            }
        }

        /// <summary>
        /// What is created in Navisworks: the ticked sets and the ticked tests between
        /// them. A copy - the full matrix, with what is unticked, stays as it is.
        /// </summary>
        public MatrixParseResult Effective()
        {
            var e = new MatrixParseResult { BatchName = BatchName, BestPractice = BestPractice };
            foreach (string set in Categories.Where(IsIncluded))
            {
                e.Categories.Add(set);
                Copy(SetMembers, e.SetMembers, set);
                Copy(Weights, e.Weights, set);
                Copy(Disciplines, e.Disciplines, set);
                Copy(SetModels, e.SetModels, set);
            }
            e.Tests.AddRange(Tests.Where(IsIncluded));
            e.Warnings.AddRange(Warnings);
            return e;
        }

        private static void Copy<T>(Dictionary<string, T> from, Dictionary<string, T> to, string key)
        {
            T value;
            if (from.TryGetValue(key, out value)) to[key] = value;
        }
    }
}
