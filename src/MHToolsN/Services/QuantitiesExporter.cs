using System;
using System.Collections.Generic;
using System.Linq;

namespace MHToolsN.Services
{
    /// <summary>
    /// The Quantification quantities as an Excel workbook for the duct factory: a
    /// Ductwork sheet (one row per Revit type and size: W, H, pieces, length, perimeter,
    /// sheet thickness, sheet area, weight, with totals), the fittings and accessories by
    /// size, the insulation by size, every item, and the sheet rule used. Reads
    /// <see cref="TakeoffService.Summary"/>, so it shows what Quantification holds now;
    /// metric Project Setup assumed (m, m², m³, kg).
    /// </summary>
    public static class QuantitiesExporter
    {
        public static void WriteExcel(string path, List<Dictionary<string, object>> rows, DuctSheet sheet)
        {
            var tables = new List<ResultsExporter.Table>();

            var ducts = new ResultsExporter.Table("Ductwork",
                new[] { "Discipline", "Revit type", "Size", "Width (mm)", "Height (mm)", "Pieces", "Length (m)", "Perimeter (m)", "Sheet (mm)", "Sheet area (m²)", "Weight (kg)" },
                new[] { 22, 48, 16, 11, 11, 9, 12, 13, 11, 15, 13 }) { TotalRow = true };
            double length = 0, area = 0, weight = 0;
            int pieces = 0;
            foreach (var r in Rows(rows, "Ducts"))
            {
                ducts.Rows.Add(new object[]
                {
                    Segment(r, 0), Segment(r, 2), Text(r, "item"), Mm(r, "Width"), Mm(r, "Height"), Pieces(r), Num(r, "Length"), Num(r, "Perimeter"),
                    Mm(r, "Thickness"), Num(r, "Area"), Num(r, "Weight")
                });
                pieces += Pieces(r);
                length += Num(r, "Length") ?? 0;
                area += Num(r, "Area") ?? 0;
                weight += Num(r, "Weight") ?? 0;
            }
            ducts.Rows.Add(new object[] { "Total", null, null, null, null, pieces, length, null, null, area, weight });
            tables.Add(ducts);

            var fittings = new ResultsExporter.Table("Fittings and accessories",
                new[] { "Discipline", "Category", "Revit type", "Size", "Pieces", "Sheet (mm)" },
                new[] { 22, 18, 48, 30, 9, 11 }) { TotalRow = true };
            int fittingPieces = 0;
            foreach (string category in new[] { "Duct Fittings", "Duct Accessories", "Flex Ducts" })
                foreach (var r in Rows(rows, category))
                {
                    fittings.Rows.Add(new object[] { Segment(r, 0), category, Segment(r, 2), Text(r, "item"), Pieces(r), Mm(r, "Thickness") });
                    fittingPieces += Pieces(r);
                }
            fittings.Rows.Add(new object[] { "Total", null, null, null, fittingPieces, null });
            tables.Add(fittings);

            var insulation = new ResultsExporter.Table("Insulation",
                new[] { "Discipline", "Category", "Revit type", "Duct size - thickness", "Pieces", "Length (m)", "Area (m²)", "Volume (m³)" },
                new[] { 22, 18, 36, 24, 9, 12, 12, 12 }) { TotalRow = true };
            double insLength = 0, insArea = 0, insVolume = 0;
            int insPieces = 0;
            foreach (string category in new[] { "Duct Insulations", "Duct Linings" })
                foreach (var r in Rows(rows, category))
                {
                    insulation.Rows.Add(new object[] { Segment(r, 0), category, Segment(r, 2), Text(r, "item"), Pieces(r), Num(r, "Length"), Num(r, "Area"), Num(r, "Volume") });
                    insPieces += Pieces(r);
                    insLength += Num(r, "Length") ?? 0;
                    insArea += Num(r, "Area") ?? 0;
                    insVolume += Num(r, "Volume") ?? 0;
                }
            insulation.Rows.Add(new object[] { "Total", null, null, null, insPieces, insLength, insArea, insVolume });
            tables.Add(insulation);

            var all = new ResultsExporter.Table("All items",
                new[] { "Group", "Item", "Objects", "Length (m)", "Width (m)", "Thickness (m)", "Height (m)", "Perimeter (m)", "Area (m²)", "Volume (m³)", "Weight (kg)", "Count" },
                new[] { 60, 44, 9, 12, 11, 13, 11, 13, 12, 12, 12, 9 });
            foreach (var r in rows)
                all.Rows.Add(new object[]
                {
                    Text(r, "group"), Text(r, "item"), Pieces(r), Num(r, "Length"), Num(r, "Width"), Num(r, "Thickness"), Num(r, "Height"), Num(r, "Perimeter"),
                    Num(r, "Area"), Num(r, "Volume"), Num(r, "Weight"), Num(r, "Count")
                });
            tables.Add(all);

            var rule = new ResultsExporter.Table("Sheet rule", new[] { "Setting", "Value" }, new[] { 34, 70 });
            rule.Rows.Add(new object[] { "Standard", sheet.Standard });
            rule.Rows.Add(new object[] { "Thickness by longest side (mm : mm)", sheet.StepsText });
            rule.Rows.Add(new object[] { "Density (kg/m³)", sheet.Density });
            rule.Rows.Add(new object[] { "Allowance (%)", sheet.AllowancePercent });
            rule.Rows.Add(new object[] { "Weight", "sheet area x thickness x density x (1 + allowance); sheet area = Revit duct Area (outer surface)" });
            rule.Rows.Add(new object[] { "Exported", DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            tables.Add(rule);

            ResultsExporter.XlsxTables.Write(path, tables);
        }

        /// <summary>The items under "discipline / category / type" for this category, in Summary order.</summary>
        private static IEnumerable<Dictionary<string, object>> Rows(List<Dictionary<string, object>> rows, string category)
        {
            return rows.Where(r => Pieces(r) > 0 && string.Equals(Segment(r, 1), category, StringComparison.OrdinalIgnoreCase) && Segment(r, 2) != null);
        }

        private static string Segment(Dictionary<string, object> r, int index)
        {
            object p;
            var path = r.TryGetValue("path", out p) ? p as List<string> : null;
            return path != null && path.Count > index ? path[index] : null;
        }

        private static string Text(Dictionary<string, object> r, string key)
        {
            object v;
            return r.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        private static int Pieces(Dictionary<string, object> r)
        {
            object v;
            return r.TryGetValue("objects", out v) && v != null ? (int)Convert.ToInt64(v) : 0;
        }

        private static double? Num(Dictionary<string, object> r, string key)
        {
            object v;
            if (!r.TryGetValue(key, out v) || v == null) return null;
            double d = Convert.ToDouble(v);
            return d == 0 ? (double?)null : d;
        }

        private static double? Mm(Dictionary<string, object> r, string key)
        {
            double? m = Num(r, key);
            return m.HasValue ? Math.Round(m.Value * 1000, 2) : (double?)null;
        }
    }
}
