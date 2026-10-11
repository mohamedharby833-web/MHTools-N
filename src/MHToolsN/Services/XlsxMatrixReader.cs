using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using MHToolsN.Model;

namespace MHToolsN.Services
{
    /// <summary>
    /// Reads an Excel clash matrix (.xlsx) with no third-party dependencies
    /// (System.IO.Compression + System.Xml only) and turns it into a list of clash
    /// tests between category names.
    ///
    /// Expected layout:
    ///   Sheet "Matrix"   - a triangular grid. Rows 1..5 are column headers
    ///                       (1 = Discipline, 2 = Description, 3 = Weighting,
    ///                        4 = Abbreviation, 5 = Revit Category); columns A..E
    ///                       are the matching row labels; data starts at F6.
    ///                       A filled cell = one pair to clash-test (either side of
    ///                       the diagonal; A-vs-B and B-vs-A are the same test). The
    ///                       value is the test priority: 1 High, 2 Medium, 3 Low (a
    ///                       1..5 matrix is folded into those three - see
    ///                       ClashStandard.ToNavisworksPriority).
    ///   Sheet "Tolerance" - the same grid, each filled cell = tolerance in mm.
    ///
    /// Each Description (column B / row 2) becomes one search set, so a test is named
    /// "{RowDescription}-vs-{ColDescription}". A Description can span several rows,
    /// one per Revit category ("Revit Category", column E / row 5); its search set
    /// then matches all of them. Without a Revit Category the Description itself is
    /// taken as the Revit category name.
    /// </summary>
    public static class XlsxMatrixReader
    {
        private const int HeaderRows = 5;       // rows 1..5 are column headers
        private const int LabelColumns = 5;     // columns A..E are row labels
        private const int DescriptionHeaderRow = 2;
        private const int RevitCategoryHeaderRow = 5;
        private const string RevitCategoryColumn = "E";
        private const string MatrixSheet = "Matrix";
        private const string ToleranceSheet = "Tolerance";

