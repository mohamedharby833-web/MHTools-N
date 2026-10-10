using System;
using System.Collections.Generic;
using System.Linq;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// The order of a matrix's sets (by discipline, as in the exported workbook), the
    /// discipline codes, and the clash test names that follow from them:
    /// <c>[Number] - [Discipline 1] [CATEGORY 1] - [Discipline 2] [CATEGORY 2]</c>,
    /// e.g. <c>381 - AR WALLS - ST FLOORS</c>.
    /// </summary>
    public static class MatrixLayout
    {
        /// <summary>Discipline for a set whose discipline is not known.</summary>
        public const string GeneralDiscipline = "GEN";

        private static readonly string[] DisciplineOrder =
            { "ARCHITECTURE", "STRUCTURE", "MECHANICAL", "ELECTRICAL", "INFORMATION", "SERVICES", "INFRASTRUCTURE" };

        /// <summary>
        /// The sets grouped by discipline (Architecture, Structure, Mechanical,
        /// Electrical, ICT, Services, Infrastructure, then the rest), keeping their order
        /// within each group.
        /// </summary>
        public static List<string> Order(MatrixParseResult result)
        {
            return result.Categories
                .Select((name, i) => new { name, i, rank = DisciplineRank(DisciplineOf(result, name)) })
                .OrderBy(x => x.rank).ThenBy(x => x.i)
                .Select(x => x.name)
                .ToList();
        }

        /// <summary>The set's discipline heading ("ARCHITECTURE - AR", ...), or "OTHER".</summary>
        public static string DisciplineOf(MatrixParseResult result, string name)
        {
            string d;
            return result.Disciplines.TryGetValue(name, out d) && !string.IsNullOrWhiteSpace(d) ? d : "OTHER";
        }

        /// <summary>
        /// The short code of a discipline heading: the letters after its last '-'
        /// ("ARCHITECTURE - AR" -> "AR", "STRUCTURE -ST" -> "ST"), else its first two
        /// letters, else <see cref="GeneralDiscipline"/>.
        /// </summary>
        public static string DisciplineCode(string heading)
        {
            if (string.IsNullOrWhiteSpace(heading) || heading == "OTHER")
                return GeneralDiscipline;

            int dash = heading.LastIndexOf('-');
            if (dash >= 0)
            {
                string code = new string(heading.Substring(dash + 1).Where(char.IsLetterOrDigit).ToArray());
                if (code.Length >= 2 && code.Length <= 4)
                    return code.ToUpperInvariant();
            }

            string letters = new string(heading.Where(char.IsLetter).ToArray());
            return letters.Length >= 2 ? letters.Substring(0, 2).ToUpperInvariant() : GeneralDiscipline;
        }

        private static int DisciplineRank(string discipline)
        {
            for (int i = 0; i < DisciplineOrder.Length; i++)
            {
                if (discipline.IndexOf(DisciplineOrder[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return DisciplineOrder.Length;
        }

        /// <summary>
        /// A set as it reads in a test name: "AR WALLS". A set already named after its
        /// discipline ("AR Walls") is not prefixed a second time.
        /// </summary>
        public static string Label(MatrixParseResult result, string setName)
        {
            string code = DisciplineCode(DisciplineOf(result, setName));
            string name = setName.ToUpperInvariant();
            return name.StartsWith(code + " ", StringComparison.Ordinal) ? name : code + " " + name;
        }

        /// <summary>
        /// Numbers and names every test, reading the matrix row by row in
        /// <see cref="Order"/>: the set earlier in that order is side 1 (Left), the
        /// later one side 2 (Right). Tests are also put in that order.
        /// </summary>
        public static void ApplyTestNames(MatrixParseResult result)
        {
            List<string> order = Order(result);
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++)
                index[order[i]] = i;

            Func<string, int> at = name =>
            {
                int i;
                return index.TryGetValue(name, out i) ? i : int.MaxValue;
            };

            foreach (ClashTestDefinition t in result.Tests)
            {
                if (at(t.LeftName) > at(t.RightName))
                {
                    string left = t.LeftName;
                    t.LeftName = t.RightName;
                    t.RightName = left;
                }
            }

            // The ticked tests are numbered first (1..N, no gaps); the unticked ones after.
            List<ClashTestDefinition> sorted = result.Tests
                .OrderBy(t => result.IsIncluded(t) ? 0 : 1)
                .ThenBy(t => at(t.RightName)).ThenBy(t => at(t.LeftName))
                .ToList();

            for (int n = 0; n < sorted.Count; n++)
            {
                ClashTestDefinition t = sorted[n];
                if (!t.CustomName)
                    t.Name = (n + 1) + " - " + Label(result, t.LeftName) + " - " + Label(result, t.RightName);
            }

            result.Tests.Clear();
            result.Tests.AddRange(sorted);
        }
    }
}
