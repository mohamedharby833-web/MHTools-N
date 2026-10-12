using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using MHToolsN.Services;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// The read tools for the model: its tree, finding items, their properties, the
    /// current selection, saved sets and viewpoints. All run on the UI thread, read
    /// only, and page or cap what they return - a federated model has hundreds of
    /// thousands of nodes and an agent only needs a few at a time.
    ///
    /// Navisworks collections are walked with foreach only (their CopyTo is broken).
    /// </summary>
    internal static class ModelMethods
    {
        private const string ElementCategoryInternal = "LcRevitData_Element";
        private const string CategoryPropertyInternal = "LcRevitPropertyElementCategory";
        private const int MaxText = 500;

        public static Document OpenDocument(ItemRegistry items)
        {
            Document doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear)
                throw new BridgeException("NO_DOCUMENT", "No model is open in Navisworks. Ask the user to open or append the coordination model.");
            items.Check(doc);
            return doc;
        }

        /// <summary>What an agent needs to recognise an item: id, name, node type, Revit category.</summary>
        public static Dictionary<string, object> Summary(ItemRegistry items, ModelItem item, bool details = false)
        {
            var row = new Dictionary<string, object>
            {
                { "id", items.Id(item) },
                { "name", item.DisplayName },
                { "nodeType", item.ClassDisplayName },
                { "hasGeometry", item.HasGeometry }
            };
            string category = Safe(() => ClashResultsReader.Property(item, p => p.FindPropertyByName(ElementCategoryInternal, CategoryPropertyInternal))
                                         ?? ClashResultsReader.Property(item, p => p.FindPropertyByDisplayName("Element", "Category")));
            if (category != null) row["category"] = category;
            if (details)
            {
                MHToolsN.Model.ItemInfo info = ClashResultsReader.Describe(item);
                if (info.Type != null) row["type"] = info.Type;
                if (info.ElementId != null) row["elementId"] = info.ElementId;
                if (info.Level != null) row["level"] = info.Level;
                if (info.File != null) row["file"] = info.File;
            }
            return row;
        }

        // ------------------------------------------------------- get_model_tree

        public static object Tree(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            string id = BridgeWire.Text(args, "itemId");
            int offset = BridgeWire.Int(args, "offset", 0, 0, int.MaxValue);
            int limit = BridgeWire.Int(args, "limit", 100, 1, 500);

            var rows = new List<object>();
            int total = 0;
            if (string.IsNullOrWhiteSpace(id))
            {
                foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
                {
                    if (model.RootItem == null) continue;
                    if (total++ < offset || rows.Count >= limit) continue;
                    var row = Summary(items, model.RootItem);
                    row["file"] = model.FileName;
                    row["hasChildren"] = HasChildren(model.RootItem);
                    rows.Add(row);
                }
                return Page("models", rows, total, offset, limit, null);
            }

            ModelItem parent = items.Get(id);
            foreach (ModelItem child in parent.Children)
            {
                if (total++ < offset || rows.Count >= limit) continue;
                var row = Summary(items, child);
                row["hasChildren"] = HasChildren(child);
                rows.Add(row);
            }
            return Page("children", rows, total, offset, limit, new Dictionary<string, object>
            {
                { "parent", Summary(items, parent) },
                { "path", ClashResultsReader.TreePath(parent) }
            });
        }

        private static bool HasChildren(ModelItem item)
        {
            foreach (ModelItem child in item.Children) return true;
            return false;
        }

        // ----------------------------------------------------------- find_items

        public static object Find(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            string category = BridgeWire.Text(args, "category");
            string model = BridgeWire.Text(args, "model");
            int limit = BridgeWire.Int(args, "limit", 100, 1, 1000);
            var conditions = new List<Dictionary<string, object>>();
            object raw;
            if (args.TryGetValue("conditions", out raw) && raw is System.Collections.IEnumerable && !(raw is string))
                foreach (object c in (System.Collections.IEnumerable)raw)
                    if (c is Dictionary<string, object>) conditions.Add((Dictionary<string, object>)c);

            if (string.IsNullOrWhiteSpace(category) && conditions.Count == 0)
                throw new BridgeException("INVALID_ARGUMENT", "Give a category (e.g. Walls) and/or at least one condition.");

            var search = new Search();
            var scope = new ModelItemCollection();
            foreach (Autodesk.Navisworks.Api.Model m in doc.Models)
            {
                if (m.RootItem == null) continue;
                if (string.IsNullOrWhiteSpace(model) || (m.FileName ?? "").IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)
                    scope.Add(m.RootItem);
            }
            if (scope.Count == 0) throw new BridgeException("NOT_FOUND", "No appended model's file name contains '" + model + "'.");
            search.Selection.CopyFrom(scope);
            search.Locations = SearchLocations.DescendantsAndSelf;
            search.PruneBelowMatch = true;

            if (!string.IsNullOrWhiteSpace(category))
                search.SearchConditions.Add(SearchCondition.HasPropertyByName(ElementCategoryInternal, CategoryPropertyInternal)
                    .EqualValue(VariantData.FromDisplayString(category.Trim())));
            foreach (var c in conditions)
                search.SearchConditions.Add(Condition(c));

            ModelItemCollection found = search.FindAll(doc, false);
            var rows = new List<object>();
            int total = 0;
            foreach (ModelItem item in found)
            {
                total++;
                if (rows.Count < limit) rows.Add(Summary(items, item, true));
            }
            return Page("items", rows, total, 0, limit, null);
        }

        /// <summary>{ category, property, value, match: equals | contains | wildcard | exists | notEquals }, display names.</summary>
        private static SearchCondition Condition(Dictionary<string, object> c)
        {
            string cat = BridgeWire.Text(c, "category"), prop = BridgeWire.Text(c, "property");
            string value = BridgeWire.Text(c, "value"), match = BridgeWire.Text(c, "match", "equals").ToLowerInvariant();
            if (cat.Length == 0 || prop.Length == 0)
                throw new BridgeException("INVALID_ARGUMENT", "Each condition needs a category and a property (display names as in the Properties window, e.g. Element / Type).");

            SearchCondition has = SearchCondition.HasPropertyByDisplayName(cat, prop);
            switch (match)
            {
                case "exists": return has;
                case "contains": return has.DisplayStringContains(value).IgnoreStringValueCase();
                case "wildcard": return has.DisplayStringWildcard(value).IgnoreStringValueCase();
                case "notequals": return has.EqualValue(VariantData.FromDisplayString(value)).IgnoreStringValueCase().Negate();
                case "equals": return has.EqualValue(VariantData.FromDisplayString(value)).IgnoreStringValueCase();
                default: throw new BridgeException("INVALID_ARGUMENT", "match must be equals, notEquals, contains, wildcard or exists.");
            }
        }

        // -------------------------------------------------- get_item_properties

        public static object Properties(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            List<string> ids = BridgeWire.Strings(args, "itemIds");
            if (ids.Count == 0) throw new BridgeException("INVALID_ARGUMENT", "Give itemIds (from find_items, get_model_tree, get_selection...).");
            if (ids.Count > 20) throw new BridgeException("INVALID_ARGUMENT", "At most 20 items at a time.");
            var only = new HashSet<string>(BridgeWire.Strings(args, "categories"), StringComparer.OrdinalIgnoreCase);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);

            var result = new List<object>();
            foreach (string id in ids)
            {
                ModelItem item = items.Get(id);
                var row = Summary(items, item, true);
                row["path"] = ClashResultsReader.TreePath(item);
                BoundingBox3D box = Safe(() => item.BoundingBox());
                if (box != null && !box.IsEmpty)
                    row["boundingBoxMeters"] = new Dictionary<string, object>
                    {
                        { "min", Point(box.Min, mmPerUnit) }, { "max", Point(box.Max, mmPerUnit) }
                    };

                var categories = new List<object>();
                foreach (PropertyCategory category in item.PropertyCategories)
                {
                    if (only.Count > 0 && !only.Contains(category.DisplayName) && !only.Contains(category.Name)) continue;
                    var props = new Dictionary<string, object>();
                    foreach (DataProperty p in category.Properties)
                    {
                        string text = Safe(() => p.Value == null ? null : ClashResultsReader.ValueText(p.Value));
                        if (text == null) continue;
                        if (text.Length > MaxText) text = text.Substring(0, MaxText) + "...";
                        string key = p.DisplayName;
                        int n = 2;
                        while (props.ContainsKey(key)) key = p.DisplayName + " (" + n++ + ")";
                        props[key] = text;
                    }
                    if (props.Count > 0) categories.Add(new Dictionary<string, object> { { "category", category.DisplayName }, { "properties", props } });
                }
                row["propertyCategories"] = categories;
                result.Add(row);
            }
            return new Dictionary<string, object> { { "items", result } };
        }

        // -------------------------------------------------------- get_selection

        public static object Selection(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            int limit = BridgeWire.Int(args, "limit", 100, 1, 1000);
            var rows = new List<object>();
            int total = 0;
            foreach (ModelItem item in doc.CurrentSelection.SelectedItems)
            {
                total++;
                if (rows.Count < limit) rows.Add(Summary(items, item, true));
            }
            return Page("items", rows, total, 0, limit, null);
        }

        // ------------------------------------------------- list_sets / get_set_items

        public static object Sets(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            bool count = BridgeWire.Bool(args, "countItems", false);
            string folder = BridgeWire.Text(args, "folder");
            var rows = new List<object>();
            WalkSets(doc, doc.SelectionSets.RootItem, "", folder, count, rows);
            return new Dictionary<string, object> { { "sets", rows }, { "total", rows.Count } };
        }

        private static void WalkSets(Document doc, GroupItem group, string prefix, string folder, bool count, List<object> rows)
        {
            if (group == null) return;
            foreach (SavedItem item in group.Children)
            {
                string path = prefix.Length == 0 ? item.DisplayName : prefix + "/" + item.DisplayName;
                var set = item as SelectionSet;
                if (set != null)
                {
                    if (folder.Length > 0 && !prefix.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                    var row = new Dictionary<string, object>
                    {
                        { "name", set.DisplayName },
                        { "path", path },
                        { "kind", set.HasSearch ? "search" : "selection" },
                        { "clashMatrix", prefix.StartsWith(NavisClashCreator.FolderName, StringComparison.OrdinalIgnoreCase) }
                    };
                    if (count) row["items"] = Safe(() => set.GetSelectedItems(doc).Count);
                    rows.Add(row);
                }
                else if (item is GroupItem)
                {
                    WalkSets(doc, (GroupItem)item, path, folder, count, rows);
                }
            }
        }

        public static object SetItems(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            string name = BridgeWire.Text(args, "set");
            int limit = BridgeWire.Int(args, "limit", 100, 1, 1000);
            SelectionSet set = FindSet(doc.SelectionSets.RootItem, "", name);
            if (set == null) throw new BridgeException("NOT_FOUND", "No saved set '" + name + "'. Use list_sets for the names or paths (Folder/Name).");

            var rows = new List<object>();
            int total = 0;
            foreach (ModelItem item in set.GetSelectedItems(doc))
            {
                total++;
                if (rows.Count < limit) rows.Add(Summary(items, item, true));
            }
            return Page("items", rows, total, 0, limit, new Dictionary<string, object> { { "set", set.DisplayName }, { "kind", set.HasSearch ? "search" : "selection" } });
        }

        /// <summary>By path ("Clash Matrix/AR Walls") or, when unique, by name alone.</summary>
        internal static SelectionSet FindSet(GroupItem root, string prefix, string wanted)
        {
            var byName = new List<SelectionSet>();
            SelectionSet byPath = FindSet(root, prefix, wanted, byName);
            if (byPath != null) return byPath;
            if (byName.Count > 1)
                throw new BridgeException("AMBIGUOUS", byName.Count + " sets are called '" + wanted + "'. Give the path, Folder/Name, from list_sets.");
            return byName.Count == 1 ? byName[0] : null;
        }

        private static SelectionSet FindSet(GroupItem group, string prefix, string wanted, List<SelectionSet> byName)
        {
            if (group == null) return null;
            foreach (SavedItem item in group.Children)
            {
                string path = prefix.Length == 0 ? item.DisplayName : prefix + "/" + item.DisplayName;
                var set = item as SelectionSet;
                if (set != null)
                {
                    if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) return set;
                    if (string.Equals(set.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)) byName.Add(set);
                }
                else if (item is GroupItem)
                {
                    SelectionSet found = FindSet((GroupItem)item, path, wanted, byName);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // ------------------------------------------------------- list_viewpoints

        public static object Viewpoints(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = OpenDocument(items);
            int limit = BridgeWire.Int(args, "limit", 300, 1, 2000);
            string filter = BridgeWire.Text(args, "filter");
            var rows = new List<object>();
            int total = 0;
            WalkViewpoints(doc.SavedViewpoints.RootItem, "", filter, rows, ref total, limit);
            return Page("viewpoints", rows, total, 0, limit, null);
        }

        private static void WalkViewpoints(GroupItem group, string prefix, string filter, List<object> rows, ref int total, int limit)
        {
            if (group == null) return;
            foreach (SavedItem item in group.Children)
            {
                string path = prefix.Length == 0 ? item.DisplayName : prefix + "/" + item.DisplayName;
                if (item is SavedViewpoint)
                {
                    if (filter.Length > 0 && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++;
                    if (rows.Count < limit) rows.Add(new Dictionary<string, object> { { "name", item.DisplayName }, { "path", path } });
                }
                else if (item is GroupItem)
                {
                    WalkViewpoints((GroupItem)item, path, filter, rows, ref total, limit);
                }
            }
        }

        // -------------------------------------------------------------- helpers

        internal static Dictionary<string, object> Page(string name, List<object> rows, int total, int offset, int limit,
            Dictionary<string, object> extra)
        {
            var page = extra ?? new Dictionary<string, object>();
            page[name] = rows;
            page["total"] = total;
            page["returned"] = rows.Count;
            if (offset + rows.Count < total)
                page["more"] = "Showing " + (offset + 1) + "-" + (offset + rows.Count) + " of " + total +
                               (offset > 0 || name == "children" || name == "models" ? "; ask again with offset " + (offset + rows.Count) + "." : "; narrow the search or raise limit.");
            return page;
        }

        internal static Dictionary<string, object> Point(Point3D p, double mmPerUnit)
        {
            return new Dictionary<string, object>
            {
                { "x", Math.Round(p.X * mmPerUnit / 1000.0, 3) },
                { "y", Math.Round(p.Y * mmPerUnit / 1000.0, 3) },
                { "z", Math.Round(p.Z * mmPerUnit / 1000.0, 3) }
            };
        }

        internal static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception) { return default(T); }
        }
    }
}
