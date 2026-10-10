using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClashMatrix.Services
{
    /// <summary>
    /// What goes into an Export Results report - the same choices as Clash Detective's
    /// own Report dialog (contents and statuses) - remembered between exports in
    /// %AppData%\ClashMatrix\ReportOptions.txt.
    /// </summary>
    public sealed class ReportOptions
    {
        /// <summary>The content choices, in the order the window lists them: key -> label.</summary>
        public static readonly KeyValuePair<string, string>[] Contents =
        {
            Pair("Summary", "Summary"),
            Pair("ClashPoint", "Clash Point"),
            Pair("DateFound", "Date Found"),
            Pair("AssignedTo", "Assigned To"),
            Pair("DateApproved", "Date Approved"),
            Pair("ApprovedBy", "Approved By"),
            Pair("DateResolved", "Date Resolved"),
            Pair("ResolvedBy", "Resolved By"),
            Pair("LayerName", "Layer Name"),
            Pair("ItemPath", "Item Path"),
            Pair("ItemId", "Item ID"),
            Pair("Status", "Status"),
            Pair("Priority", "Priority"),
            Pair("Distance", "Distance"),
            Pair("Description", "Description"),
            Pair("Comments", "Comments"),
            Pair("Image", "Image"),
            Pair("ClashGroup", "Clash Group"),
        };

        private static readonly string[] OffByDefault = { "ItemPath" };

        private readonly HashSet<string> _on = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ReportOptions()
        {
            foreach (KeyValuePair<string, string> c in Contents)
                if (!OffByDefault.Contains(c.Key)) _on.Add(c.Key);
            Statuses = new HashSet<string>(Model.ClashReport.Statuses, StringComparer.OrdinalIgnoreCase);
            Excel = true;
            Pdf = true;
        }

        public bool Has(string key) { return _on.Contains(key); }

        public void Set(string key, bool on)
        {
            if (on) _on.Add(key); else _on.Remove(key);
        }

        /// <summary>The result statuses to include (New, Active, Reviewed, Approved, Resolved).</summary>
        public HashSet<string> Statuses { get; private set; }

        public bool Excel { get; set; }
        public bool Pdf { get; set; }

        // Shorthands for the writers.
        public bool Summary { get { return Has("Summary"); } }
        public bool ClashPoint { get { return Has("ClashPoint"); } }
        public bool DateFound { get { return Has("DateFound"); } }
        public bool AssignedTo { get { return Has("AssignedTo"); } }
        public bool DateApproved { get { return Has("DateApproved"); } }
        public bool ApprovedBy { get { return Has("ApprovedBy"); } }
        public bool DateResolved { get { return Has("DateResolved"); } }
        public bool ResolvedBy { get { return Has("ResolvedBy"); } }
        public bool LayerName { get { return Has("LayerName"); } }
        public bool ItemPath { get { return Has("ItemPath"); } }
        public bool ItemId { get { return Has("ItemId"); } }
        public bool Status { get { return Has("Status"); } }
        public bool Priority { get { return Has("Priority"); } }
        public bool Distance { get { return Has("Distance"); } }
        public bool Description { get { return Has("Description"); } }
        public bool Comments { get { return Has("Comments"); } }
        public bool Image { get { return Has("Image"); } }
        public bool ClashGroup { get { return Has("ClashGroup"); } }

        private static KeyValuePair<string, string> Pair(string key, string label)
        {
            return new KeyValuePair<string, string>(key, label);
        }

        // ------------------------------------------------------------- remembered

        private static string StorePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClashMatrix", "ReportOptions.txt");
            }
        }

        public static ReportOptions Load()
        {
            var options = new ReportOptions();
            try
            {
                if (!File.Exists(StorePath)) return options;
                foreach (string line in File.ReadAllLines(StorePath, Encoding.UTF8))
                {
                    string[] f = line.Split('=');
                    if (f.Length != 2) continue;
                    string key = f[0].Trim(), value = f[1].Trim();
                    if (key == "Statuses")
                    {
                        options.Statuses.Clear();
                        foreach (string s in value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                            options.Statuses.Add(s);
                    }
                    else if (key == "Excel") options.Excel = value == "1";
                    else if (key == "Pdf") options.Pdf = value == "1";
                    else if (Contents.Any(c => c.Key == key)) options.Set(key, value == "1");
                }
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Load report options", ex);
            }
            return options;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                foreach (KeyValuePair<string, string> c in Contents)
                    lines.Add(c.Key + "=" + (Has(c.Key) ? "1" : "0"));
                lines.Add("Statuses=" + string.Join(",", Statuses));
                lines.Add("Excel=" + (Excel ? "1" : "0"));
                lines.Add("Pdf=" + (Pdf ? "1" : "0"));
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                File.WriteAllLines(StorePath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Save report options", ex);
            }
        }
    }
}
