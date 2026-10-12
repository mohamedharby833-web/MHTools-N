using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Data;
using Autodesk.Navisworks.Api.Takeoff;

namespace MHToolsN.Services
{
    /// <summary>One Quantification item to fill: a Revit type in a category of a discipline, and its elements.</summary>
    public sealed class TakeoffLine
    {
        public string Discipline;     // "MEC"
        public string DisciplineName; // "MECHANICAL - MEC" - the top item group
        public string Category;       // "Ducts" - the item group under it
        public string Type;           // the Revit type - the item, or for a sized category the item group above the sizes
        public string Size;           // "1000 x 700" - the item, for ducts and the like (null: not itemized by size)
        public DuctSize SizeOf;       // the size read (null: none readable)
        public double InsulationMm;   // duct insulation / lining thickness
        public List<ModelItem> ToAdd = new List<ModelItem>();

        public string ItemName { get { return Size ?? Type; } }
        public int AlreadyTakenOff;
        public List<ModelItem> Already = new List<ModelItem>(); // the elements already taken off (for Update Take Off)
        public int NoGuid;
    }

    public sealed class TakeoffPlan
    {
        public List<TakeoffLine> Lines = new List<TakeoffLine>();
        public List<string> Warnings = new List<string>();
        public int Elements { get { return Lines.Sum(l => l.ToAdd.Count + l.AlreadyTakenOff); } }
        public int ToAdd { get { return Lines.Sum(l => l.ToAdd.Count); } }
    }

    public sealed class TakeoffResult
    {
        public int GroupsCreated;
        public int ItemsCreated;
        public int ObjectsAdded;
        public int ObjectsRemoved, ItemsRemoved, GroupsRemoved; // Update Take Off
        public int Failed;
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Quantification ("Takeoff") written from the model: one item group per
    /// discipline (from the file, as Clash Matrix decides it), one per Revit
    /// category under it, one item per Revit type, and the type's elements taken off
    /// onto it - Navisworks then works out Length, Area, Volume and Count from the
    /// elements' properties, as when elements are dragged onto an item.
    ///
    /// Running it again adds only elements that are not taken off yet (by their
    /// GUID); existing groups, items and objects are found by name and reused, never
    /// changed or removed.
    ///
    /// Table and column names are always read from the API (DbTableName, the column
    /// definitions), never written out here. Runs on the UI thread.
    /// </summary>
    public static class TakeoffService
    {
        private const string ElementCategoryInternal = "LcRevitData_Element";
        private const string CategoryPropertyInternal = "LcRevitPropertyElementCategory";

        /// <summary>Revit categories that are not built things - nothing to quantify.</summary>
        private static readonly string[] NotPhysical =
        {
            "Rooms", "Spaces", "Areas", "Grids", "Levels", "Lines", "Views", "Sheets", "Cameras", "Detail Items",
            "Generic Annotations", "Room Separation", "<Room Separation>", "Space Separation", "Area Boundary",
            "Reference Planes", "Scope Boxes", "Matchline", "Section Boxes", "HVAC Zones", "Mass", "Project Information"
        };

        /// <summary>
        /// Categories itemized by size (for the factory): Revit type > one item per size.
        /// Value: the Revit properties holding the size, the first one found is read.
        /// Pipes: Revit's Size is the nominal diameter ("100 mmø").
        /// </summary>
        private static readonly Dictionary<string, string[]> SizedCategories = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Ducts", new[] { "Size" } }, { "Flex Ducts", new[] { "Size", "Overall Size" } }, { "Duct Fittings", new[] { "Size" } }, { "Duct Accessories", new[] { "Size" } },
            { "Duct Insulations", new[] { "Duct Size", "Size" } }, { "Duct Linings", new[] { "Duct Size", "Size" } },
            { "Pipes", new[] { "Size" } }, { "Flex Pipes", new[] { "Size", "Overall Size" } }, { "Pipe Fittings", new[] { "Size" } }, { "Pipe Accessories", new[] { "Size" } },
            { "Pipe Insulations", new[] { "Pipe Size", "Size" } }
        };

        public const string NoSize = "(no size)";

        public static bool IsSized(string category)
        {
            return category != null && SizedCategories.ContainsKey(category);
        }

        public static DocumentTakeoff Takeoff(Document doc)
        {
            return doc.GetTakeoff();
        }

        /// <summary>Quantification is ready to take off into: its database exists and Project Setup was done.</summary>
        public static bool IsReady(Document doc, out string why)
        {
            DocumentTakeoff takeoff = Takeoff(doc);
            if (!takeoff.HasDatabaseInitialized)
            {
                why = "Quantification is not set up in this file. In Navisworks open Quantification (Home > Tools > Quantification) and run Project Setup " +
                      "(choose a catalog, e.g. the default, and the units), then try again.";
                return false;
            }
            why = null;
            return true;
        }

        // ---------------------------------------------------------------- plan

