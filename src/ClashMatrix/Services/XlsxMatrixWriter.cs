using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Writes a clash-matrix workbook (.xlsx) in the look of the sample Clash Detection
    /// Matrix: a "Matrix" sheet (filled cell = a clash test, value = priority) and a
    /// "Tolerance" sheet (per-test tolerance in mm).
    ///
    /// The layout is the one <see cref="XlsxMatrixReader"/> reads - Description in
    /// column B / row 2, Revit Category in column E / row 5, data from F6 - so an
    /// export can be edited and re-imported unchanged. The look follows the sample
    /// sheet: dark discipline banners, grey Description and beige Revit Category
    /// headers (text turned 90°), hairline cell borders, and the same priority
    /// colours as conditional formatting (1 High red, 2 Medium amber, 3 Low yellow
    /// tints), so the colours follow when a priority is edited in Excel. A Priority
    /// Key sits to the right of the matrix, as in the sample matrix.
    ///
    /// No third-party dependencies - the workbook is written directly as OOXML.
    /// </summary>
    public static class XlsxMatrixWriter
    {
        private const int FirstDataRow = 6;
        private const int FirstDataCol = 6; // F

        // Matrix colours.
        private const string BannerColour = "FF333E48";
        private const string DescriptionColour = "FFD9D9D6";
        private const string CategoryColour = "FFEFECEA";

        /// <summary>Priority 1..3 fill colours (Navisworks has High, Medium, Low).</summary>
        private static readonly string[] PriorityColours = { "FFFF0000", "FFFFC000", "FFFFF2CC" };
        private static readonly string[] PriorityNames = { "High", "Medium", "Low" };

        // Cell style indexes in StylesXml (cellXfs).
        private const int SBanner = 1, SDescHeader = 2, SWeight = 3, SCatHeader = 4, SRowDesc = 5,
            SRowCat = 6, SData = 7, SDiagonal = 8, SBannerVertical = 9, STitle = 10, SLegend = 11;


        public static void Write(string path, MatrixParseResult result)
        {
            List<string> categories = MatrixLayout.Order(result);

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < categories.Count; i++)
                index[categories[i]] = i;

            var matrixCells = new Dictionary<string, string>(); // "row,col" -> priority
            var tolCells = new Dictionary<string, string>();    // "row,col" -> tolerance
            foreach (ClashTestDefinition t in result.Tests)
            {
                int a, b;
                if (!index.TryGetValue(t.LeftName, out a) || !index.TryGetValue(t.RightName, out b) || a == b)
                    continue;

                // Below the diagonal, whichever way round the test was defined.
                string key = Math.Max(a, b) + "," + Math.Min(a, b);
                matrixCells[key] = (t.Priority > 0 ? t.Priority : 1).ToString(CultureInfo.InvariantCulture);
                if (t.ToleranceFromSheet)
                    tolCells[key] = t.ToleranceMm.ToString("0.####", CultureInfo.InvariantCulture);
            }

            var layout = new Layout(result, categories);

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
                WriteEntry(zip, "_rels/.rels", RootRelsXml);
                WriteEntry(zip, "xl/workbook.xml", WorkbookXml);
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
                WriteEntry(zip, "xl/styles.xml", StylesXml());
                WriteEntry(zip, "xl/worksheets/sheet1.xml",
                    BuildSheet("Clash Matrix - priority (1 = High, 2 = Medium, 3 = Low)", layout, matrixCells, true));
                WriteEntry(zip, "xl/worksheets/sheet2.xml",
                    BuildSheet("Tolerance (mm) - blank = the dialog's default", layout, tolCells, false));
            }
        }

        /// <summary>What both sheets share: the sets, their headers, and the discipline groups.</summary>
        private sealed class Layout
        {
            public readonly IList<string> Categories;
            public readonly IList<string> Members;
            public readonly IList<string> Weights;
            public readonly List<KeyValuePair<string, int[]>> Groups = new List<KeyValuePair<string, int[]>>(); // discipline -> [first, last]

            public Layout(MatrixParseResult result, IList<string> categories)
            {
                Categories = categories;
                Members = categories.Select(c => string.Join(", ", result.GetMembers(c))).ToList();
                Weights = categories.Select(c =>
                {
                    int w;
                    return result.Weights.TryGetValue(c, out w) ? w.ToString(CultureInfo.InvariantCulture) : "";
                }).ToList();

                for (int i = 0; i < categories.Count; i++)
                {
                    string d = MatrixLayout.DisciplineOf(result, categories[i]);
                    if (Groups.Count > 0 && Groups[Groups.Count - 1].Key == d)
                        Groups[Groups.Count - 1].Value[1] = i;
                    else
                        Groups.Add(new KeyValuePair<string, int[]>(d, new[] { i, i }));
                }
            }
        }

        private static string BuildSheet(string title, Layout layout, Dictionary<string, string> cells, bool priorities)
        {
            int n = layout.Categories.Count;
            int lastCol = FirstDataCol + Math.Max(n, 1) - 1;
            int lastRow = FirstDataRow + Math.Max(n, 1) - 1;
            int keyCol = lastCol + 2;

            var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
            Action<int, int, string> put = (r, c, xml) =>
            {
                SortedDictionary<int, string> row;
                if (!rows.TryGetValue(r, out row)) rows[r] = row = new SortedDictionary<int, string>();
                row[c] = xml;
            };

            put(1, 2, Str(1, 2, title, STitle));

            // Headers: discipline banner (row 1), Description (row 2), weighting (row 3),
            // Revit Category (row 5); the same down the left for the rows.
            foreach (KeyValuePair<string, int[]> g in layout.Groups)
            {
                for (int i = g.Value[0]; i <= g.Value[1]; i++)
                {
                    put(1, FirstDataCol + i, i == g.Value[0] ? Str(1, FirstDataCol + i, g.Key, SBanner) : Blank(1, FirstDataCol + i, SBanner));
                    put(FirstDataRow + i, 1, i == g.Value[0] ? Str(FirstDataRow + i, 1, g.Key, SBannerVertical) : Blank(FirstDataRow + i, 1, SBannerVertical));
                }
            }
            put(2, 5, Str(2, 5, "Description", SRowDesc));
            put(3, 5, Str(3, 5, "Weighting", SRowDesc));
            put(5, 2, Str(5, 2, "Description", SRowDesc));
            put(5, 3, Str(5, 3, "W", SWeight));
            put(5, 5, Str(5, 5, "Revit Category", SRowDesc));

            for (int i = 0; i < n; i++)
            {
                int col = FirstDataCol + i;
                int row = FirstDataRow + i;
                put(2, col, Str(2, col, layout.Categories[i], SDescHeader));
                put(3, col, NumOrBlank(3, col, layout.Weights[i], SWeight));
                put(5, col, Str(5, col, layout.Members[i], SCatHeader));

                put(row, 2, Str(row, 2, layout.Categories[i], SRowDesc));
                put(row, 3, NumOrBlank(row, 3, layout.Weights[i], SWeight));
                put(row, 5, Str(row, 5, layout.Members[i], SRowCat));

                // Below the diagonal: a value where a test exists, a bordered blank
                // where none does; the diagonal itself is shaded like the sample matrix.
                for (int j = 0; j < i; j++)
                {
                    string value;
                    put(row, FirstDataCol + j, cells.TryGetValue(i + "," + j, out value)
                        ? Num(row, FirstDataCol + j, value, SData)
                        : Blank(row, FirstDataCol + j, SData));
                }
                put(row, col, Blank(row, col, SDiagonal));
            }

            if (priorities)
            {
                put(FirstDataRow, keyCol, Str(FirstDataRow, keyCol, "Priority Key", STitle));
                for (int p = 1; p <= PriorityNames.Length; p++)
                {
                    int r = FirstDataRow + p;
                    put(r, keyCol, Num(r, keyCol, p.ToString(CultureInfo.InvariantCulture), SData));
                    put(r, keyCol + 1, Str(r, keyCol + 1, PriorityNames[p - 1], SLegend));
                }
            }

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\" zoomScale=\"85\" zoomScaleNormal=\"85\">" +
                      "<pane xSplit=\"5\" ySplit=\"5\" topLeftCell=\"F6\" activePane=\"bottomRight\" state=\"frozen\"/>" +
                      "</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"14.4\"/>");
            sb.Append("<cols>");
            sb.Append("<col min=\"1\" max=\"1\" width=\"3.8\" customWidth=\"1\"/>");
            sb.Append("<col min=\"2\" max=\"2\" width=\"22\" customWidth=\"1\"/>");
            sb.Append("<col min=\"3\" max=\"3\" width=\"3.2\" customWidth=\"1\"/>");
            sb.Append("<col min=\"4\" max=\"4\" width=\"1.5\" customWidth=\"1\"/>");
            sb.Append("<col min=\"5\" max=\"5\" width=\"26\" customWidth=\"1\"/>");
            sb.Append("<col min=\"" + FirstDataCol + "\" max=\"" + lastCol + "\" width=\"3.3\" customWidth=\"1\"/>");
            if (priorities)
                sb.Append("<col min=\"" + (keyCol + 1) + "\" max=\"" + (keyCol + 1) + "\" width=\"12\" customWidth=\"1\"/>");
            sb.Append("</cols>");

            sb.Append("<sheetData>");
            foreach (KeyValuePair<int, SortedDictionary<int, string>> row in rows)
            {
                string height = row.Key == 2 || row.Key == 5 ? " ht=\"150\" customHeight=\"1\""
                              : row.Key == 1 ? " ht=\"18\" customHeight=\"1\""
                              : row.Key == 4 ? " ht=\"4\" customHeight=\"1\"" : "";
                sb.Append("<row r=\"" + row.Key + "\"" + height + ">");
                foreach (string cell in row.Value.Values)
                    sb.Append(cell);
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");

            // Discipline banners span their columns and rows.
            var merges = new List<string>();
            foreach (KeyValuePair<string, int[]> g in layout.Groups)
            {
                if (g.Value[1] <= g.Value[0]) continue;
                merges.Add(Col(FirstDataCol + g.Value[0]) + "1:" + Col(FirstDataCol + g.Value[1]) + "1");
                merges.Add("A" + (FirstDataRow + g.Value[0]) + ":A" + (FirstDataRow + g.Value[1]));
            }
            if (merges.Count > 0)
            {
                sb.Append("<mergeCells count=\"" + merges.Count + "\">");
                foreach (string m in merges)
                    sb.Append("<mergeCell ref=\"" + m + "\"/>");
                sb.Append("</mergeCells>");
            }

            if (priorities)
            {
                // Each priority value gets its colour, live in Excel.
                string range = Col(FirstDataCol) + FirstDataRow + ":" + Col(lastCol) + lastRow + " " +
                               Col(keyCol) + (FirstDataRow + 1) + ":" + Col(keyCol) + (FirstDataRow + PriorityNames.Length);
                sb.Append("<conditionalFormatting sqref=\"" + range + "\">");
                for (int p = 1; p <= PriorityNames.Length; p++)
                {
                    sb.Append("<cfRule type=\"cellIs\" dxfId=\"" + (p - 1) + "\" priority=\"" + p + "\" operator=\"equal\">" +
                              "<formula>" + p + "</formula></cfRule>");
                }
                sb.Append("</conditionalFormatting>");
            }

            sb.Append("<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.4\" bottom=\"0.4\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using (Stream s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            {
                w.Write(content);
            }
        }

        private static string Str(int row, int col, string value, int style)
        {
            return "<c r=\"" + Col(col) + row + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                   + XmlEscape(value) + "</t></is></c>";
        }

        private static string Num(int row, int col, string value, int style)
        {
            return "<c r=\"" + Col(col) + row + "\" s=\"" + style + "\"><v>" + value + "</v></c>";
        }

        private static string NumOrBlank(int row, int col, string value, int style)
        {
            return string.IsNullOrEmpty(value) ? Blank(row, col, style) : Num(row, col, value, style);
        }

        private static string Blank(int row, int col, int style)
        {
            return "<c r=\"" + Col(col) + row + "\" s=\"" + style + "\"/>";
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string Col(int oneBased)
        {
            string s = "";
            int n = oneBased;
            while (n > 0)
            {
                int m = (n - 1) % 26;
                s = (char)('A' + m) + s;
                n = (n - 1) / 26;
            }
            return s;
        }

        private static string Fill(string rgb)
        {
            return "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + rgb + "\"/><bgColor indexed=\"64\"/></patternFill></fill>";
        }

        private static string Xf(int font, int fill, int border, string alignment)
        {
            return "<xf numFmtId=\"0\" fontId=\"" + font + "\" fillId=\"" + fill + "\" borderId=\"" + border + "\" xfId=\"0\"" +
                   (font > 0 ? " applyFont=\"1\"" : "") + (fill > 0 ? " applyFill=\"1\"" : "") +
                   (border > 0 ? " applyBorder=\"1\"" : "") +
                   (alignment != null ? " applyAlignment=\"1\"><alignment " + alignment + "/></xf>" : "/>");
        }

        private static string StylesXml()
        {
            const string font = "<name val=\"Gentium Basic\"/><family val=\"1\"/>";
            const string hair = "<left style=\"hair\"><color auto=\"1\"/></left><right style=\"hair\"><color auto=\"1\"/></right>" +
                                "<top style=\"hair\"><color auto=\"1\"/></top><bottom style=\"hair\"><color auto=\"1\"/></bottom><diagonal/>";
            const string thin = "<left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right>" +
                                "<top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/>";
            const string center = "horizontal=\"center\" vertical=\"center\"";
            const string rotated = "horizontal=\"center\" vertical=\"center\" textRotation=\"90\" wrapText=\"1\"";
            const string left = "horizontal=\"left\" vertical=\"center\" wrapText=\"1\"";
            // Banners shrink rather than wrap or spill: a discipline with few columns still reads in full.
            const string banner = "horizontal=\"center\" vertical=\"center\" shrinkToFit=\"1\"";
            const string bannerVertical = "horizontal=\"center\" vertical=\"center\" textRotation=\"90\" shrinkToFit=\"1\"";

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            sb.Append("<fonts count=\"5\">");
            sb.Append("<font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>");       // 0 default
            sb.Append("<font><sz val=\"8\"/>" + font + "</font>");                                       // 1 cells
            sb.Append("<font><sz val=\"10\"/>" + font + "</font>");                                      // 2 headers
            sb.Append("<font><b/><sz val=\"12\"/><color rgb=\"FFFFFFFF\"/>" + font + "</font>");         // 3 banner
            sb.Append("<font><b/><sz val=\"10\"/>" + font + "</font>");                                  // 4 title
            sb.Append("</fonts>");

            sb.Append("<fills count=\"5\">");
            sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
            sb.Append(Fill(BannerColour));      // 2
            sb.Append(Fill(DescriptionColour)); // 3
            sb.Append(Fill(CategoryColour));    // 4
            sb.Append("</fills>");

            sb.Append("<borders count=\"3\">");
            sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
            sb.Append("<border>" + hair + "</border>"); // 1
            sb.Append("<border>" + thin + "</border>"); // 2
            sb.Append("</borders>");

            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

            sb.Append("<cellXfs count=\"12\">");
            sb.Append(Xf(0, 0, 0, null));     // 0 default
            sb.Append(Xf(3, 2, 2, banner));   // 1 SBanner
            sb.Append(Xf(2, 3, 2, rotated));  // 2 SDescHeader
            sb.Append(Xf(2, 0, 2, center));   // 3 SWeight
            sb.Append(Xf(1, 4, 2, rotated));  // 4 SCatHeader
            sb.Append(Xf(2, 3, 2, left));     // 5 SRowDesc
            sb.Append(Xf(1, 4, 2, left));     // 6 SRowCat
            sb.Append(Xf(1, 0, 1, center));   // 7 SData
            sb.Append(Xf(1, 2, 1, center));   // 8 SDiagonal
            sb.Append(Xf(3, 2, 2, bannerVertical)); // 9 SBannerVertical
            sb.Append(Xf(4, 0, 0, "vertical=\"center\"")); // 10 STitle
            sb.Append(Xf(1, 0, 0, "vertical=\"center\"")); // 11 SLegend
            sb.Append("</cellXfs>");

            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");

            sb.Append("<dxfs count=\"" + PriorityColours.Length + "\">");
            foreach (string colour in PriorityColours)
                sb.Append("<dxf><fill><patternFill><bgColor rgb=\"" + colour + "\"/></patternFill></fill></dxf>");
            sb.Append("</dxfs>");

            sb.Append("<tableStyles count=\"0\"/>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        private const string ContentTypesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>";

        private const string RootRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private const string WorkbookXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets>" +
            "<sheet name=\"Matrix\" sheetId=\"1\" r:id=\"rId1\"/>" +
            "<sheet name=\"Tolerance\" sheetId=\"2\" r:id=\"rId2\"/>" +
            "</sheets></workbook>";

        private const string WorkbookRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";
    }
}
