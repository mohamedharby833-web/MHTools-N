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
        public string Type;           // the Revit type - the item
        public List<ModelItem> ToAdd = new List<ModelItem>();
        public int AlreadyTakenOff;
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
        public int Failed;
        public int ItemsWithFormulas;
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
                    string key = discipline + "|" + category + "|" + type;
                    TakeoffLine line;
                    if (!lines.TryGetValue(key, out line))
                    {
                        line = new TakeoffLine { Discipline = discipline, DisciplineName = Disciplines.Heading(discipline), Category = category, Type = type };
                        lines[key] = line;
                    }
                    Guid guid = item.InstanceGuid;
                    if (guid == Guid.Empty) line.NoGuid++;
                    else if (done.Contains(guid)) line.AlreadyTakenOff++;
                    else line.ToAdd.Add(item);
                }
            }

            plan.Lines = lines.Values.OrderBy(l => l.Discipline).ThenBy(l => l.Category).ThenBy(l => l.Type).ToList();
            int noGuid = plan.Lines.Sum(l => l.NoGuid);
            if (noGuid > 0)
                plan.Warnings.Add(noGuid + " element(s) have no GUID and cannot be taken off (Navisworks takes off only items with one).");
            return plan;
        }

        // --------------------------------------------------------------- apply

        public static TakeoffResult Apply(Document doc, TakeoffPlan plan, NavisProgress progress)
        {
            string why;
            if (!IsReady(doc, out why)) throw new InvalidOperationException(why);

            DocumentTakeoff takeoff = Takeoff(doc);
            var result = new TakeoffResult();
            var groupIds = ExistingRows(doc, takeoff.ItemGroups, takeoff.ItemGroups.Parent.Name, takeoff.ItemGroups.Name.Name);
            var itemIds = ExistingRows(doc, takeoff.Items, takeoff.Items.Parent.Name, takeoff.Items.Name.Name);
            int total = Math.Max(1, plan.ToAdd), done = 0;

            using (NavisworksTransaction tx = takeoff.Database.BeginTransaction(DatabaseChangedAction.Edited))
            {
                foreach (TakeoffLine line in plan.Lines.Where(l => l.ToAdd.Count > 0))
                {
                    long top = EnsureRow(tx, takeoff.ItemGroups, groupIds, null, line.DisciplineName, ref result.GroupsCreated);
                    long cat = EnsureRow(tx, takeoff.ItemGroups, groupIds, top, line.Category, ref result.GroupsCreated);
                    int before = result.ItemsCreated;
                    long item = EnsureRow(tx, takeoff.Items, itemIds, cat, line.Type, ref result.ItemsCreated);
                    if (result.ItemsCreated > before)
                    {
                        string formulaError = ModelFormulas(takeoff, item);
                        if (formulaError == null) result.ItemsWithFormulas++;
                        else if (result.Errors.Count < 10) result.Errors.Add("Formulas on " + line.Type + ": " + formulaError);
                    }

                    foreach (ModelItem element in line.ToAdd)
                    {
                        if (progress != null) progress.Report(done++ / (double)total);
                        try
                        {
                            takeoff.Objects.InsertModelItemTakeoff(item, element);
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
            Watchdog.Log("Quantification: groups +" + result.GroupsCreated + ", items +" + result.ItemsCreated + ", objects +" + result.ObjectsAdded +
                         (result.Failed > 0 ? ", failed " + result.Failed + " (" + string.Join("; ", result.Errors) + ")" : ""));
            return result;
        }

        /// <summary>
        /// A new item's calculated quantities (Length, Area, Volume...) taken from the
        /// measured model values ("=ModelLength"...), as a catalog item does - where the
        /// item has no formula of its own yet and an input variable "Model" + name exists.
        /// </summary>
        /// <returns>null when done, else what went wrong.</returns>
        private static string ModelFormulas(DocumentTakeoff takeoff, long itemRowId)
        {
            try
            {
                var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (TakeoffVariableDefinition d in takeoff.Objects.Variables)
                    if (d.Purpose == Purpose.Input && !string.IsNullOrEmpty(d.Name))
                        inputs[d.Name] = string.IsNullOrEmpty(d.DisplayNameForFormulaUsage) ? d.Name : d.DisplayNameForFormulaUsage;

                // On an ITEM the quantities are Rollup variables (seen in 2027: Length,
                // Area, Volume, Count... all Purpose.Rollup); their formula is what each
                // of the item's objects calculates. So no filter on Purpose here.
                TakeoffVariableCollection vars = takeoff.Items.SelectVariables(itemRowId);
                bool changed = false;
                foreach (TakeoffVariable v in vars)
                {
                    if (v.Definition == null || v.Definition.VariableType != VariableType.Number || !v.IsAbleToSetFormula || !v.IsFormulaNull) continue;
                    string model;
                    if (inputs.TryGetValue("Model" + v.Definition.Name, out model)) v.Formula = "=" + model;
                    else if (string.Equals(v.Definition.Name, "Count", StringComparison.OrdinalIgnoreCase)) v.Formula = "=1";
                    else continue;
                    changed = true;
                }
                if (changed) takeoff.Items.UpdateVariables(itemRowId, vars);
                return changed ? null : "no quantity variable on the item takes a formula";
            }
            catch (Exception ex)
            {
                Watchdog.Log("Quantification: model formulas on item " + itemRowId + " not set: " + ex.Message);
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ---------------------------------------------------------- the tables

        /// <summary>The rows of an item-group / item table, by (parent, name) -> row id.</summary>
        private static Dictionary<string, long> ExistingRows(Document doc, TakeoffTable table, string parentColumn, string nameColumn)
        {
            var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (NavisworksCommand cmd = Takeoff(doc).Database.Value.CreateCommand())
            {
                cmd.CommandText = "SELECT " + table.RowId.Name + ", " + parentColumn + ", " + nameColumn + " FROM " + table.DbTableName;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long id = Convert.ToInt64(reader.GetValue(0));
                        string parent = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1));
                        string name = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2));
                        string key = parent + "|" + name;
                        if (!rows.ContainsKey(key)) rows[key] = id;
                    }
                }
            }
            return rows;
        }

        private static long EnsureRow(NavisworksTransaction tx, TakeoffTable table, Dictionary<string, long> rows, long? parent, string name,
            ref int created)
        {
            string key = (parent.HasValue ? parent.Value.ToString() : "") + "|" + name;
            long id;
            if (rows.TryGetValue(key, out id)) return id;

            string parentColumn, nameColumn;
            if (table is ItemGroupTable) { parentColumn = ((ItemGroupTable)table).Parent.Name; nameColumn = ((ItemGroupTable)table).Name.Name; }
            else { parentColumn = ((ItemTable)table).Parent.Name; nameColumn = ((ItemTable)table).Name.Name; }

            using (NavisworksCommand cmd = tx.Connection.CreateCommand())
            {
                // No cmd.Transaction = tx: NavisworksCommand throws NotImplementedException
                // on it (seen in 2027). A command made from the open transaction's connection
                // runs inside that transaction anyway.
                cmd.CommandText = "INSERT INTO " + table.DbTableName + " (" + parentColumn + ", " + nameColumn + ") VALUES (@parent, @name)";
                cmd.Parameters.AddWithValue("@parent", parent.HasValue ? (object)parent.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.ExecuteNonQuery();
            }
            id = 0;
            try
            {
                using (NavisworksCommand cmd = tx.Connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT last_insert_rowid()";
                    id = Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            catch (Exception ex)
            {
                Watchdog.Log("Quantification: last_insert_rowid() not available (" + ex.Message + "); using MAX(row id).");
            }
            if (id <= 0)
            {
                // Inside our own open transaction, the newest row of the table is the one just added.
                using (NavisworksCommand cmd = tx.Connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT MAX(" + table.RowId.Name + ") FROM " + table.DbTableName;
                    id = Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            rows[key] = id;
            created++;
            return id;
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
                            object v = reader.GetValue(0);
                            Guid g;
                            if (v is Guid) guids.Add((Guid)v);
                            else if (v is byte[] && ((byte[])v).Length == 16) guids.Add(new Guid((byte[])v));
                            else if (Guid.TryParse(Convert.ToString(v), out g)) guids.Add(g);
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

        /// <summary>
        /// The quantities as they are now: per item (with its group path), the number of
        /// objects and the sums of the item's calculated variables (Length, Area, Volume,
        /// Count...), in the Quantification project's units.
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
                string sums = string.Join("", quantities.Select(q => ", SUM(o." + q.ValueDbColumnName + ")"));
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
                            { "item", r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2)) },
                            { "objects", Convert.ToInt64(r.GetValue(3)) }
                        };
                        for (int q = 0; q < quantities.Count; q++)
                        {
                            object v = r.GetValue(4 + q);
                            if (v != null && v != DBNull.Value) row[quantities[q].DisplayName] = Math.Round(Convert.ToDouble(v), 3);
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