        /// <param name="categories">Only these Revit categories (empty = all physical ones).</param>
        /// <param name="disciplines">Only files of these disciplines, e.g. MEC, EL (empty = all).</param>
        /// <param name="modelFilter">Only appended files whose name contains this (empty = all).</param>
        public static TakeoffPlan Plan(Document doc, ICollection<string> categories, ICollection<string> disciplines, string modelFilter,
            NavisProgress progress)
        {
            var plan = new TakeoffPlan();
            List<ModelInfo> models = NavisClashCreator.ScanModels(doc, plan.Warnings, progress);
            HashSet<Guid> done = TakenOffGuids(doc);

            var wantCategories = new HashSet<string>(categories ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var wantDisciplines = new HashSet<string>(disciplines ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var lines = new Dictionary<string, TakeoffLine>(StringComparer.OrdinalIgnoreCase);

            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
            {
                if (model.RootItem == null) continue;
                string file = SafeName(model.FileName);
                if (!string.IsNullOrEmpty(modelFilter) && (file ?? "").IndexOf(modelFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ModelInfo info = models.FirstOrDefault(m => string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase));
                string discipline = info != null && !string.IsNullOrEmpty(info.Discipline) ? info.Discipline : "GEN";
                if (wantDisciplines.Count > 0 && !wantDisciplines.Contains(discipline)) continue;

                // Every element with a Revit category, the element itself (not its geometry below).
                var search = new Search();
                var scope = new ModelItemCollection { model.RootItem };
                search.Selection.CopyFrom(scope);
                search.Locations = SearchLocations.DescendantsAndSelf;
                search.PruneBelowMatch = true;
                search.SearchConditions.Add(SearchCondition.HasPropertyByName(ElementCategoryInternal, CategoryPropertyInternal));

                foreach (ModelItem item in search.FindAll(doc, false))
                {
                    string category = Text(item, ElementCategoryInternal, CategoryPropertyInternal, "Element", "Category");
                    if (string.IsNullOrWhiteSpace(category) || NotPhysical.Contains(category, StringComparer.OrdinalIgnoreCase)) continue;
                    if (category.IndexOf("Analytical", StringComparison.OrdinalIgnoreCase) >= 0 || category.EndsWith("Tags", StringComparison.OrdinalIgnoreCase)) continue;
                    if (wantCategories.Count > 0 && !wantCategories.Contains(category) && !wantCategories.Contains(NavisClashCreator.GetMainCategory(category))) continue;

                    string type = Text(item, null, null, "Element", "Type") ?? Text(item, null, null, "Element", "Family") ?? item.DisplayName;
                    string sizeLabel = null;
                    DuctSize size = null;
                    double insulationMm = 0;
                    string[] sizeProperties;
                    if (SizedCategories.TryGetValue(category, out sizeProperties))
                    {
                        size = DuctSize.Parse(sizeProperties.Select(p => Text(item, null, null, "Element", p)).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)));
                        sizeLabel = size != null ? size.Label : NoSize;
                        if (category.EndsWith("Insulations", StringComparison.OrdinalIgnoreCase) || category.EndsWith("Linings", StringComparison.OrdinalIgnoreCase))
                        {
                            string thickness = Text(item, null, null, "Element", category.EndsWith("Linings", StringComparison.OrdinalIgnoreCase) ? "Lining Thickness" : "Insulation Thickness");
                            DuctSize t = DuctSize.Parse(thickness);
                            if (t != null) { insulationMm = t.Width; sizeLabel += " - " + DuctSheet.Num(insulationMm) + " mm"; }
                        }
                    }

                    string key = discipline + "|" + category + "|" + type + "|" + sizeLabel;
                    TakeoffLine line;
                    if (!lines.TryGetValue(key, out line))
                    {
                        line = new TakeoffLine
                        {
                            Discipline = discipline, DisciplineName = Disciplines.Heading(discipline), Category = category, Type = type,
                            Size = sizeLabel, SizeOf = size, InsulationMm = insulationMm
                        };
                        lines[key] = line;
                    }
                    Guid guid = item.InstanceGuid;
                    if (guid == Guid.Empty) line.NoGuid++;
                    else if (done.Contains(guid)) { line.AlreadyTakenOff++; line.Already.Add(item); }
                    else line.ToAdd.Add(item);
                }
            }

            // Sizes smallest first (by width, then height), as a factory list reads.
            plan.Lines = lines.Values.OrderBy(l => l.Discipline).ThenBy(l => l.Category).ThenBy(l => l.Type)
                .ThenBy(l => l.SizeOf != null ? l.SizeOf.Width : double.MaxValue).ThenBy(l => l.SizeOf != null ? l.SizeOf.Height : 0)
                .ThenBy(l => l.Size).ToList();
            int noGuid = plan.Lines.Sum(l => l.NoGuid);
            if (noGuid > 0)
                plan.Warnings.Add(noGuid + " element(s) have no GUID and cannot be taken off (Navisworks takes off only items with one).");
            return plan;
        }

        // --------------------------------------------------------------- apply

        /// <param name="replace">
        /// Take off again (Update Take Off): the plan's elements already taken off are
        /// first removed from their items - anywhere in Quantification - and taken off
        /// afresh with the rest, so they follow the current structure (sizes), duct sheet
        /// rule and model. Items and groups left empty by that are removed; nothing else
        /// is touched.
        /// </param>
        public static TakeoffResult Apply(Document doc, TakeoffPlan plan, NavisProgress progress, DuctSheet sheet = null, bool replace = false)
        {
            string why;
            if (!IsReady(doc, out why)) throw new InvalidOperationException(why);
            if (sheet == null) sheet = DuctSheet.Load();
            var units = new ProjectUnits();

            DocumentTakeoff takeoff = Takeoff(doc);
            var result = new TakeoffResult();
            int total = Math.Max(1, plan.ToAdd + (replace ? plan.Lines.Sum(l => l.Already.Count) : 0)), done = 0;

            // Found live (Navisworks 2027, through Trusted C#):
            // - InsertModelItemTakeoff(item, element) alone leaves every quantity empty;
            //   the element's properties must be mapped to the input variables first
            //   (MapModelItemPropertiesToInputVariables) and passed in.
            // - The quantities are calculated when the database transaction commits.
            // - Inside a Navisworks transaction the whole takeoff is one undo step:
            //   Ctrl+Z removes the groups, items and objects again.
            // - An item's formulas default to =ModelLength, =ModelArea... (=1 for Count)
            //   in the table itself: nothing to set.
            using (Transaction undo = doc.BeginTransaction("MHTools-N: Quantification takeoff"))
            {
                using (NavisworksTransaction tx = takeoff.Database.BeginTransaction(DatabaseChangedAction.Edited))
                {
                    if (replace)
                        RemoveTakeoffs(tx.Connection, takeoff, new HashSet<Guid>(plan.Lines.SelectMany(l => l.Already).Select(e => e.InstanceGuid)), result);
                    var writer = new TableWriter(tx.Connection, takeoff);
                    var seen = new HashSet<Guid>();
                    foreach (TakeoffLine line in plan.Lines.Where(l => l.ToAdd.Count > 0 || (replace && l.Already.Count > 0)))
                    {
                        IEnumerable<ModelItem> elements = replace ? line.ToAdd.Concat(line.Already) : line.ToAdd;
                        long top = writer.Group(null, line.DisciplineName, ref result.GroupsCreated);
                        long cat = writer.Group(top, line.Category, ref result.GroupsCreated);
                        long item = line.Size == null
                            ? writer.Item(cat, line.Type, line.Category, ref result.ItemsCreated)
                            : writer.Item(writer.Group(cat, line.Type, ref result.GroupsCreated), line.Size, line.Category, ref result.ItemsCreated);

                        foreach (ModelItem element in elements)
                        {
                            if (progress != null) progress.Report(done++ / (double)total);
                            if (!seen.Add(element.InstanceGuid)) continue;
                            try
                            {
                                TakeoffVariableCollection inputs = takeoff.Objects.CreateDefaultInputVariables();
                                takeoff.Objects.MapModelItemPropertiesToInputVariables(item, element, inputs);
                                if (line.Size != null) FillSized(inputs, line, sheet, units);
                                takeoff.Objects.InsertModelItemTakeoff(item, element.InstanceGuid, inputs, false);
                                result.ObjectsAdded++;
                            }
                            catch (Exception ex)
                            {
                                result.Failed++;
                                if (result.Errors.Count < 10) result.Errors.Add(line.Type + ": " + ex.Message);
                            }
                        }
                    }
                    tx.Commit();
                }
                undo.Commit();
            }
            if (units.NotMetric)
                result.Errors.Add("Quantification's Project Setup is not metric: duct sizes, sheet thickness and weight are written in " +
                                  units.LengthName + " and kg - check them, or use a metric Project Setup.");
            Watchdog.Log("Quantification: groups +" + result.GroupsCreated + ", items +" + result.ItemsCreated + ", objects +" + result.ObjectsAdded +
                         (result.Failed > 0 ? ", failed " + result.Failed + " (" + string.Join("; ", result.Errors) + ")" : ""));
            return result;
        }

        // ------------------------------------------------------ update takeoff

        /// <summary>
        /// Removes the takeoff objects of these elements (with any steps and resources
        /// on them), then the items they leave empty and, upwards, the groups left with
        /// nothing in them. Inside the caller's database transaction (and its Navisworks
        /// transaction, so one Undo puts it all back). There is no delete in the API:
        /// SQL on the tables, names read from the API as everywhere here.
        /// </summary>
        private static void RemoveTakeoffs(NavisworksConnection c, DocumentTakeoff takeoff, HashSet<Guid> guids, TakeoffResult result)
        {
            if (guids.Count == 0) return;
            ObjectTable objects = takeoff.Objects;
            var ids = new List<long>();
            var items = new HashSet<long>();
            using (NavisworksCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT " + objects.RowId.Name + ", " + objects.Parent.Name + ", " + objects.ModelItemId.Name + " FROM " + objects.DbTableName;
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        Guid g;
                        if (r.IsDBNull(2) || !TryGuid(r.GetValue(2), out g) || !guids.Contains(g)) continue;
                        ids.Add(Convert.ToInt64(r.GetValue(0)));
                        if (!r.IsDBNull(1)) items.Add(Convert.ToInt64(r.GetValue(1)));
                    }
                }
            }

