namespace ClashMatrix.Model
{
    /// <summary>
    /// One clash test derived from a filled matrix cell. Left = the matrix ROW
    /// category name, Right = the matrix COLUMN category name, so the test name
    /// reads "{Row}-vs-{Col}" (e.g. "Doors-vs-Windows"). Category names are the
    /// plain Navisworks category names; abbreviations are dropped.
    /// </summary>
    public class ClashTestDefinition
    {
        public string LeftName { get; set; }
        public string RightName { get; set; }
        public string Name { get; set; }

        /// <summary>Tolerance in millimetres (from the Tolerance sheet, else the default).</summary>
        public double ToleranceMm { get; set; }

        /// <summary>Clash test priority (the matrix cell value, e.g. 1/2/3).</summary>
        public int Priority { get; set; }
    }
}