        public static MatrixParseResult Parse(string xlsxPath, double defaultToleranceMm)
        {
            // FileShare.ReadWrite: the workbook may well still be open in Excel.
            using (var stream = new FileStream(xlsxPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                string[] sharedStrings = ReadSharedStrings(zip);
                Dictionary<string, string> sheetFiles = ReadSheetFileMap(zip);

                if (!sheetFiles.ContainsKey(MatrixSheet))
                    throw new InvalidDataException(
                        "The workbook has no sheet named '" + MatrixSheet + "'. Expected sheets: '" +
                        MatrixSheet + "' and '" + ToleranceSheet + "'.");

                Sheet matrix = ReadSheet(zip, sheetFiles[MatrixSheet], sharedStrings);

                Sheet tolerance = null;
                if (sheetFiles.ContainsKey(ToleranceSheet))
                    tolerance = ReadSheet(zip, sheetFiles[ToleranceSheet], sharedStrings);

                return BuildResult(matrix, tolerance, defaultToleranceMm,
                    Path.GetFileNameWithoutExtension(xlsxPath));
            }
        }

        // ------------------------------------------------------------------ build

        private static MatrixParseResult BuildResult(Sheet matrix, Sheet tolerance,
            double defaultToleranceMm, string batchName)
        {
            var result = new MatrixParseResult { BatchName = batchName };

            // The workbook has a "legend" (template documentation) below the data
            // (rows 138+) and to its right (columns EH+). Bound the matrix to the
            // real data so those labels are never read as categories.
            int dataColEnd = FindDataColEnd(matrix);
            int dataRowEnd = FindDataRowEnd(matrix, dataColEnd);

            var members = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            // Description (column B) carries down through each row group; the Revit
            // Category (column E) of every row in the group belongs to its search set.
            // The weighting (column C) and discipline (column A) of each group are kept
            // for the exported workbook.
            var rowDesc = new string[matrix.MaxRow + 1];
            string cur = null;
            string discipline = null;
            for (int r = HeaderRows + 1; r <= dataRowEnd; r++)
            {
                string b = Clean(matrix.Get("B", r));
                if (b != null) cur = b;
                rowDesc[r] = cur;

                string a = Clean(matrix.Get("A", r));
                if (a != null) discipline = a;
                if (cur == null)
                    continue;

                AddMembers(members, cur, matrix.Get(RevitCategoryColumn, r));
                if (discipline != null && !result.Disciplines.ContainsKey(cur))
                    result.Disciplines[cur] = discipline;

                int weight, known;
                if (int.TryParse(Clean(matrix.Get("C", r)), NumberStyles.Integer, CultureInfo.InvariantCulture, out weight)
                    && weight > 0 && (!result.Weights.TryGetValue(cur, out known) || weight < known))
                    result.Weights[cur] = weight;
            }

            // Description (header row 2) carries right through each column group,
            // with the Revit Category in header row 5.
            var colDesc = new string[matrix.MaxCol + 1];
            cur = null;
            for (int c = LabelColumns + 1; c <= dataColEnd; c++)
            {
                string b = Clean(matrix.Get(ColumnName(c), DescriptionHeaderRow));
                if (b != null) cur = b;
                colDesc[c] = cur;
                if (cur != null)
                    AddMembers(members, cur, matrix.Get(ColumnName(c), RevitCategoryHeaderRow));
            }

            // In the workbook's own order (rows first), so an exported matrix reads back
            // with the same order - and the same test numbers.
            var categories = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tests = new Dictionary<string, ClashTestDefinition>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            int diagonal = 0;
            int mirrored = 0;
            var badTolerance = new List<string>();
            int toleranceFormulas = 0;

            for (int r = HeaderRows + 1; r <= dataRowEnd; r++)
            {
                string rowName = rowDesc[r];
                if (string.IsNullOrEmpty(rowName))
                    continue;
                if (seen.Add(rowName)) categories.Add(rowName);

                for (int c = LabelColumns + 1; c <= dataColEnd; c++)
                {
                    string colName = colDesc[c];
                    if (string.IsNullOrEmpty(colName))
                        continue;

                    string cell = Clean(matrix.Get(ColumnName(c), r));
                    if (string.IsNullOrEmpty(cell))
                        continue;

                    int priority;
                    int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);

                    if (seen.Add(colName)) categories.Add(colName);

                    // Same set on both sides (a group crossing itself): not a pair test.
                    if (string.Equals(rowName, colName, StringComparison.OrdinalIgnoreCase))
                    {
                        diagonal++;
                        continue;
                    }

                    double tol = defaultToleranceMm;
                    bool fromSheet = false;
                    if (tolerance != null)
                    {
                        string tolCell = Clean(tolerance.Get(ColumnName(c), r));
                        double parsed;
                        if (tolCell != null)
                        {
                            // A tolerance is typed in. A formula there is, in the sample
                            // template, a leftover copy of the priority formula - its
                            // 1/2/3 are priorities, not millimetres.
                            if (tolerance.IsFormula(ColumnName(c), r))
                            {
                                toleranceFormulas++;
                            }
                            else if (UnitConverter.TryParseMm(tolCell, out parsed))
                            {
                                tol = parsed;
                                fromSheet = true;
                            }
                            else if (badTolerance.Count < 5)
                            {
                                badTolerance.Add(ColumnName(c) + r + " ('" + tolCell + "')");
                            }
                        }
                    }

                    string key = ClashTestDefinition.PairKey(rowName, colName);
                    ClashTestDefinition test;
                    if (!tests.TryGetValue(key, out test))
                    {
                        test = new ClashTestDefinition
                        {
                            LeftName = rowName,
                            RightName = colName,
                            Name = rowName + "-vs-" + colName,
                            ToleranceMm = tol,
                            ToleranceFromSheet = fromSheet,
                            Priority = priority
                        };
                        tests[key] = test;
                        order.Add(key);
                    }
                    else
                    {
                        if (!string.Equals(test.LeftName, rowName, StringComparison.OrdinalIgnoreCase))
                            mirrored++;

                        // Several category pairs can sit under one group-pair; keep the
                        // tightest sheet tolerance so nothing is missed.
                        if (fromSheet && (!test.ToleranceFromSheet || tol < test.ToleranceMm))
                        {
                            test.ToleranceMm = tol;
                            test.ToleranceFromSheet = true;
                        }
                        if (priority != 0 && (test.Priority == 0 || priority < test.Priority))
                            test.Priority = priority;
                    }
                }
            }

            result.Categories.AddRange(categories);
            foreach (string name in categories)
            {
                SortedSet<string> set;
                // No Revit Category given: the Description is the category, with its
                // fittings folded in exactly as a model scan does.
                result.SetMembers[name] = members.TryGetValue(name, out set) && set.Count > 0
                    ? new List<string>(set)
                    : NavisClashCreator.GetMergedCategories(name);
            }
            foreach (string key in order)
                result.Tests.Add(tests[key]);

            if (diagonal > 0)
                result.Warnings.Add(diagonal + " filled cell(s) pair a group with itself and were skipped (no self-clash tests).");
            if (mirrored > 0)
                result.Warnings.Add(mirrored + " pair(s) are filled on both sides of the diagonal; each is one test.");
            if (toleranceFormulas > 0)
                result.Warnings.Add(toleranceFormulas + " tolerance cell(s) hold a formula instead of a typed value and were ignored (default tolerance used). Such cells are usually leftover copies of the priority formula.");
            if (badTolerance.Count > 0)
                result.Warnings.Add("Tolerance cells that are not numbers (default used): " + string.Join(", ", badTolerance));

            // Navisworks has three priorities (High, Medium, Low). A matrix on a 1..5
            // scale is folded into them; one already on 1..3 is kept as it is.
            int highest = result.Tests.Count > 0 ? result.Tests.Max(t => t.Priority) : 0;
            if (highest > 3)
            {
                foreach (ClashTestDefinition t in result.Tests)
                    t.Priority = ClashStandard.ToNavisworksPriority(t.Priority);
                result.Warnings.Add("The matrix uses priorities 1-5; Navisworks has three, so 1 = High, 2-3 = Medium, 4-5 = Low.");
            }
            else
            {
                foreach (ClashTestDefinition t in result.Tests)
                    t.Priority = Math.Max(0, t.Priority);
            }

            MatrixLayout.ApplyTestNames(result);
            return result;
        }