            for (int i = 0; i < ids.Count; i += 400)
            {
                string list = string.Join(",", ids.Skip(i).Take(400));
                // Steps and resources hang off objects; usually there are none on ours.
                try
                {
                    ObjectStepTable steps = takeoff.ObjectSteps;
                    ObjectResourceTable resources = takeoff.ObjectResources;
                    Execute(c, "DELETE FROM " + resources.DbTableName + " WHERE " + resources.ObjectStepId.Name + " IN (SELECT " + steps.RowId.Name +
                               " FROM " + steps.DbTableName + " WHERE " + steps.ObjectId.Name + " IN (" + list + "))");
                    Execute(c, "DELETE FROM " + steps.DbTableName + " WHERE " + steps.ObjectId.Name + " IN (" + list + ")");
                }
                catch (Exception ex) { Watchdog.Log("Quantification: clearing object steps failed: " + ex.Message); }
                result.ObjectsRemoved += Execute(c, "DELETE FROM " + objects.DbTableName + " WHERE " + objects.RowId.Name + " IN (" + list + ")");
            }

            // Items left empty, then their groups upwards while empty.
            var groups = new HashSet<long>();
            foreach (long item in items)
            {
                if (Scalar(c, "SELECT COUNT(*) FROM " + objects.DbTableName + " WHERE " + objects.Parent.Name + " = " + item) > 0) continue;
                long parent = Scalar(c, "SELECT " + takeoff.Items.Parent.Name + " FROM " + takeoff.Items.DbTableName + " WHERE " + takeoff.Items.RowId.Name + " = " + item);
                try
                {
                    result.ItemsRemoved += Execute(c, "DELETE FROM " + takeoff.Items.DbTableName + " WHERE " + takeoff.Items.RowId.Name + " = " + item);
                    if (parent > 0) groups.Add(parent);
                }
                catch (Exception ex) { Watchdog.Log("Quantification: kept empty item " + item + ": " + ex.Message); }
            }
            while (groups.Count > 0)
            {
                var next = new HashSet<long>();
                foreach (long group in groups)
                {
                    if (Scalar(c, "SELECT COUNT(*) FROM " + takeoff.Items.DbTableName + " WHERE " + takeoff.Items.Parent.Name + " = " + group) > 0) continue;
                    if (Scalar(c, "SELECT COUNT(*) FROM " + takeoff.ItemGroups.DbTableName + " WHERE " + takeoff.ItemGroups.Parent.Name + " = " + group) > 0) continue;
                    long parent = Scalar(c, "SELECT " + takeoff.ItemGroups.Parent.Name + " FROM " + takeoff.ItemGroups.DbTableName + " WHERE " + takeoff.ItemGroups.RowId.Name + " = " + group);
                    try
                    {
                        result.GroupsRemoved += Execute(c, "DELETE FROM " + takeoff.ItemGroups.DbTableName + " WHERE " + takeoff.ItemGroups.RowId.Name + " = " + group);
                        if (parent > 0) next.Add(parent);
                    }
                    catch (Exception ex) { Watchdog.Log("Quantification: kept empty group " + group + ": " + ex.Message); }
                }
                groups = next;
            }
        }

