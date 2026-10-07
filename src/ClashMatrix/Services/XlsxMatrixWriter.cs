using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClashMatrix.Model;

namespace ClashMatrix.Services
{
    /// <summary>
    /// Writes a clash-matrix workbook (.xlsx) from a parsed result: a "Matrix" sheet
    /// (filled cell = a clash test) and a "Tolerance" sheet (per-test tolerance in
    /// mm). Used to export the matrix for the user to edit, then re-import.
    ///
    /// No third-party dependencies - the workbook is written directly as OOXML.
    /// </summary>
    public static class XlsxMatrixWriter
    {
        public static void Write(string path, MatrixParseResult result)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Categories.Count; i++)
                index[result.Categories[i]] = i;

            var matrixCells = new Dictionary<string, string>(); // "row,col" -> priority
            var tolCells = new Dictionary<string, string>();    // "row,col" -> tolerance
            foreach (ClashTestDefinition t in result.Tests)
            {
                int row = index[t.LeftName];
                int col = index[t.RightName];
                string key = row + "," + col;
                matrixCells[key] = t.Priority.ToString(CultureInfo.InvariantCulture);
                tolCells[key] = t.ToleranceMm.ToString("0.##", CultureInfo.InvariantCulture);
            }

            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
                WriteEntry(zip, "_rels/.rels", RootRelsXml);
                WriteEntry(zip, "xl/workbook.xml", WorkbookXml);
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
                WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(result.Categories, matrixCells));
                WriteEntry(zip, "xl/worksheets/sheet2.xml", BuildSheet(result.Categories, tolCells));
            }
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

        private static string BuildSheet(IList<string> categories, Dictionary<string, string> cells)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");

            // Row 1: title.
            sb.Append("<row r=\"1\">");
            sb.Append(InlineStr("A1", "Clash Matrix"));
            sb.Append("</row>");

            // Row 2: category headers, columns F onward.
            sb.Append("<row r=\"2\">");
            sb.Append(InlineStr("B2", "Category"));
            for (int i = 0; i < categories.Count; i++)
                sb.Append(InlineStr(Col(6 + i) + "2", categories[i]));
            sb.Append("</row>");

            // Data rows: column B = category, cells filled where a test exists.
            for (int i = 0; i < categories.Count; i++)
            {
                int row = 6 + i;
                sb.Append("<row r=\"" + row + "\">");
                sb.Append(InlineStr("B" + row, categories[i]));
                for (int j = 0; j < i; j++)
                {
                    if (cells.TryGetValue(i + "," + j, out string value))
                        sb.Append(Num(Col(6 + j) + row, value));
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string InlineStr(string cellRef, string value)
        {
            return "<c r=\"" + cellRef + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                   + XmlEscape(value) + "</t></is></c>";
        }

        private static string Num(string cellRef, string value)
        {
            return "<c r=\"" + cellRef + "\"><v>" + value + "</v></c>";
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

        private const string ContentTypesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
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
            "</Relationships>";
    }
}