        /// <summary>Adds the Revit categories in a "Revit Category" cell (comma / semicolon / line separated) to a set.</summary>
        private static void AddMembers(Dictionary<string, SortedSet<string>> members, string setName, string cell)
        {
            if (string.IsNullOrWhiteSpace(cell))
                return;

            SortedSet<string> set;
            if (!members.TryGetValue(setName, out set))
            {
                set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                members[setName] = set;
            }
            foreach (string part in cell.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string category = part.Trim();
                if (category.Length > 0 && !IsLegendLabel(category))
                    set.Add(category);
            }
        }

        /// <summary>Last data column: the right-most column whose Description header is a real category name.</summary>
        private static int FindDataColEnd(Sheet matrix)
        {
            int end = LabelColumns;
            for (int c = LabelColumns + 1; c <= matrix.MaxCol; c++)
            {
                string desc = Clean(matrix.Get(ColumnName(c), DescriptionHeaderRow));
                if (desc != null && !IsLegendLabel(desc))
                    end = c;
            }
            return end;
        }

        /// <summary>Last data row: the bottom-most row with any marked matrix cell.</summary>
        private static int FindDataRowEnd(Sheet matrix, int dataColEnd)
        {
            int end = HeaderRows;
            for (int r = HeaderRows + 1; r <= matrix.MaxRow; r++)
            {
                for (int c = LabelColumns + 1; c <= dataColEnd; c++)
                {
                    if (Clean(matrix.Get(ColumnName(c), r)) != null)
                    {
                        end = r;
                        break;
                    }
                }
            }
            return end;
        }