        private static int Execute(NavisworksConnection c, string sql)
        {
            using (NavisworksCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                return cmd.ExecuteNonQuery();
            }
        }

        private static long Scalar(NavisworksConnection c, string sql)
        {
            using (NavisworksCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
            }
        }

        /// <summary>TK_Object.ModelItemID as stored: a Guid, its 16 bytes or its text.</summary>
        private static bool TryGuid(object v, out Guid g)
        {
            if (v is Guid) { g = (Guid)v; return true; }
            if (v is byte[] && ((byte[])v).Length == 16) { g = new Guid((byte[])v); return true; }
            return Guid.TryParse(Convert.ToString(v), out g);
        }

        // ------------------------------------------------------- sized items

        /// <summary>
        /// The length and area units Quantification's inputs are in - Project Setup's.
        /// Values written without units are taken in them (seen live, 2027: a plain 1.0 for
        /// ModelWidth came out as Width 1 m in a metric project), so ours are converted.
        /// Read from the first mapped ModelLength / ModelArea that carries its units.
        /// </summary>
        private sealed class ProjectUnits
        {
            public double MetresPerLength = 1, SquareMetresPerArea = 1;
            public string LengthName = "Meter";
            private bool _length, _area;

            public bool NotMetric { get { return _length && MetresPerLength != 1 && MetresPerLength != 0.001 && MetresPerLength != 0.01; } }

            public void Learn(TakeoffVariableCollection inputs)
            {
                if (!_length)
                {
                    string u = UnitsOf(Var(inputs, "ModelLength"));
                    double f;
                    if (u != null && LengthFactors.TryGetValue(u, out f)) { MetresPerLength = f; LengthName = u; _length = true; }
                }
                if (!_area)
                {
                    string u = UnitsOf(Var(inputs, "ModelArea"));
                    double f;
                    if (u != null && AreaFactors.TryGetValue(u, out f)) { SquareMetresPerArea = f; _area = true; }
                }
            }

            public double Length(double metres) { return metres / MetresPerLength; }
            public double Area(double squareMetres) { return squareMetres / SquareMetresPerArea; }
        }

