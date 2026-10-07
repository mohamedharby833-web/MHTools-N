using System.Collections.Generic;

namespace ClashMatrix.Model
{
    /// <summary>
    /// The result of parsing an Excel clash matrix: the category names it references
    /// and the clash tests, plus any parse warnings.
    /// </summary>
    public class MatrixParseResult
    {
        public string BatchName { get; set; }

        /// <summary>Unique category names (the matrix's Description column).</summary>
        public List<string> Categories { get; set; }

        public List<ClashTestDefinition> Tests { get; set; }
        public List<string> Warnings { get; set; }

        public MatrixParseResult()
        {
            Categories = new List<string>();
            Tests = new List<ClashTestDefinition>();
            Warnings = new List<string>();
        }
    }
}
