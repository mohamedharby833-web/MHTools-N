using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

// Reads a clash detection matrix workbook and writes the add-in's built-in
// standard: one line per Revit-category row and one per filled matrix cell.
//
// What is read, and why:
//   - Rows are keyed by their Revit Category (column E), not their Description
//     (column B). In the MEP block of the sample matrix the Description labels are
//     shifted against the rows, while the Revit Category, the weighting and the
//     cells line up row by row - the merged groups follow column E.
//   - Priority = the Matrix cell's value (the sheet computes it as row weighting +
//     column weighting - 1; 1 = High ... 5 = Low). An empty cell = no test.
//   - Tolerance = a CONSTANT on the Tolerance sheet. Formula cells there are left-
//     over copies of the priority formula (they give 1/2/3, not millimetres) and
//     are ignored, so such a pair takes the dialog's default tolerance.
//   - Revit Category spellings that are not real Revit category names are mapped
//     to the real ones (see Rename below).
//
// Output (tab separated, UTF-8):
//   R  <row>  <revit category>  <discipline>  <description>  <weighting>
//   M  <row>  <col row>         <priority>    <tolerance mm or empty>
// A column is identified by the row it mirrors: column F <-> row 6, G <-> 7, ...

const int FirstRow = 6;
const int LastRow = 137; // the legend starts below this
XNamespace d = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ClashStandard <clash matrix .xlsx> [output .tsv]");
    return 1;
}

string input = args[0];
string output = args.Length > 1
    ? args[1]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "MHToolsN", "Resources", "ClashStandard.tsv"));

// Workbook spelling -> Revit category name.
var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    { "Wall", "Walls" },
    { "Floor", "Floors" },
    { "Rainlings", "Railings" },
    { "Special Equipments", "Specialty Equipment" },
    { "Cable Trays Accessories", "Cable Tray Fittings" },
    { "Conduits Fittings", "Conduit Fittings" },
    { "Pipe Insulation", "Pipe Insulations" },
    { "Pipes Placeholders", "Pipe Placeholders" },
    { "Structural Beam System", "Structural Beam Systems" },
    { "Mech Equip (Water Pumps)", "Mechanical Equipment" },
};

using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
using var zip = new ZipArchive(stream);

var shared = XDocument.Load(zip.GetEntry("xl/sharedStrings.xml").Open()).Root
    .Elements(d + "si").Select(si => string.Concat(si.Descendants(d + "t").Select(t => t.Value))).ToList();
var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml").Open());
var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels").Open()).Root.Elements()
    .ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));

Sheet Load(string name)
{
    XElement sheet = workbook.Descendants(d + "sheet").First(s => (string)s.Attribute("name") == name);
    string target = rels[(string)sheet.Attribute(rel + "id")].TrimStart('/');
    if (!target.StartsWith("xl/")) target = "xl/" + target;
    var doc = XDocument.Load(zip.GetEntry(target).Open());

    var result = new Sheet();
    foreach (XElement c in doc.Descendants(d + "c"))
    {
        string v = c.Element(d + "v")?.Value;
        if ((string)c.Attribute("t") == "s" && v != null) v = shared[int.Parse(v, CultureInfo.InvariantCulture)];
        string r = (string)c.Attribute("r");
        result.Values[r] = v ?? "";
        if (c.Element(d + "f") != null) result.Formulas.Add(r);
    }
    foreach (XElement m in doc.Descendants(d + "mergeCell"))
    {
        string[] parts = ((string)m.Attribute("ref")).Split(':');
        var (c1, r1) = Ref(parts[0]);
        var (c2, r2) = parts.Length > 1 ? Ref(parts[1]) : (c1, r1);
        for (int rr = r1; rr <= r2; rr++)
            for (int cc = c1; cc <= c2; cc++)
                result.Anchors[Col(cc) + rr] = Col(c1) + r1;
    }
    return result;
}

Sheet matrix = Load("Matrix");
Sheet tolerance = Load("Tolerance");

// ---- rows: every row with a Revit Category
var rows = new List<(int Row, string Category, string Discipline, string Description, int Weight)>();
for (int r = FirstRow; r <= LastRow; r++)
{
    string category = Clean(matrix.Get("E", r));
    if (category == null || category.StartsWith("XXX")) continue;
    if (rename.TryGetValue(category, out string real)) category = real;

    string discipline = Clean(matrix.Get("A", r)) ?? "";
    // A72 holds the "STRUCTURE & INFRASTRUCTURE" banner above the structure group.
    if (discipline == "STRUCTURE & INFRASTRUCTURE") discipline = "STRUCTURE -ST";
    string description = Clean(matrix.Get("B", r)) ?? "";
    int.TryParse(Clean(matrix.Get("C", r)), NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight);
    rows.Add((r, category, discipline, description, weight));
}

// ---- cells between those rows
var cells = new List<string>();
int toleranceConstants = 0, toleranceFormulas = 0;
foreach (var a in rows)
{
    foreach (var b in rows)
    {
        string cellRef = Col(b.Row) + a.Row; // row a, column mirroring row b
        string value = Clean(matrix.Get(Col(b.Row), a.Row));
        if (value == null) continue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority)) continue;

        string tol = "";
        string tolValue = Clean(tolerance.Get(Col(b.Row), a.Row));
        if (tolValue != null)
        {
            if (tolerance.Formulas.Contains(cellRef))
                toleranceFormulas++;
            else if (double.TryParse(tolValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) && mm >= 0)
            {
                tol = mm.ToString("0.###", CultureInfo.InvariantCulture);
                toleranceConstants++;
            }
        }
        cells.Add("M\t" + a.Row + "\t" + b.Row + "\t" + priority + "\t" + tol);
    }
}

var sb = new StringBuilder();
sb.AppendLine("# Built-in clash standard - generated by tools/ClashStandard");
sb.AppendLine("# R <row> <revit category> <discipline> <description> <weighting>");
sb.AppendLine("# M <row> <column row> <priority 1=High..5=Low> <tolerance mm, empty = dialog default>");
foreach (var r in rows)
    sb.AppendLine("R\t" + r.Row + "\t" + r.Category + "\t" + r.Discipline + "\t" + r.Description + "\t" + r.Weight);
foreach (string c in cells)
    sb.AppendLine(c);

Directory.CreateDirectory(Path.GetDirectoryName(output));
File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"{rows.Count} category rows, {cells.Count} cells, {toleranceConstants} tolerances, " +
                  $"{toleranceFormulas} tolerance formula cells ignored -> {output}");
foreach (var g in rows.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
    Console.WriteLine("  category on several rows: " + g.Key + " (rows " + string.Join(", ", g.Select(r => r.Row)) + ")");
return 0;

static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

static string Col(int n)
{
    string s = "";
    while (n > 0) { int m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; }
    return s;
}

static (int Col, int Row) Ref(string r)
{
    int i = 0, c = 0;
    while (i < r.Length && char.IsLetter(r[i])) { c = c * 26 + (char.ToUpperInvariant(r[i]) - 'A' + 1); i++; }
    return (c, int.Parse(r.Substring(i), CultureInfo.InvariantCulture));
}

sealed class Sheet
{
    public readonly Dictionary<string, string> Values = new();
    public readonly Dictionary<string, string> Anchors = new();
    public readonly HashSet<string> Formulas = new();

    public string Get(string col, int row)
    {
        string key = col + row;
        if (Values.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v)) return v;
        if (Anchors.TryGetValue(key, out string anchor) && Values.TryGetValue(anchor, out v)) return v;
        return null;
    }
}