        private static readonly Dictionary<string, double> LengthFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Meter", 1 }, { "Millimeter", 0.001 }, { "Centimeter", 0.01 }, { "Kilometer", 1000 }, { "Inch", 0.0254 }, { "Foot", 0.3048 }, { "Feet", 0.3048 }, { "Yard", 0.9144 }
        };

        private static readonly Dictionary<string, double> AreaFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "SquareMeter", 1 }, { "SquareMillimeter", 1e-6 }, { "SquareCentimeter", 1e-4 }, { "SquareKilometer", 1e6 }, { "SquareInch", 0.00064516 },
            { "SquareFeet", 0.09290304 }, { "SquareFoot", 0.09290304 }, { "SquareYard", 0.83612736 }
        };

        /// <summary>
        /// The values of one element of a sized item. Revit gives a duct's Width and Height
        /// as text in mm (mapped as 1000 for a metre) and no thickness or weight, so they
        /// are written here from the size: Width, Height, Perimeter, sheet Thickness (by the
        /// longest side) and, for straight ducts, Weight = sheet area x thickness x density.
        /// The sheet area is Revit's duct Area (its outer surface), else perimeter x length.
        /// Description1 = the size, Description2 = the sheet.
        /// </summary>
        private static void FillSized(TakeoffVariableCollection inputs, TakeoffLine line, DuctSheet sheet, ProjectUnits units)
        {
            units.Learn(inputs);
            SetText(inputs, "Description1", line.Size);
            DuctSize size = line.SizeOf;
            if (size == null) return;

            SetNumber(inputs, "ModelWidth", units.Length(size.Width / 1000.0));
            SetNumber(inputs, "ModelHeight", units.Length(size.Height / 1000.0));

            string category = line.Category;
            if (category.EndsWith("Insulations", StringComparison.OrdinalIgnoreCase) || category.EndsWith("Linings", StringComparison.OrdinalIgnoreCase))
            {
                if (line.InsulationMm > 0) SetNumber(inputs, "ModelThickness", units.Length(line.InsulationMm / 1000.0));
                return;
            }
            bool straight = string.Equals(category, "Ducts", StringComparison.OrdinalIgnoreCase);
            bool fitting = string.Equals(category, "Duct Fittings", StringComparison.OrdinalIgnoreCase);
            if (!straight && !fitting) return;

            double thicknessMm = sheet.ThicknessFor(size.LongestSide);
            SetNumber(inputs, "ModelThickness", units.Length(thicknessMm / 1000.0));
            SetText(inputs, "Description2", "Sheet " + DuctSheet.Num(thicknessMm) + " mm (" + sheet.Standard + ")");
            if (!straight) return;

            SetNumber(inputs, "ModelPerimeter", units.Length(size.Perimeter / 1000.0));
            double areaM2 = Number(Var(inputs, "ModelArea")) * units.SquareMetresPerArea;
            if (areaM2 <= 0)
            {
                double lengthM = Number(Var(inputs, "ModelLength")) * units.MetresPerLength;
                areaM2 = size.Perimeter / 1000.0 * lengthM;
                if (areaM2 > 0) SetNumber(inputs, "ModelArea", units.Area(areaM2));
            }
            if (areaM2 > 0) SetNumber(inputs, "ModelWeight", sheet.WeightKg(areaM2, thicknessMm));
        }

        private static TakeoffVariable Var(TakeoffVariableCollection inputs, string name)
        {
            foreach (TakeoffVariable v in inputs)
                if (string.Equals(v.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        private static string UnitsOf(TakeoffVariable v)
        {
            try { return v == null || v.IsUnitsNull ? null : v.Units.ToString(); }
            catch (Exception) { return null; }
        }

        private static double Number(TakeoffVariable v)
        {
            try
            {
                if (v == null || v.Value == null) return 0;
                VariantData d = v.Value;
                if (d.IsDouble) return d.ToDouble();
                if (d.IsDoubleLength) return d.ToDoubleLength();
                if (d.IsDoubleArea) return d.ToDoubleArea();
                if (d.IsInt32) return d.ToInt32();
                return 0;
            }
            catch (Exception) { return 0; }
        }

        private static void SetNumber(TakeoffVariableCollection inputs, string name, double value)
        {
            TakeoffVariable v = Var(inputs, name);
            if (v != null && v.IsAbleToSetValue && !double.IsNaN(value) && !double.IsInfinity(value)) v.Value = VariantData.FromDouble(value);
        }

        private static void SetText(TakeoffVariableCollection inputs, string name, string value)
        {
            TakeoffVariable v = Var(inputs, name);
            if (v != null && v.IsAbleToSetValue && !string.IsNullOrEmpty(value)) v.Value = VariantData.FromDisplayString(value);
        }


        // ---------------------------------------------------------- the tables

        /// <summary>
        /// Adds item groups and items, inside one open database transaction. The tables
        /// have NOT NULL columns of their own (WBS - seen in 2027 - and maybe more), so
        /// the columns come from the table itself (PRAGMA table_info): WBS is numbered
        /// as Quantification numbers it (1, 1.2, 1.2.3) after what is there; any other
        /// required column takes the value of an existing row of that table (the
        /// catalog's), or a neutral 0 / "" when the table is empty.
        /// </summary>
        private sealed class TableWriter
        {
            private sealed class Table
            {
                public TakeoffTable Def;
                public string Parent, Name, Wbs;
                public readonly Dictionary<string, long> ByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                public readonly Dictionary<long, string> WbsOf = new Dictionary<long, string>();
                public readonly HashSet<string> UsedWbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public readonly Dictionary<string, object> Required = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                public readonly HashSet<string> Columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            private readonly NavisworksConnection _connection;
            private readonly Table _groups, _items;

            // Children under each group (groups and items share one WBS sequence), and at the root.
            private readonly Dictionary<long, int> _children = new Dictionary<long, int>();
            private int _rootGroups;

            public TableWriter(NavisworksConnection connection, DocumentTakeoff takeoff)
            {
                _connection = connection;
                _groups = Read(takeoff.ItemGroups, takeoff.ItemGroups.Parent.Name, takeoff.ItemGroups.Name.Name, takeoff.ItemGroups.WBS.Name, true);
                _items = Read(takeoff.Items, takeoff.Items.Parent.Name, takeoff.Items.Name.Name, takeoff.Items.WBS.Name, false);
            }

            public long Group(long? parent, string name, ref int created)
            {
                return Ensure(_groups, parent, name, ref created);
            }

            /// <summary>
            /// An item. TK_Item has NOT NULL display columns with no default (seen in 2027:
            /// Transparency, Color, LineThickness, CountSymbol, CountSize): given here as
            /// Quantification shows a new item - half see-through, a colour per category.
            /// </summary>
            public long Item(long parent, string name, string category, ref int created)
            {
                var looks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Transparency", 0.5 }, { "Color", ColourFor(category) }, { "LineThickness", 1.0 }, { "CountSymbol", 0L }, { "CountSize", 1.0 }
                };
                return Ensure(_items, parent, name, ref created, looks);
            }

            /// <summary>A steady colour per category (the same every run), from a palette that reads on a model.</summary>
            private static long ColourFor(string category)
            {
                int[] palette = { 0xE53935, 0x1E88E5, 0x43A047, 0xFB8C00, 0x8E24AA, 0x00ACC1, 0xFDD835, 0xD81B60, 0x6D4C41, 0x3949AB, 0x7CB342, 0x00897B };
                int hash = 0;
                foreach (char ch in category ?? "") hash = unchecked(hash * 31 + char.ToUpperInvariant(ch));
                return palette[(hash & 0x7FFFFFFF) % palette.Length];
            }

            private long Ensure(Table t, long? parent, string name, ref int created, Dictionary<string, object> extra = null)
            {
                string key = (parent.HasValue ? parent.Value.ToString() : "") + "|" + name;
                long id;
                if (t.ByKey.TryGetValue(key, out id)) return id;

                string wbs = NextWbs(t, parent);
                var values = new Dictionary<string, object>(t.Required, StringComparer.OrdinalIgnoreCase);
                if (extra != null) foreach (var pair in extra) if (t.Columns.Contains(pair.Key)) values[pair.Key] = pair.Value;
                values[t.Parent] = parent.HasValue ? (object)parent.Value : DBNull.Value;
                values[t.Name] = name;
                if (!string.IsNullOrEmpty(t.Wbs)) values[t.Wbs] = wbs;

                using (NavisworksCommand cmd = _connection.CreateCommand())
                {
                    var names = values.Keys.ToList();
                    cmd.CommandText = "INSERT INTO " + t.Def.DbTableName + " (" + string.Join(", ", names) + ") VALUES (" +
                                      string.Join(", ", names.Select((n, i) => "@p" + i)) + ")";
                    for (int i = 0; i < names.Count; i++) cmd.Parameters.AddWithValue("@p" + i, values[names[i]]);
                    cmd.ExecuteNonQuery();
                }
                id = NewRowId(t);

                t.ByKey[key] = id;
                t.WbsOf[id] = wbs;
                t.UsedWbs.Add(wbs);
                if (parent.HasValue) _children[parent.Value] = Children(parent.Value) + 1;
                else _rootGroups++;
                created++;
                return id;
            }

            private int Children(long group)
            {
                int n;
                return _children.TryGetValue(group, out n) ? n : 0;
            }

            /// <summary>The parent group's WBS + ".n", or "n" at the root - the next n not used yet.</summary>
            private string NextWbs(Table t, long? parent)
            {
                string prefix = "";
                int n = 0;
                if (parent.HasValue)
                {
                    string parentWbs;
                    prefix = (_groups.WbsOf.TryGetValue(parent.Value, out parentWbs) && !string.IsNullOrEmpty(parentWbs) ? parentWbs : parent.Value.ToString()) + ".";
                    n = Children(parent.Value);
                }
                else
                {
                    n = _rootGroups;
                }
                string candidate;
                // Two digits, as the catalog numbers its groups (01, 02 ... 16).
                do { candidate = prefix + (++n).ToString("00"); }
                while (t.UsedWbs.Contains(candidate) || (t == _items && _groups.UsedWbs.Contains(candidate)) || (t == _groups && _items.UsedWbs.Contains(candidate)));
                return candidate;
            }

            private long NewRowId(Table t)
            {
                try
                {
                    using (NavisworksCommand cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT last_insert_rowid()";
                        long id = Convert.ToInt64(cmd.ExecuteScalar());
                        if (id > 0) return id;
                    }
                }
                catch (Exception ex)
                {
                    Watchdog.Log("Quantification: last_insert_rowid() not available (" + ex.Message + "); using MAX(row id).");
                }
                // Inside our own open transaction, the newest row of the table is the one just added.
                using (NavisworksCommand cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT MAX(" + t.Def.RowId.Name + ") FROM " + t.Def.DbTableName;
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }

            private Table Read(TakeoffTable def, string parent, string name, string wbs, bool groups)
            {
                var t = new Table { Def = def, Parent = parent, Name = name, Wbs = wbs };

                using (NavisworksCommand cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + def.RowId.Name + ", " + parent + ", " + name + (string.IsNullOrEmpty(wbs) ? "" : ", " + wbs) + " FROM " + def.DbTableName;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            long id = Convert.ToInt64(r.GetValue(0));
                            long? p = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1));
                            string n = r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2));
                            string w = string.IsNullOrEmpty(wbs) || r.IsDBNull(3) ? "" : Convert.ToString(r.GetValue(3));
                            string key = (p.HasValue ? p.Value.ToString() : "") + "|" + n;
                            if (!t.ByKey.ContainsKey(key)) t.ByKey[key] = id;
                            t.WbsOf[id] = w;
                            if (w.Length > 0) t.UsedWbs.Add(w);
                            if (p.HasValue) _children[p.Value] = Children(p.Value) + 1;
                            else if (groups) _rootGroups++;
                        }
                    }
                }

                // Required columns: NOT NULL, no default, not the row id, not one we fill.
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { def.RowId.Name, parent, name };
                if (!string.IsNullOrEmpty(wbs)) skip.Add(wbs);
                var required = new List<KeyValuePair<string, string>>();
                try
                {
                    using (NavisworksCommand cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA table_info(" + def.DbTableName + ")";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string column = Convert.ToString(r.GetValue(1));
                                t.Columns.Add(column);
                                string type = Convert.ToString(r.GetValue(2));
                                bool notNull = Convert.ToInt64(r.GetValue(3)) != 0;
                                bool hasDefault = !r.IsDBNull(4);
                                bool pk = Convert.ToInt64(r.GetValue(5)) != 0;
                                if (notNull && !hasDefault && !pk && !skip.Contains(column)) required.Add(new KeyValuePair<string, string>(column, type));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Watchdog.Log("Quantification: PRAGMA table_info(" + def.DbTableName + ") failed: " + ex.Message);
                }
                if (required.Count == 0) return t;

                // A value for each: from an existing row when there is one, else neutral.
                Dictionary<string, object> sample = null;
                try
                {
                    using (NavisworksCommand cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT " + string.Join(", ", required.Select(c => c.Key)) + " FROM " + def.DbTableName + " LIMIT 1";
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                sample = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                for (int i = 0; i < required.Count; i++) sample[required[i].Key] = r.GetValue(i);
                            }
                        }
                    }
                }
                catch (Exception) { }

                foreach (var c in required)
                {
                    object v;
                    if (sample == null || !sample.TryGetValue(c.Key, out v) || v == null || v == DBNull.Value)
                    {
                        string type = (c.Value ?? "").ToUpperInvariant();
                        v = type.Contains("INT") ? (object)0L : type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") ? (object)0.0 : "";
                    }
                    t.Required[c.Key] = v;
                }
                Watchdog.Log("Quantification: " + def.DbTableName + " requires " + string.Join(", ", t.Required.Select(p => p.Key + "=" + p.Value)) + ".");
                return t;
            }
        }

        /// <summary>The GUIDs of the model items already taken off, onto any item.</summary>
        private static HashSet<Guid> TakenOffGuids(Document doc)
        {
            var guids = new HashSet<Guid>();
            DocumentTakeoff takeoff = Takeoff(doc);
            if (!takeoff.HasDatabaseInitialized) return guids;
            try
            {
                using (NavisworksCommand cmd = takeoff.Database.Value.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + takeoff.Objects.ModelItemId.Name + " FROM " + takeoff.Objects.DbTableName;
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0)) continue;
                            Guid g;
                            if (TryGuid(reader.GetValue(0), out g)) guids.Add(g);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Watchdog.Log("Quantification: reading taken-off GUIDs failed: " + ex.Message);
            }
            return guids;
        }

        // ------------------------------------------------------------- reading

        /// <summary>What the Quantification looks like, for the diagnostics tool: tables, columns, variables, row counts.</summary>
        public static Dictionary<string, object> Describe(Document doc)
        {
            DocumentTakeoff takeoff = Takeoff(doc);
            var info = new Dictionary<string, object> { { "databaseInitialized", takeoff.HasDatabaseInitialized } };
            if (!takeoff.HasDatabaseInitialized) return info;

            var tables = new List<object>();
            foreach (var pair in new Dictionary<string, TakeoffTable>
            {
                { "ItemGroups", takeoff.ItemGroups }, { "Items", takeoff.Items }, { "Objects", takeoff.Objects },
                { "ResourceGroups", takeoff.ResourceGroups }, { "Resources", takeoff.Resources }
            })
            {
                var vars = new List<object>();
                foreach (TakeoffVariableDefinition d in pair.Value.Variables)
                    vars.Add(new Dictionary<string, object>
                    {
                        { "name", d.Name }, { "displayName", d.DisplayName }, { "formulaName", d.DisplayNameForFormulaUsage },
                        { "purpose", d.Purpose.ToString() }, { "type", d.VariableType.ToString() }, { "valueColumn", d.ValueDbColumnName }
                    });
                tables.Add(new Dictionary<string, object>
                {
                    { "table", pair.Key }, { "dbTable", pair.Value.DbTableName }, { "rows", Count(takeoff, pair.Value.DbTableName) }, { "variables", vars }
                });
            }
            info["tables"] = tables;
            return info;
        }

        private static long Count(DocumentTakeoff takeoff, string table)
        {
            try
            {
                using (NavisworksCommand cmd = takeoff.Database.Value.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM " + table;
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            catch (Exception) { return -1; }
        }

        /// <summary>The dimensions <see cref="Summary"/> averages instead of adding up.</summary>
        public static readonly HashSet<string> PerPiece = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Width", "Height", "Thickness", "Perimeter" };

        /// <summary>
        /// The quantities as they are now: per item (with its group path), the number of
        /// objects and the item's calculated variables - Length, Area, Volume, Weight,
        /// Count summed, Width, Height, Thickness, Perimeter averaged (per piece) - in the
        /// Quantification project's units.
        /// </summary>
        public static List<Dictionary<string, object>> Summary(Document doc, int limit)
        {
            DocumentTakeoff takeoff = Takeoff(doc);
            var rows = new List<Dictionary<string, object>>();
            if (!takeoff.HasDatabaseInitialized) return rows;

            // Group names and parents, to give each item its path.
            var groups = new Dictionary<long, KeyValuePair<long?, string>>();
            using (NavisworksCommand cmd = takeoff.Database.Value.CreateCommand())
            {
                cmd.CommandText = "SELECT " + takeoff.ItemGroups.RowId.Name + ", " + takeoff.ItemGroups.Parent.Name + ", " + takeoff.ItemGroups.Name.Name +
                                  " FROM " + takeoff.ItemGroups.DbTableName;
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        groups[Convert.ToInt64(r.GetValue(0))] = new KeyValuePair<long?, string>(
                            r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1)), r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2)));
            }

            // The calculated quantities the objects carry (their value columns).
            var quantities = new List<TakeoffVariableDefinition>();
            foreach (TakeoffVariableDefinition d in takeoff.Objects.Variables)
                if (d.Purpose == Purpose.Calculation && d.VariableType == VariableType.Number && !string.IsNullOrEmpty(d.ValueDbColumnName))
                    quantities.Add(d);

            using (NavisworksCommand cmd = takeoff.Database.Value.CreateCommand())
            {
                // Dimensions are a piece's own (the same for every piece of a size): their
                // average over the pieces that have one; the rest add up.
                string sums = string.Join("", quantities.Select(q => PerPiece.Contains(q.Name)
                    ? ", AVG(NULLIF(o." + q.ValueDbColumnName + ", 0))"
                    : ", SUM(o." + q.ValueDbColumnName + ")"));
                cmd.CommandText = "SELECT i." + takeoff.Items.RowId.Name + ", i." + takeoff.Items.Parent.Name + ", i." + takeoff.Items.Name.Name +
                                  ", COUNT(o." + takeoff.Objects.RowId.Name + ")" + sums +
                                  " FROM " + takeoff.Items.DbTableName + " i LEFT JOIN " + takeoff.Objects.DbTableName + " o ON o." + takeoff.Objects.Parent.Name +
                                  " = i." + takeoff.Items.RowId.Name + " GROUP BY i." + takeoff.Items.RowId.Name;
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read() && rows.Count < limit)
                    {
                        var path = new List<string>();
                        long? parent = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1));
                        for (int guard = 0; parent.HasValue && groups.ContainsKey(parent.Value) && guard < 20; guard++)
                        {
                            path.Insert(0, groups[parent.Value].Value);
                            parent = groups[parent.Value].Key;
                        }
                        var row = new Dictionary<string, object>
                        {
                            { "group", string.Join(" / ", path) },
                            { "path", path },
                            { "item", r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2)) },
                            { "objects", Convert.ToInt64(r.GetValue(3)) }
                        };
                        for (int q = 0; q < quantities.Count; q++)
                        {
                            object v = r.GetValue(4 + q);
                            if (v != null && v != DBNull.Value) row[quantities[q].DisplayName] = Math.Round(Convert.ToDouble(v), 6);
                        }
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        // -------------------------------------------------------------- helpers

        private static string Text(ModelItem item, string catName, string propName, string catDisplay, string propDisplay)
        {
            try
            {
                return (catName != null ? ClashResultsReader.Property(item, p => p.FindPropertyByName(catName, propName)) : null)
                       ?? ClashResultsReader.Property(item, p => p.FindPropertyByDisplayName(catDisplay, propDisplay));
            }
            catch (Exception) { return null; }
        }

        private static string SafeName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path); }
            catch (Exception) { return path; }
        }
    }
}
