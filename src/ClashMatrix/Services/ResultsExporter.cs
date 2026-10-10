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
    /// Writes a <see cref="ClashReport"/> as an Excel workbook (a Summary sheet per test
    /// and a Results sheet per clash) and as a PDF report (a summary page, then every
    /// clash, with its picture when images were read). No third-party dependencies.
    /// </summary>
    public static class ResultsExporter
    {
        // Brand and status colours (status colours follow Clash Detective).
        private const string Accent = "0B6E6E";
        private const string AccentTint = "E1F1F0";
        private const string Ink = "17202A";
        private const string Muted = "5B6672";
        private const string Rule = "D5DBE1";

        private static readonly Dictionary<string, string> StatusColour = new Dictionary<string, string>
        {
            { "New", "E53935" }, { "Active", "FB8C00" }, { "Reviewed", "1E88E5" },
            { "Approved", "43A047" }, { "Resolved", "F2C200" }
        };

        // ================================================================ Excel

        /// <summary>One column of the Results sheet: shown when its content option is on.</summary>
        private sealed class Column
        {
            public string Header;
            public int Width;
            public Func<TestReport, ResultRow, object> Value;
            public bool Show;
        }

        private static Column Col(string header, int width, bool show, Func<TestReport, ResultRow, object> value)
        {
            return new Column { Header = header, Width = width, Show = show, Value = value };
        }

        public static void WriteExcel(string path, ClashReport report, ReportOptions options)
        {
            var tables = new List<Table>();

            if (options.Summary)
            {
                var summary = new Table("Summary",
                    new[] { "No.", "Clash test", "Type", "Tolerance (mm)", "Priority", "Test status", "Last run",
                            "Total", "New", "Active", "Reviewed", "Approved", "Resolved" },
                    new[] { 7, 48, 10, 17, 11, 14, 18, 10, 9, 10, 13, 13, 13 });
                int n = 0;
                foreach (TestReport t in report.Tests)
                {
                    summary.Rows.Add(new object[]
                    {
                        ++n, t.Name, t.TestType, t.ToleranceMm, t.Priority, t.Status, Date(t.LastRun), t.Results.Count,
                        t.Count("New"), t.Count("Active"), t.Count("Reviewed"), t.Count("Approved"), t.Count("Resolved")
                    });
                }
                summary.Rows.Add(new object[]
                {
                    null, "Total", null, null, null, null, null, report.Tests.Sum(t => t.Results.Count),
                    report.Tests.Sum(t => t.Count("New")), report.Tests.Sum(t => t.Count("Active")),
                    report.Tests.Sum(t => t.Count("Reviewed")), report.Tests.Sum(t => t.Count("Approved")),
                    report.Tests.Sum(t => t.Count("Resolved"))
                });
                summary.TotalRow = true;
                tables.Add(summary);
            }

            ReportOptions o = options;
            Func<ResultRow, ItemInfo> one = r => r.Item1 ?? ItemInfo.Empty, two = r => r.Item2 ?? ItemInfo.Empty;
            var columns = new List<Column>
            {
                Col("Clash test", 40, true, (t, r) => t.Name),
                Col("Group", 16, o.ClashGroup, (t, r) => r.Group),
                Col("Clash", 12, true, (t, r) => r.Name),
                Col("Status", 12, o.Status, (t, r) => r.Status),
                Col("Priority", 11, o.Priority, (t, r) => r.Priority),
                Col("Distance (mm)", 16, o.Distance, (t, r) => r.DistanceMm),
                Col("X (m)", 10, o.ClashPoint, (t, r) => r.X),
                Col("Y (m)", 10, o.ClashPoint, (t, r) => r.Y),
                Col("Z (m)", 10, o.ClashPoint, (t, r) => r.Z),
                Col("Item 1 category", 20, true, (t, r) => one(r).Category),
                Col("Item 1 type", 30, true, (t, r) => one(r).Type ?? one(r).Name),
                Col("Item 1 element ID", 19, o.ItemId, (t, r) => one(r).ElementId),
                Col("Item 1 layer", 16, o.LayerName, (t, r) => one(r).Layer ?? one(r).Level),
                Col("Item 1 path", 60, o.ItemPath, (t, r) => one(r).Path),
                Col("Item 1 file", 34, true, (t, r) => one(r).File),
                Col("Item 2 category", 20, true, (t, r) => two(r).Category),
                Col("Item 2 type", 30, true, (t, r) => two(r).Type ?? two(r).Name),
                Col("Item 2 element ID", 19, o.ItemId, (t, r) => two(r).ElementId),
                Col("Item 2 layer", 16, o.LayerName, (t, r) => two(r).Layer ?? two(r).Level),
                Col("Item 2 path", 60, o.ItemPath, (t, r) => two(r).Path),
                Col("Item 2 file", 34, true, (t, r) => two(r).File),
                Col("Date found", 17, o.DateFound, (t, r) => Date(r.Created)),
                Col("Assigned to", 16, o.AssignedTo, (t, r) => r.AssignedTo),
                Col("Approved by", 16, o.ApprovedBy, (t, r) => r.ApprovedBy),
                Col("Date approved", 17, o.DateApproved, (t, r) => Date(r.ApprovedTime)),
                Col("Resolved by", 16, o.ResolvedBy, (t, r) => r.ResolvedBy),
                Col("Date resolved", 17, o.DateResolved, (t, r) => Date(r.ResolvedTime)),
                Col("Description", 30, o.Description, (t, r) => r.Description),
                Col("Comments", 40, o.Comments, (t, r) => r.Comments),
            };
            columns = columns.Where(c => c.Show).ToList();

            var results = new Table("Results", columns.Select(c => c.Header).ToArray(), columns.Select(c => c.Width).ToArray());
            results.StatusColumn = columns.FindIndex(c => c.Header == "Status");
            foreach (TestReport t in report.Tests)
                foreach (ResultRow r in t.Results)
                    results.Rows.Add(columns.Select(c => c.Value(t, r)).ToArray());
            tables.Add(results);

            XlsxTables.Write(path, tables);
        }

        private static string Date(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        }

        // ================================================================ PDF

        private const double Margin = 28;
        private const double ContentTop = 74;
        private const double ContentBottom = PdfWriter.PageHeight - 42;

        public static void WritePdf(string path, ClashReport report, string product, ReportOptions options)
        {
            var pdf = new PdfWriter();
            var layout = new PdfLayout(pdf, report, options);

            if (options.Summary)
            {
                layout.NewPage();
                SummaryPage(layout, report);
            }

            foreach (TestReport t in report.Tests)
                TestSection(layout, t);

            if (pdf.PageCount == 0)
                layout.NewPage();

            // Header and footer on every page, now that the page count is known.
            for (int p = 1; p <= pdf.PageCount; p++)
            {
                pdf.SelectPage(p);
                PageFrame(pdf, report, product, p, pdf.PageCount);
            }
            pdf.Save(path);
        }

        private sealed class PdfLayout
        {
            public readonly PdfWriter Pdf;
            public readonly ClashReport Report;
            public readonly ReportOptions Options;
            public double Y;

            public PdfLayout(PdfWriter pdf, ClashReport report, ReportOptions options)
            {
                Pdf = pdf;
                Report = report;
                Options = options;
            }

            public void NewPage()
            {
                Pdf.NewPage();
                Y = ContentTop;
            }

            /// <summary>Starts a new page when <paramref name="height"/> more does not fit.</summary>
            public bool Need(double height)
            {
                if (Pdf.PageCount > 0 && Y + height <= ContentBottom) return false;
                NewPage();
                return true;
            }
        }

        private static void PageFrame(PdfWriter pdf, ClashReport report, string product, int page, int pages)
        {
            double w = PdfWriter.PageWidth;
            pdf.FillRect(0, 0, w, 6, Accent);

            // The MH mark, as in the add-in's windows.
            pdf.FillRect(Margin, 20, 30, 30, Accent);
            pdf.Text(Margin + 6.5, 40, "MH", 12, true, "FFFFFF");
            pdf.Text(Margin + 40, 34, "Clash Results", 16, true, Ink);
            pdf.Text(Margin + 40, 48, report.DocumentName ?? "", 9, false, Muted, 300);
            pdf.TextRight(w - Margin, 34, report.Exported.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), 9, false, Muted);
            pdf.TextRight(w - Margin, 48, report.Tests.Count + " test(s), " + report.Tests.Sum(t => t.Results.Count) + " clash(es)", 9, false, Muted);
            pdf.Line(Margin, 60, w - Margin, 60, Rule);

            double footer = PdfWriter.PageHeight - 24;
            pdf.Line(Margin, footer - 12, w - Margin, footer - 12, Rule);
            pdf.Text(Margin, footer, product + " - Mohamed Ali Harby", 8, false, Muted);
            pdf.TextRight(w - Margin, footer, "Page " + page + " of " + pages, 8, false, Muted);
        }

        private static void SummaryPage(PdfLayout l, ClashReport report)
        {
            PdfWriter pdf = l.Pdf;
            double x = Margin, right = PdfWriter.PageWidth - Margin;

            // Totals by status.
            int total = report.Tests.Sum(t => t.Results.Count);
            double chipW = (right - x - 5 * 8) / 6.0;
            Chip(pdf, x, l.Y, chipW, "Total", total, Accent);
            for (int i = 0; i < ClashReport.Statuses.Length; i++)
            {
                string s = ClashReport.Statuses[i];
                Chip(pdf, x + (i + 1) * (chipW + 8), l.Y, chipW, s, report.Tests.Sum(t => t.Count(s)), StatusColour[s]);
            }
            l.Y += 58;

            // One row per test.
            string[] heads = { "No.", "Clash test", "Tol.", "Priority", "Status", "Total", "New", "Active", "Reviewed", "Approved", "Resolved" };
            double[] widths = { 22, 180, 32, 44, 52, 32, 30, 36, 46, 46, 44 };
            double scale = (right - x) / widths.Sum();
            for (int i = 0; i < widths.Length; i++) widths[i] *= scale;

            Action header = () =>
            {
                pdf.FillRect(x, l.Y, right - x, 18, Accent);
                double cx = x;
                for (int i = 0; i < heads.Length; i++)
                {
                    pdf.Text(cx + 4, l.Y + 12.5, heads[i], 7.5, true, "FFFFFF", widths[i] - 6);
                    cx += widths[i];
                }
                l.Y += 18;
            };

            pdf.Text(x, l.Y, "Clash tests", 11, true, Ink);
            l.Y += 8;
            header();

            int n = 0;
            foreach (TestReport t in report.Tests)
            {
                if (l.Need(16)) header();
                if (n % 2 == 1) pdf.FillRect(x, l.Y, right - x, 16, "F4F6F8");
                string[] cells =
                {
                    (++n).ToString(CultureInfo.InvariantCulture), t.Name, Mm(t.ToleranceMm), t.Priority, t.Status,
                    t.Results.Count.ToString(CultureInfo.InvariantCulture),
                    t.Count("New").ToString(CultureInfo.InvariantCulture), t.Count("Active").ToString(CultureInfo.InvariantCulture),
                    t.Count("Reviewed").ToString(CultureInfo.InvariantCulture), t.Count("Approved").ToString(CultureInfo.InvariantCulture),
                    t.Count("Resolved").ToString(CultureInfo.InvariantCulture)
                };
                double cx = x;
                for (int i = 0; i < cells.Length; i++)
                {
                    pdf.Text(cx + 4, l.Y + 11, cells[i], 8, i == 1, Ink, widths[i] - 6);
                    cx += widths[i];
                }
                pdf.Line(x, l.Y + 16, right, l.Y + 16, "E4E8EC", 0.4);
                l.Y += 16;
            }
        }

        private static void Chip(PdfWriter pdf, double x, double y, double w, string label, int value, string colour)
        {
            pdf.FillRect(x, y, w, 44, "F4F6F8");
            pdf.FillRect(x, y, 4, 44, colour);
            pdf.Text(x + 12, y + 16, label, 8, false, Muted);
            pdf.Text(x + 12, y + 35, value.ToString("N0", CultureInfo.InvariantCulture), 16, true, Ink);
        }

        private static void TestSection(PdfLayout l, TestReport t)
        {
            PdfWriter pdf = l.Pdf;
            double x = Margin, right = PdfWriter.PageWidth - Margin;
            bool images = l.Options.Image && t.Results.Any(r => r.Image != null);

            // Tests follow on from one another; one starts a new page only when its
            // heading and first row do not fit.
            double first = 42 + (t.Results.Count > 0 ? 28 + (images ? 70 : 34) : 20);
            if (pdf.PageCount == 0 || l.Y + first > ContentBottom)
                l.NewPage();
            else if (l.Y > ContentTop)
                l.Y += 14;

            pdf.FillRect(x, l.Y, right - x, 34, AccentTint);
            pdf.FillRect(x, l.Y, 4, 34, Accent);
            pdf.Text(x + 12, l.Y + 14, t.Name, 11, true, Ink, right - x - 24);
            string facts = t.TestType + " test  |  tolerance " + Mm(t.ToleranceMm) + " mm" +
                           (string.IsNullOrEmpty(t.Priority) ? "" : "  |  priority " + t.Priority) +
                           "  |  " + t.Results.Count + " clash(es)" + (t.Results.Count > 0 ? ": " : "") +
                           string.Join(", ", ClashReport.Statuses.Where(s => t.Count(s) > 0).Select(s => t.Count(s) + " " + s)) +
                           (t.LastRun.HasValue ? "  |  last run " + Date(t.LastRun) : "");
            pdf.Text(x + 12, l.Y + 27, facts, 8, false, Muted, right - x - 24);
            l.Y += 42;

            if (t.Results.Count == 0)
            {
                pdf.Text(x, l.Y + 8, "No clashes in this test.", 8.5, false, Muted);
                l.Y += 16;
                return;
            }
            ResultTable(l, t, images);
        }

        /// <summary>One line of text in a table cell, already wrapped to the cell.</summary>
        private sealed class CellLine
        {
            public string Text;
            public double Size;
            public bool Bold;
            public string Colour;

            /// <summary>A coloured square before the text (a status), or null.</summary>
            public string Dot;

            public double Leading { get { return Size * 1.25; } }
        }

        private static void Add(List<CellLine> lines, string text, double width, double size, bool bold, string colour,
            int maxLines = 99, string dot = null)
        {
            if (string.IsNullOrWhiteSpace(text) || width <= 0) return;
            if (dot != null) width -= 8;
            List<string> parts = PdfWriter.Wrap(text.Trim(), size, bold, width);
            for (int i = 0; i < parts.Count && i < maxLines; i++)
            {
                string s = i == maxLines - 1 && parts.Count > maxLines ? PdfWriter.Fit(parts[i] + " ...", size, bold, width) : parts[i];
                lines.Add(new CellLine { Text = s, Size = size, Bold = bold, Colour = colour, Dot = i == 0 ? dot : null });
            }
        }

        private static double Height(List<CellLine> lines)
        {
            return lines.Sum(c => c.Leading);
        }

        private static void Draw(PdfWriter pdf, List<CellLine> lines, double x, double y)
        {
            foreach (CellLine c in lines)
            {
                y += c.Leading;
                double baseline = y - c.Size * 0.3;
                double tx = x;
                if (c.Dot != null)
                {
                    pdf.FillRect(x, baseline - c.Size * 0.62, 5, 5, c.Dot);
                    tx += 8;
                }
                pdf.Text(tx, baseline, c.Text, c.Size, c.Bold, c.Colour);
            }
        }

        /// <summary>
        /// The clashes of a test as a table, A4 portrait, as in Clash Detective's own
        /// report: Image | Clash | Item 1 (ID, path) | Item 2 (ID, path) - with whatever
        /// else the chosen contents ask for in the clash cell and on a line under the items.
        /// </summary>
        private static void ResultTable(PdfLayout l, TestReport t, bool images)
        {
            PdfWriter pdf = l.Pdf;
            ReportOptions o = l.Options;
            double x = Margin, width = PdfWriter.PageWidth - 2 * Margin;

            const double imageBoxH = 56;
            double imageW = images ? 78 : 0;
            const double clashW = 66;
            double itemW = (width - imageW - clashW) / 2;
            double idW = o.ItemId ? 52 : 0;
            double detailW = itemW - idW;
            string detailTitle = o.ItemPath ? "Path" : "Element";
            const string Grid = "CBD2D9";

            Action header = () =>
            {
                double y0 = l.Y;
                pdf.FillRect(x, y0, width, 28, Accent);
                double cx = x;
                if (images)
                {
                    pdf.Text(cx + 4, y0 + 18, "Image", 8, true, "FFFFFF");
                    cx += imageW;
                    pdf.Line(cx, y0, cx, y0 + 28, "FFFFFF", 0.5);
                }
                pdf.Text(cx + 4, y0 + 18, "Clash", 8, true, "FFFFFF");
                cx += clashW;
                for (int k = 1; k <= 2; k++)
                {
                    pdf.Line(cx, y0, cx, y0 + 28, "FFFFFF", 0.5);
                    string title = "Item " + k;
                    pdf.Text(cx + itemW / 2 - PdfWriter.Width(title, 8.5, true) / 2, y0 + 10.5, title, 8.5, true, "FFFFFF");
                    pdf.Line(cx, y0 + 14, cx + itemW, y0 + 14, "FFFFFF", 0.5);
                    if (idW > 0)
                    {
                        pdf.Text(cx + 4, y0 + 24, "Item ID", 7.5, true, "FFFFFF");
                        pdf.Line(cx + idW, y0 + 14, cx + idW, y0 + 28, "FFFFFF", 0.5);
                    }
                    pdf.Text(cx + idW + 4, y0 + 24, detailTitle, 7.5, true, "FFFFFF");
                    cx += itemW;
                }
                l.Y += 28;
            };
            header();

            foreach (ResultRow r in t.Results)
            {
                // What goes into each cell.
                var clash = new List<CellLine>();
                double cw = clashW - 8;
                Add(clash, r.Name, cw, 7.5, true, Ink);
                if (o.Status) Add(clash, r.Status, cw, 6.8, false, Ink, 1, StatusColour.ContainsKey(r.Status) ? StatusColour[r.Status] : Muted);
                if (o.Priority && !string.IsNullOrEmpty(r.Priority)) Add(clash, "Priority " + r.Priority, cw, 6.8, false, Muted);
                if (o.Distance) Add(clash, Mm(r.DistanceMm) + " mm", cw, 6.8, false, Muted);
                if (o.ClashGroup && !string.IsNullOrEmpty(r.Group)) Add(clash, r.Group, cw, 6.8, false, Muted, 2);
                if (o.DateFound && r.Created.HasValue) Add(clash, "Found " + Day(r.Created), cw, 6.5, false, Muted);
                if (o.AssignedTo && !string.IsNullOrEmpty(r.AssignedTo)) Add(clash, "Assigned " + r.AssignedTo, cw, 6.5, false, Muted, 2);
                if (o.ApprovedBy && !string.IsNullOrEmpty(r.ApprovedBy)) Add(clash, "Approved by " + r.ApprovedBy, cw, 6.5, false, Muted, 2);
                if (o.DateApproved && r.ApprovedTime.HasValue) Add(clash, "Approved " + Day(r.ApprovedTime), cw, 6.5, false, Muted);
                if (o.ResolvedBy && !string.IsNullOrEmpty(r.ResolvedBy)) Add(clash, "Resolved by " + r.ResolvedBy, cw, 6.5, false, Muted, 2);
                // A short description sits in the clash cell, a long one on the line under the items.
                bool longDescription = o.Description && !string.IsNullOrWhiteSpace(r.Description) && PdfWriter.Wrap(r.Description.Trim(), 6.5, false, cw).Count > 3;
                if (o.Description && !longDescription) Add(clash, r.Description, cw, 6.5, false, Ink, 3);
                if (o.DateResolved && r.ResolvedTime.HasValue) Add(clash, "Resolved " + Day(r.ResolvedTime), cw, 6.5, false, Muted);

                var ids = new[] { IdCell(r.Item1, idW - 8), IdCell(r.Item2, idW - 8) };
                var details = new[] { DetailCell(o, r.Item1, detailW - 8), DetailCell(o, r.Item2, detailW - 8) };

                var note = new List<CellLine>();
                Add(note, Join("   |   ",
                    o.ClashPoint ? "Location " + M(r.X) + ", " + M(r.Y) + ", " + M(r.Z) + " m" : null,
                    longDescription ? "Description: " + r.Description : null,
                    o.Comments && !string.IsNullOrWhiteSpace(r.Comments) ? "Comments: " + r.Comments : null),
                    width - imageW - 8, 6.8, false, Ink, 3);

                double textH = new[] { Height(clash), Height(ids[0]), Height(ids[1]), Height(details[0]), Height(details[1]) }.Max();
                double noteH = note.Count > 0 ? Height(note) + 5 : 0;
                double h = Math.Max(images ? imageBoxH + 6 : 0, textH + noteH + 8);

                if (l.Need(h))
                {
                    pdf.Text(x, l.Y + 8, t.Name + "  (continued)", 8, true, Muted, width);
                    l.Y += 14;
                    header();
                }

                double y = l.Y;
                double cx = x;
                if (images)
                {
                    if (r.Image != null)
                        pdf.Jpeg(r.Image, r.ImageWidth, r.ImageHeight, cx + 3, y + 3, imageW - 6, h - 6);
                    cx += imageW;
                    pdf.Line(cx, y, cx, y + h, Grid, 0.5);
                }
                Draw(pdf, clash, cx + 4, y + 3);
                cx += clashW;
                for (int k = 0; k < 2; k++)
                {
                    pdf.Line(cx, y, cx, y + h - noteH, Grid, 0.5);
                    if (idW > 0)
                    {
                        Draw(pdf, ids[k], cx + 4, y + 3);
                        pdf.Line(cx + idW, y, cx + idW, y + h - noteH, Grid, 0.5);
                    }
                    Draw(pdf, details[k], cx + idW + 4, y + 3);
                    cx += itemW;
                }
                if (note.Count > 0)
                {
                    double ny = y + h - noteH;
                    pdf.Line(x + imageW, ny, x + width, ny, Grid, 0.4);
                    Draw(pdf, note, x + imageW + 4, ny + 1);
                }
                pdf.StrokeRect(x, y, width, h, Grid);
                l.Y += h;
            }
        }

        private static List<CellLine> IdCell(ItemInfo item, double width)
        {
            var lines = new List<CellLine>();
            item = item ?? ItemInfo.Empty;
            if (width <= 0 || string.IsNullOrEmpty(item.ElementId)) return lines;
            Add(lines, "Element ID:", width, 6.3, false, Muted);
            Add(lines, item.ElementId, width, 7.5, false, Ink);
            return lines;
        }

        private static List<CellLine> DetailCell(ReportOptions o, ItemInfo item, double width)
        {
            var lines = new List<CellLine>();
            item = item ?? ItemInfo.Empty;
            if (o.LayerName && !string.IsNullOrEmpty(item.Layer ?? item.Level))
                Add(lines, "Layer: " + (item.Layer ?? item.Level), width, 6.5, true, Muted, 2);
            if (o.ItemPath && !string.IsNullOrEmpty(item.Path))
            {
                Add(lines, item.Path, width, 6.8, false, Ink, 8);
            }
            else
            {
                string type = string.IsNullOrEmpty(item.Type) ? item.Name : item.Type;
                Add(lines, Join(" | ", item.Category, type), width, 7.3, false, Ink, 3);
                Add(lines, item.File, width, 6.5, false, Muted, 2);
            }
            return lines;
        }

        private static string Day(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }

        private static string Join(string separator, params string[] parts)
        {
            return string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string Mm(double mm)
        {
            return mm.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string M(double m)
        {
            return m.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // ================================================================ tables -> xlsx

        private sealed class Table
        {
            public readonly string Name;
            public readonly string[] Headers;
            public readonly int[] Widths;
            public readonly List<object[]> Rows = new List<object[]>();
            public int StatusColumn = -1;
            public bool TotalRow;

            public Table(string name, string[] headers, int[] widths)
            {
                Name = name;
                Headers = headers;
                Widths = widths;
            }
        }

        /// <summary>Plain OOXML for a few styled tables: header row, frozen and filtered; status cells coloured.</summary>
        private static class XlsxTables
        {
            // cellXfs: 0 default, 1 header, 2 text, 3 number 0.0, 4 number 0.000, 5 integer,
            // 6.. one per status (in ClashReport.Statuses order), then total row.
            private const int SHeader = 1, SText = 2, SNumber1 = 3, SNumber3 = 4, SInteger = 5, SStatus = 6;
            private static readonly int STotal = SStatus + ClashReport.Statuses.Length;

            public static void Write(string path, IList<Table> tables)
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    var types = new StringBuilder();
                    var sheets = new StringBuilder();
                    var rels = new StringBuilder();
                    for (int i = 0; i < tables.Count; i++)
                    {
                        types.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                        sheets.Append("<sheet name=\"" + Esc(tables[i].Name) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
                        rels.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
                        Entry(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", Sheet(tables[i]));
                    }
                    rels.Append("<Relationship Id=\"rId" + (tables.Count + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");

                    Entry(zip, "[Content_Types].xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + types + "</Types>");
                    Entry(zip, "_rels/.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Entry(zip, "xl/workbook.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                        sheets + "</sheets></workbook>");
                    Entry(zip, "xl/_rels/workbook.xml.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>");
                    Entry(zip, "xl/styles.xml", Styles());
                }
            }

            private static string Sheet(Table t)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                sb.Append("<cols>");
                for (int c = 0; c < t.Widths.Length; c++)
                    sb.Append("<col min=\"" + (c + 1) + "\" max=\"" + (c + 1) + "\" width=\"" + t.Widths[c] + "\" customWidth=\"1\"/>");
                sb.Append("</cols><sheetData>");

                sb.Append("<row r=\"1\" ht=\"22\" customHeight=\"1\">");
                for (int c = 0; c < t.Headers.Length; c++)
                    sb.Append(Str(Ref(c, 1), t.Headers[c], SHeader));
                sb.Append("</row>");

                for (int r = 0; r < t.Rows.Count; r++)
                {
                    int rowNumber = r + 2;
                    bool total = t.TotalRow && r == t.Rows.Count - 1;
                    sb.Append("<row r=\"" + rowNumber + "\">");
                    object[] row = t.Rows[r];
                    for (int c = 0; c < row.Length; c++)
                    {
                        object v = row[c];
                        if (v == null) { if (total) sb.Append("<c r=\"" + Ref(c, rowNumber) + "\" s=\"" + STotal + "\"/>"); continue; }
                        if (v is string)
                        {
                            int style = total ? STotal : SText;
                            int si = Array.IndexOf(ClashReport.Statuses, (string)v);
                            if (c == t.StatusColumn && si >= 0) style = SStatus + si;
                            if ((string)v != "") sb.Append(Str(Ref(c, rowNumber), (string)v, style));
                        }
                        else if (v is int)
                        {
                            sb.Append(Num(Ref(c, rowNumber), ((int)v).ToString(CultureInfo.InvariantCulture), total ? STotal : SInteger));
                        }
                        else if (v is double)
                        {
                            double d = (double)v;
                            int style = t.Headers[c].Contains("(m)") ? SNumber3 : SNumber1;
                            sb.Append(Num(Ref(c, rowNumber), d.ToString("R", CultureInfo.InvariantCulture), style));
                        }
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData>");
                int lastRow = t.Rows.Count + 1 - (t.TotalRow ? 1 : 0);
                sb.Append("<autoFilter ref=\"A1:" + Ref(t.Headers.Length - 1, Math.Max(1, lastRow)) + "\"/>");
                sb.Append("</worksheet>");
                return sb.ToString();
            }

            private static string Styles()
            {
                string[] statusFills = { "FFFCE4E4", "FFFFEBD3", "FFDCEBFB", "FFDFF1E0", "FFFFF6CC" };
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                sb.Append("<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"0.0\"/><numFmt numFmtId=\"165\" formatCode=\"0.000\"/></numFmts>");
                sb.Append("<fonts count=\"3\"><font><sz val=\"10\"/><name val=\"Segoe UI\"/></font>" +
                          "<font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Segoe UI\"/></font>" +
                          "<font><b/><sz val=\"10\"/><name val=\"Segoe UI\"/></font></fonts>");
                sb.Append("<fills count=\"" + (4 + statusFills.Length) + "\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");
                sb.Append(Fill("FF" + Accent) + Fill("FF" + AccentTint));
                foreach (string f in statusFills) sb.Append(Fill(f));
                sb.Append("</fills>");
                sb.Append("<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
                          "<border><left/><right/><top/><bottom style=\"hair\"><color rgb=\"FFCBD2D9\"/></bottom><diagonal/></border></borders>");
                sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
                sb.Append("<cellXfs count=\"" + (STotal + 1) + "\">");
                sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
                sb.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf>");
                sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/>");
                sb.Append("<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>");
                sb.Append("<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>");
                sb.Append("<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>");
                for (int i = 0; i < statusFills.Length; i++)
                    sb.Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"" + (4 + i) + "\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>");
                sb.Append("<xf numFmtId=\"1\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>");
                sb.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                return sb.ToString();
            }

            private static string Fill(string argb)
            {
                return "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + argb + "\"/><bgColor indexed=\"64\"/></patternFill></fill>";
            }

            private static void Entry(ZipArchive zip, string name, string content)
            {
                using (Stream s = zip.CreateEntry(name).Open())
                using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                    w.Write(content);
            }

            private static string Str(string cellRef, string value, int style)
            {
                return "<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Esc(value) + "</t></is></c>";
            }

            private static string Num(string cellRef, string value, int style)
            {
                return "<c r=\"" + cellRef + "\" s=\"" + style + "\"><v>" + value + "</v></c>";
            }

            private static string Ref(int col, int row)
            {
                string s = "";
                int n = col + 1;
                while (n > 0) { int m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; }
                return s + row;
            }

            private static string Esc(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                var sb = new StringBuilder(s.Length);
                foreach (char c in s)
                {
                    if (c == '&') sb.Append("&amp;");
                    else if (c == '<') sb.Append("&lt;");
                    else if (c == '>') sb.Append("&gt;");
                    else if (c == '"') sb.Append("&quot;");
                    else if (c < 32 && c != '\t' && c != '\n' && c != '\r') sb.Append(' ');
                    else sb.Append(c);
                }
                return sb.ToString();
            }
        }
    }
}
