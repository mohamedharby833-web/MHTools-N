namespace MHToolsN.Model
{
    /// <summary>
    /// One clash test derived from a filled matrix cell. Left = the matrix ROW
    /// set name, Right = the matrix COLUMN set name, so the test name reads
    /// "{Row}-vs-{Col}" (e.g. "Doors-vs-Windows"). Each side is linked to the
    /// search set of the same name in the "Clash Matrix" selection-set folder.
    /// </summary>
    public class ClashTestDefinition
    {
        public string LeftName { get; set; }
        public string RightName { get; set; }
        public string Name { get; set; }

        /// <summary>Ticked in the Clash Tests tab (created). Unticked tests are left out.</summary>
        public bool Include { get; set; } = true;

        /// <summary>True once the user typed a name: renumbering then leaves it alone.</summary>
        public bool CustomName { get; set; }

        /// <summary>Tolerance in millimetres (from the Tolerance sheet, else the dialog default).</summary>
        public double ToleranceMm { get; set; }

        /// <summary>
        /// True when <see cref="ToleranceMm"/> came from the Tolerance sheet. Tests
        /// without one follow the dialog's tolerance at the moment tests are created.
        /// </summary>
        public bool ToleranceFromSheet { get; set; }

        /// <summary>
        /// Clash test priority: the matrix cell value (the 1/2/3 weighting). Applied
        /// on Navisworks 2026 and later; earlier releases have no priority in the API.
        /// </summary>
        public int Priority { get; set; }

        /// <summary>Order-independent key, so "A-vs-B" and "B-vs-A" are the same test.</summary>
        public static string PairKey(string a, string b)
        {
            string x = (a ?? "").ToUpperInvariant();
            string y = (b ?? "").ToUpperInvariant();
            return string.CompareOrdinal(x, y) <= 0 ? x + "||" + y : y + "||" + x;
        }
    }
}