        private static bool IsLegendLabel(string s)
        {
            switch (s)
            {
                case "Discipline":
                case "Description":
                case "Weighting":
                case "Abbreviation":
                case "Revit Category":
                case "Priority Key":
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ sheet

        private sealed class Sheet
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public readonly Dictionary<string, string> MergeAnchors = new Dictionary<string, string>();
            public readonly HashSet<string> Formulas = new HashSet<string>();
            public int MaxRow;
            public int MaxCol;

            /// <summary>True when the cell holds a formula (its value is the formula's cached result).</summary>
            public bool IsFormula(string column, int row)
            {
                return Formulas.Contains(column + row);
            }

            public string Get(string column, int row)
            {
                string key = column + row;
                string value;
                if (Values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                    return value;

                string anchor;
                if (MergeAnchors.TryGetValue(key, out anchor) && Values.TryGetValue(anchor, out value))
                    return value;

                return null;
            }
        }

        private static Sheet ReadSheet(ZipArchive zip, string path, string[] sharedStrings)
        {
            var sheet = new Sheet();
            ZipArchiveEntry entry = zip.GetEntry(path);
            if (entry == null)
                return sheet;

            using (Stream stream = entry.Open())
            {
                var doc = new XmlDocument();
                doc.Load(stream);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("d", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                foreach (XmlNode mc in doc.SelectNodes("//d:mergeCell", ns))
                {
                    string range = mc.Attributes["ref"] != null ? mc.Attributes["ref"].Value : null;
                    if (string.IsNullOrEmpty(range))
                        continue;

                    int c1, r1, c2, r2;
                    ParseRange(range, out c1, out r1, out c2, out r2);
                    string topLeft = ColumnName(c1) + r1;
                    for (int r = r1; r <= r2; r++)
                        for (int c = c1; c <= c2; c++)
                            sheet.MergeAnchors[ColumnName(c) + r] = topLeft;
                }

                foreach (XmlNode c in doc.SelectNodes("//d:c", ns))
                {
                    string refAttr = c.Attributes["r"] != null ? c.Attributes["r"].Value : null;
                    if (string.IsNullOrEmpty(refAttr))
                        continue;

                    string t = c.Attributes["t"] != null ? c.Attributes["t"].Value : null;
                    string value = "";

                    if (t == "s")
                    {
                        XmlNode v = c.SelectSingleNode("d:v", ns);
                        if (v != null)
                        {
                            int idx;
                            if (int.TryParse(v.InnerText, NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out idx)
                                && idx >= 0 && idx < sharedStrings.Length)
                                value = sharedStrings[idx];
                        }
                    }
                    else if (t == "inlineStr")
                    {
                        XmlNode tn = c.SelectSingleNode("d:is/d:t", ns);
                        if (tn != null)
                            value = tn.InnerText;
                    }
                    else if (t == "str" || t == "b" || t == "e" || t == null)
                    {
                        XmlNode v = c.SelectSingleNode("d:v", ns);
                        if (v != null)
                            value = v.InnerText;
                    }

                    sheet.Values[refAttr] = value;
                    if (c.SelectSingleNode("d:f", ns) != null)
                        sheet.Formulas.Add(refAttr);

                    int colIdx, rowIdx;
                    ParseRef(refAttr, out colIdx, out rowIdx);
                    if (rowIdx > sheet.MaxRow) sheet.MaxRow = rowIdx;
                    if (colIdx > sheet.MaxCol) sheet.MaxCol = colIdx;
                }
            }
            return sheet;
        }

        // ------------------------------------------------------------------ workbook parts

        private static string[] ReadSharedStrings(ZipArchive zip)
        {
            ZipArchiveEntry entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return new string[0];

            var list = new List<string>();
            using (Stream stream = entry.Open())
            {
                var doc = new XmlDocument();
                doc.Load(stream);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("d", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                foreach (XmlNode si in doc.SelectNodes("//d:si", ns))
                {
                    var sb = new StringBuilder();
                    foreach (XmlNode t in si.SelectNodes(".//d:t", ns))
                        sb.Append(t.InnerText);
                    list.Add(sb.ToString());
                }
            }
            return list.ToArray();
        }

        private static Dictionary<string, string> ReadSheetFileMap(ZipArchive zip)
        {
            var map = new Dictionary<string, string>();

            var rels = new Dictionary<string, string>();
            ZipArchiveEntry relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry != null)
            {
                using (Stream stream = relsEntry.Open())
                {
                    var doc = new XmlDocument();
                    doc.Load(stream);
                    foreach (XmlNode rel in doc.SelectNodes("//*[local-name()='Relationship']"))
                    {
                        string id = rel.Attributes["Id"] != null ? rel.Attributes["Id"].Value : null;
                        string target = rel.Attributes["Target"] != null ? rel.Attributes["Target"].Value : null;
                        if (id != null && target != null)
                            rels[id] = target;
                    }
                }
            }

            ZipArchiveEntry wbEntry = zip.GetEntry("xl/workbook.xml");
            if (wbEntry == null)
                return map;

            using (Stream stream = wbEntry.Open())
            {
                var doc = new XmlDocument();
                doc.Load(stream);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("d", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                foreach (XmlNode sh in doc.SelectNodes("//d:sheet", ns))
                {
                    string name = sh.Attributes["name"] != null ? sh.Attributes["name"].Value : null;
                    XmlAttribute idAttr = sh.Attributes["id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"];
                    if (name == null || idAttr == null)
                        continue;

                    string target;
                    if (rels.TryGetValue(idAttr.Value, out target))
                        map[name] = NormalizeTarget(target);
                }
            }
            return map;
        }

        private static string NormalizeTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
                return target;
            target = target.TrimStart('/');
            return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
        }

        // ------------------------------------------------------------------ helpers

        private static string ColumnName(int oneBasedIndex)
        {
            string s = "";
            int n = oneBasedIndex;
            while (n > 0)
            {
                int m = (n - 1) % 26;
                s = (char)('A' + m) + s;
                n = (n - 1) / 26;
            }
            return s;
        }

        private static void ParseRef(string refStr, out int column, out int row)
        {
            column = 0;
            row = 0;
            int i = 0;
            while (i < refStr.Length && char.IsLetter(refStr[i]))
            {
                column = column * 26 + (char.ToUpperInvariant(refStr[i]) - 'A' + 1);
                i++;
            }
            if (i < refStr.Length)
                int.TryParse(refStr.Substring(i), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out row);
        }

        private static void ParseRange(string refStr, out int c1, out int r1, out int c2, out int r2)
        {
            string[] parts = refStr.Split(':');
            ParseRef(parts[0], out c1, out r1);
            if (parts.Length > 1)
                ParseRef(parts[1], out c2, out r2);
            else
            {
                c2 = c1;
                r2 = r1;
            }
        }

        private static string Clean(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
    }
}
