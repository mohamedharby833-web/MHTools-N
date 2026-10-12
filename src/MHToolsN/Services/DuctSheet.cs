using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MHToolsN.Services
{
    /// <summary>
    /// The sheet-metal rule the duct takeoff weighs ducts by: sheet thickness by the
    /// duct's longest side (DW/144 by default), the steel's density and an allowance
    /// for seams, flanges and stiffeners. Weight = sheet area x thickness x density x
    /// (1 + allowance). Kept in %AppData%\MHTools-N\DuctSheet.txt and editable in the
    /// Quantities window - the defaults are provisional (public sources quote an older
    /// DW/144 edition; a project spec's own table should replace it).
    /// </summary>
    public sealed class DuctSheet
    {
        public const string DefaultStandard = "DW/144";
        public const string DefaultSteps = "600:0.6, 750:0.8, 1250:1.0, 2500:1.2";

        /// <summary>Longest side up to (mm) -> sheet thickness (mm), in ascending order.</summary>
        public List<KeyValuePair<double, double>> Steps { get; private set; }
        public string Standard { get; set; }
        /// <summary>kg/m³ - galvanised steel 7850.</summary>
        public double Density { get; set; }
        /// <summary>Percent added to the sheet weight.</summary>
        public double AllowancePercent { get; set; }

        public DuctSheet()
        {
            Standard = DefaultStandard;
            Density = 7850;
            AllowancePercent = 0;
            List<KeyValuePair<double, double>> steps;
            string error;
            TryParseSteps(DefaultSteps, out steps, out error);
            Steps = steps;
        }

        /// <summary>Sheet thickness (mm) for a duct whose longest side (or diameter) is this (mm): the first step it fits, else the last.</summary>
        public double ThicknessFor(double longestSideMm)
        {
            foreach (var step in Steps)
                if (longestSideMm <= step.Key + 1e-6) return step.Value;
            return Steps.Count > 0 ? Steps[Steps.Count - 1].Value : 0;
        }

        /// <summary>kg for a sheet area (m²) at a thickness (mm).</summary>
        public double WeightKg(double areaM2, double thicknessMm)
        {
            return areaM2 * thicknessMm / 1000.0 * Density * (1 + AllowancePercent / 100.0);
        }

        public string StepsText
        {
            get { return string.Join(", ", Steps.Select(s => Num(s.Key) + ":" + Num(s.Value))); }
        }

        public void SetSteps(List<KeyValuePair<double, double>> steps)
        {
            Steps = steps;
        }

        /// <summary>"600:0.6, 750:0.8" -> ascending steps; false with a reason when it does not read.</summary>
        public static bool TryParseSteps(string text, out List<KeyValuePair<double, double>> steps, out string error)
        {
            steps = new List<KeyValuePair<double, double>>();
            error = null;
            foreach (string part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = part.Split(':');
                double upTo, t;
                if (f.Length != 2 || !TryNumber(f[0], out upTo) || !TryNumber(f[1], out t) || upTo <= 0 || t <= 0)
                {
                    error = "\"" + part.Trim() + "\" is not \"longest side mm : thickness mm\", e.g. 600:0.6.";
                    return false;
                }
                steps.Add(new KeyValuePair<double, double>(upTo, t));
            }
            if (steps.Count == 0) { error = "Give at least one step, e.g. 600:0.6."; return false; }
            steps = steps.OrderBy(s => s.Key).ToList();
            return true;
        }

        public static bool TryNumber(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static string Num(double d)
        {
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------- remembered

        private static string StorePath
        {
            get { return AppFolders.SettingsFile("DuctSheet.txt"); }
        }

        public static DuctSheet Load()
        {
            var sheet = new DuctSheet();
            try
            {
                if (!File.Exists(StorePath)) return sheet;
                foreach (string line in File.ReadAllLines(StorePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    double d;
                    List<KeyValuePair<double, double>> steps;
                    string error;
                    if (key == "Standard" && value.Length > 0) sheet.Standard = value;
                    else if (key == "Density" && TryNumber(value, out d) && d > 0) sheet.Density = d;
                    else if (key == "Allowance" && TryNumber(value, out d) && d >= 0) sheet.AllowancePercent = d;
                    else if (key == "Thickness" && TryParseSteps(value, out steps, out error)) sheet.Steps = steps;
                }
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Load duct sheet", ex);
            }
            return sheet;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                File.WriteAllLines(StorePath, new[]
                {
                    "Standard=" + Standard,
                    "Thickness=" + StepsText,
                    "Density=" + Num(Density),
                    "Allowance=" + Num(AllowancePercent)
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Save duct sheet", ex);
            }
        }
    }

    /// <summary>
    /// A duct size read from Revit's Size text: "1000x700", "1000 mmx700 mm", "250ø",
    /// and for fittings and accessories one size per connector, "300x650-250x650".
    /// </summary>
    public sealed class DuctSize
    {
        /// <summary>Per connector: width and height in mm (both the diameter for a round one).</summary>
        public readonly List<double[]> Ends = new List<double[]>();
        public readonly List<bool> RoundEnds = new List<bool>();

        public double Width { get { return Ends[0][0]; } }
        public double Height { get { return Ends[0][1]; } }
        public bool Round { get { return RoundEnds[0]; } }
        public double LongestSide { get { return Ends.Max(e => Math.Max(e[0], e[1])); } }

        /// <summary>mm: 2(W+H), or πD.</summary>
        public double Perimeter
        {
            get { return Round ? Math.PI * Width : 2 * (Width + Height); }
        }

        /// <summary>"1000 x 700", "Ø250", "300 x 650 - 250 x 650"; ends all the same size read as one ("150 x 150", not "150 x 150 - 150 x 150").</summary>
        public string Label
        {
            get
            {
                var parts = new List<string>();
                for (int i = 0; i < Ends.Count; i++)
                    parts.Add(RoundEnds[i] ? "Ø" + DuctSheet.Num(Ends[i][0]) : DuctSheet.Num(Ends[i][0]) + " x " + DuctSheet.Num(Ends[i][1]));
                return parts.Distinct().Count() == 1 ? parts[0] : string.Join(" - ", parts);
            }
        }

        private static readonly Regex Number = new Regex(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        /// <summary>The size, or null when the text holds none.</summary>
        public static DuctSize Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var size = new DuctSize();
            // Connectors are separated by "-" (Revit writes no negative sizes).
            foreach (string end in text.Split('-'))
            {
                var numbers = new List<double>();
                foreach (Match m in Number.Matches(end))
                {
                    double d;
                    if (DuctSheet.TryNumber(m.Value, out d)) numbers.Add(d);
                }
                if (numbers.Count == 0) continue;
                bool round = numbers.Count == 1 || end.IndexOf('ø') >= 0 || end.IndexOf('Ø') >= 0;
                double w = numbers[0], h = round ? numbers[0] : numbers[1];
                if (w <= 0 || h <= 0) continue;
                size.Ends.Add(new[] { w, h });
                size.RoundEnds.Add(round);
            }
            return size.Ends.Count > 0 ? size : null;
        }
    }
}
