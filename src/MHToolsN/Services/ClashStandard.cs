using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MHToolsN.Services
{
    /// <summary>
    /// The built-in clash standard: for each pair of Revit categories, whether it
    /// is clash-tested, its Navisworks priority (1 = High, 2 = Medium, 3 = Low) and its tolerance.
    ///
    /// The data is Resources/ClashStandard.tsv, generated from a clash detection
    /// matrix workbook by tools/ClashStandard (see its README). A category
    /// can sit on several rows (Walls is both architectural and structural); a
    /// pair then takes the most important priority and the tightest tolerance of any
    /// of its rows, so nothing is tested more loosely than the standard asks.
    /// </summary>
    public sealed class ClashStandard
    {
        private const string ResourceName = "MHToolsN.Resources.ClashStandard.tsv";

        /// <summary>Weighting given to a category the standard does not list (the lowest).</summary>
        public const int UnknownWeight = 3;

        private static ClashStandard _default;

        private readonly Dictionary<string, List<Row>> _rows =
            new Dictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, Cell> _cells = new Dictionary<long, Cell>();

        /// <summary>The standard embedded in the add-in. Empty (knows nothing) if it cannot be read.</summary>
        public static ClashStandard Default
        {
            get
            {
                if (_default == null)
                {
                    try
                    {
                        using (Stream s = typeof(ClashStandard).Assembly.GetManifestResourceStream(ResourceName))
                        using (var reader = new StreamReader(s, Encoding.UTF8))
                            _default = Parse(reader);
                    }
                    catch (Exception ex)
                    {
                        Watchdog.LogException("Load built-in clash standard", ex);
                        _default = new ClashStandard();
                    }
                }
                return _default;
            }
        }

        public enum PairKind
        {
            /// <summary>At least one side has no Revit category the standard lists.</summary>
            Unknown,

            /// <summary>Both sides are in the standard, which does not test this pair.</summary>
            NotTested,

            /// <summary>Both sides are in the standard, which tests this pair.</summary>
            Tested
        }

        public bool Knows(string category)
        {
            return _rows.ContainsKey(category ?? "");
        }

        /// <summary>
        /// The categories' weighting (1 = most important), or null when the standard
        /// does not list them. <paramref name="group"/> picks the rows of one discipline
        /// group ("ARCHITECTURE", "STRUCTURE", "SERVICES"; see <see cref="RowsOf"/>).
        /// </summary>
        public int? Weight(IEnumerable<string> categories, string group = null)
        {
            int? best = null;
            foreach (Row r in RowsOf(categories, group))
                if (r.Weight > 0 && (best == null || r.Weight < best)) best = r.Weight;
            return best;
        }

        /// <summary>Sort key: the standard's row of these categories (categories not in the standard sort last).</summary>
        public int RowOrder(IEnumerable<string> categories, string group = null)
        {
            int first = int.MaxValue;
            foreach (Row r in RowsOf(categories, group))
                if (r.Index < first) first = r.Index;
            return first;
        }

        /// <summary>
        /// Looks up the pair of sets, each given by the Revit categories it matches and
        /// its discipline group (null = any). For <see cref="PairKind.Tested"/>,
        /// <paramref name="priority"/> is 1..3 and <paramref name="toleranceMm"/> is the
        /// standard's tolerance, or null when it gives none (use the dialog's default then).
        /// </summary>
        public PairKind Lookup(IEnumerable<string> left, string leftGroup, IEnumerable<string> right, string rightGroup,
            out int priority, out double? toleranceMm)
        {
            priority = 0;
            toleranceMm = null;

            List<Row> a = RowsOf(left, leftGroup);
            List<Row> b = RowsOf(right, rightGroup);
            if (a.Count == 0 || b.Count == 0)
                return PairKind.Unknown;

            bool tested = false;
            foreach (Row ra in a)
            {
                foreach (Row rb in b)
                {
                    if (ra.Index == rb.Index) continue;

                    // The sheet can hold a pair on either side of the diagonal.
                    Cell cell;
                    if (!_cells.TryGetValue(Key(ra.Index, rb.Index), out cell) &&
                        !_cells.TryGetValue(Key(rb.Index, ra.Index), out cell))
                        continue;

                    tested = true;
                    int p = Math.Max(1, ToNavisworksPriority(cell.Priority));
                    if (priority == 0 || p < priority) priority = p;
                    if (cell.ToleranceMm.HasValue && (toleranceMm == null || cell.ToleranceMm < toleranceMm))
                        toleranceMm = cell.ToleranceMm;
                }
            }
            return tested ? PairKind.Tested : PairKind.NotTested;
        }

        /// <summary>
        /// The rows of these categories. With a <paramref name="group"/>, a category's
        /// rows in that discipline group are used ("Walls" under STRUCTURE for ST Walls);
        /// a category with no row in the group falls back to all its rows.
        /// </summary>
        private List<Row> RowsOf(IEnumerable<string> categories, string group)
        {
            var list = new List<Row>();
            foreach (string c in categories)
            {
                List<Row> rows;
                if (!_rows.TryGetValue(c, out rows)) continue;

                List<Row> inGroup = group == null
                    ? rows
                    : rows.FindAll(r => r.Discipline.IndexOf(group, StringComparison.OrdinalIgnoreCase) >= 0);
                list.AddRange(inGroup.Count > 0 ? inGroup : rows);
            }
            return list;
        }

        /// <summary>The standard's rule for a pair it does not list: row weighting + column weighting - 1, as a Navisworks priority.</summary>
        /// <summary>
        /// Maps a matrix priority to the three Navisworks has: 1 = High, 2 = Medium,
        /// 3 = Low (and 0 = none). A five-step matrix (1 High, 2 Medium High, 3 Medium,
        /// 4 Medium Low, 5 Low) folds into them as 1 -> 1, 2..3 -> 2, 4..5 -> 3. Navisworks
        /// cannot show any other value - Clash Detective crashes on a test with priority 4 or 5.
        /// </summary>
        public static int ToNavisworksPriority(int matrixPriority)
        {
            if (matrixPriority <= 0) return 0;
            if (matrixPriority == 1) return 1;
            if (matrixPriority <= 3) return 2;
            return 3;
        }

        public static int PriorityFromWeights(int leftWeight, int rightWeight)
        {
            return ToNavisworksPriority(leftWeight + rightWeight - 1);
        }

        private static long Key(int row, int col)
        {
            return ((long)row << 32) | (uint)col;
        }

        internal static ClashStandard Parse(TextReader reader)
        {
            var standard = new ClashStandard();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f[0] == "R" && f.Length >= 6)
                {
                    var row = new Row
                    {
                        Index = int.Parse(f[1], CultureInfo.InvariantCulture),
                        Category = f[2],
                        Discipline = f[3],
                        Weight = int.Parse(f[5], CultureInfo.InvariantCulture)
                    };
                    List<Row> rows;
                    if (!standard._rows.TryGetValue(row.Category, out rows))
                    {
                        rows = new List<Row>();
                        standard._rows[row.Category] = rows;
                    }
                    rows.Add(row);
                }
                else if (f[0] == "M" && f.Length >= 4)
                {
                    double tol;
                    var cell = new Cell
                    {
                        Priority = int.Parse(f[3], CultureInfo.InvariantCulture),
                        ToleranceMm = f.Length > 4 && double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out tol)
                            ? tol : (double?)null
                    };
                    standard._cells[Key(int.Parse(f[1], CultureInfo.InvariantCulture),
                        int.Parse(f[2], CultureInfo.InvariantCulture))] = cell;
                }
            }
            Watchdog.Log("Built-in clash standard: " + standard._rows.Count + " categories, " + standard._cells.Count + " cells");
            return standard;
        }

        private sealed class Row
        {
            public int Index;
            public string Category;
            public string Discipline;
            public int Weight;
        }

        private struct Cell
        {
            public int Priority;
            public double? ToleranceMm;
        }
    }
}
