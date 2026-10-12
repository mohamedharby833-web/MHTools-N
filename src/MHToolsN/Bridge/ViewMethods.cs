using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// The view: a picture of it, and the changes an agent may make to it without
    /// asking - selection, zoom, hiding, temporary colours, going to a saved
    /// viewpoint. None of these touch the coordination data (sets, tests, results),
    /// and temporary colours are not saved with the file.
    /// </summary>
    internal static class ViewMethods
    {
        // --------------------------------------------------------- capture_view

        public static object Capture(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            int width = BridgeWire.Int(args, "width", 1024, 200, 1920);
            int height = BridgeWire.Int(args, "height", 768, 150, 1440);
            if (doc.ActiveView == null) throw new BridgeException("NO_VIEW", "Navisworks has no active 3D view.");

            byte[] jpeg;
            int w, h;
            using (Bitmap bitmap = doc.ActiveView.GenerateImage(ImageGenerationStyle.ScenePlusOverlay, width, height, true))
            {
                if (bitmap == null) throw new BridgeException("FAILED", "Navisworks made no picture of the view.");
                jpeg = ClashResultsReader.Jpeg(bitmap);
                w = bitmap.Width;
                h = bitmap.Height;
            }

            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);
            var result = new Dictionary<string, object> { { "selectedItems", doc.CurrentSelection.SelectedItems.Count } };
            Viewpoint vp = ModelMethods.Safe(() => doc.CurrentViewpoint.Value);
            if (vp != null)
                result["cameraMeters"] = ModelMethods.Point(vp.Position, mmPerUnit);
            result["image"] = new Dictionary<string, object>
            {
                { "mimeType", "image/jpeg" }, { "width", w }, { "height", h }, { "base64", Convert.ToBase64String(jpeg) }
            };
            return result;
        }

        // ----------------------------------------------------------- targets

        /// <summary>
        /// What a view or set tool acts on, from its arguments: itemIds, a saved set
        /// (set), the user's current selection (useSelection), or a clash's two items
        /// (clashTest + clash). Exactly one of them.
        /// </summary>
        internal static ModelItemCollection Targets(ItemRegistry items, Document doc, Dictionary<string, object> args)
        {
            var targets = new ModelItemCollection();
            List<string> ids = BridgeWire.Strings(args, "itemIds");
            string set = BridgeWire.Text(args, "set");
            bool useSelection = BridgeWire.Bool(args, "useSelection", false);
            string test = BridgeWire.Text(args, "clashTest"), clash = BridgeWire.Text(args, "clash");

            int given = (ids.Count > 0 ? 1 : 0) + (set.Length > 0 ? 1 : 0) + (useSelection ? 1 : 0) + (test.Length > 0 ? 1 : 0);
            if (given != 1)
                throw new BridgeException("INVALID_ARGUMENT", "Give exactly one of: itemIds, set, useSelection=true, or clashTest + clash.");

            if (ids.Count > 0)
            {
                foreach (string id in ids) targets.Add(items.Get(id));
            }
            else if (set.Length > 0)
            {
                SelectionSet found = ModelMethods.FindSet(doc.SelectionSets.RootItem, "", set);
                if (found == null) throw new BridgeException("NOT_FOUND", "No saved set '" + set + "'. Use list_sets.");
                foreach (ModelItem item in found.GetSelectedItems(doc)) targets.Add(item);
            }
            else if (useSelection)
            {
                foreach (ModelItem item in doc.CurrentSelection.SelectedItems) targets.Add(item);
            }
            else
            {
                ClashResult r = ClashMethods.FindResult(ClashMethods.FindTest(doc, test), clash);
                ModelItem one = r.CompositeItem1 ?? r.Item1, two = r.CompositeItem2 ?? r.Item2;
                if (one != null) targets.Add(one);
                if (two != null) targets.Add(two);
            }
            if (targets.Count == 0) throw new BridgeException("NOT_FOUND", "Nothing to act on: the items, set or selection is empty.");
            return targets;
        }

        // ------------------------------------------------------- select_items

        public static object Select(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            ModelItemCollection targets = Targets(items, doc, args);
            if (BridgeWire.Bool(args, "add", false)) doc.CurrentSelection.AddRange(targets);
            else doc.CurrentSelection.CopyFrom(targets);
            return new Dictionary<string, object> { { "selected", doc.CurrentSelection.SelectedItems.Count } };
        }

        // ------------------------------------------------------ zoom_to_items

        public static object Zoom(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            ModelItemCollection targets = Targets(items, doc, args);
            BoundingBox3D box = targets.BoundingBox();
            if (box == null || box.IsEmpty) throw new BridgeException("NO_GEOMETRY", "The items have no geometry to zoom to.");
            if (doc.ActiveView == null) throw new BridgeException("NO_VIEW", "Navisworks has no active 3D view.");

            // A little room around them, at least 1 m each way.
            double mmPerUnit = UnitConverter.MillimetresPerUnit(doc.Units);
            double min = 1000.0 / Math.Max(1e-9, mmPerUnit);
            Point3D c = box.Center;
            Vector3D s = box.Size;
            double hx = Math.Max(s.X * 1.2, min) / 2, hy = Math.Max(s.Y * 1.2, min) / 2, hz = Math.Max(s.Z * 1.2, min) / 2;
            var room = new BoundingBox3D(new Point3D(c.X - hx, c.Y - hy, c.Z - hz), new Point3D(c.X + hx, c.Y + hy, c.Z + hz));

            Viewpoint vp = doc.ActiveView.CreateViewpointCopy();
            vp.ZoomBox(room);
            doc.ActiveView.CopyViewpointFrom(vp, ViewChange.JumpCut);
            if (BridgeWire.Bool(args, "select", true)) doc.CurrentSelection.CopyFrom(targets);
            return new Dictionary<string, object> { { "zoomedTo", targets.Count }, { "selected", BridgeWire.Bool(args, "select", true) } };
        }

        // ------------------------------------------------------ set_visibility

        public static object Visibility(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            string action = BridgeWire.Text(args, "action").ToLowerInvariant();
            if (action == "show_all")
            {
                doc.Models.ResetAllHidden();
                return new Dictionary<string, object> { { "action", "show_all" } };
            }

            ModelItemCollection targets = Targets(items, doc, args);
            switch (action)
            {
                case "hide":
                    doc.Models.SetHidden(targets, true);
                    break;
                case "show":
                    doc.Models.SetHidden(targets, false);
                    break;
                case "isolate":
                    Isolate(doc, targets);
                    break;
                default:
                    throw new BridgeException("INVALID_ARGUMENT", "action must be isolate, hide, show or show_all.");
            }
            return new Dictionary<string, object> { { "action", action }, { "items", targets.Count } };
        }

        /// <summary>
        /// Hides everything but the targets: every branch off the path from a model's
        /// root down to a target is hidden, and nothing on the path itself - so the
        /// targets show however deep they sit.
        /// </summary>
        private static void Isolate(Document doc, ModelItemCollection targets)
        {
            doc.Models.ResetAllHidden();
            var keep = new HashSet<ModelItem>();
            foreach (ModelItem target in targets)
                for (ModelItem a = target; a != null; a = a.Parent) keep.Add(a);

            var hide = new ModelItemCollection();
            var keepTargets = new HashSet<ModelItem>();
            foreach (ModelItem t in targets) keepTargets.Add(t);
            foreach (ModelItem item in keep)
            {
                if (keepTargets.Contains(item)) continue; // a target shows whole, children and all
                foreach (ModelItem child in item.Children)
                    if (!keep.Contains(child)) hide.Add(child);
            }
            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
                if (model.RootItem != null && !keep.Contains(model.RootItem)) hide.Add(model.RootItem);
            doc.Models.SetHidden(hide, true);
        }

        // -------------------------------------------- color_items / reset_appearance

        public static object Color(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            ModelItemCollection targets = Targets(items, doc, args);
            string colour = BridgeWire.Text(args, "color");
            int transparency = BridgeWire.Int(args, "transparency", -1, -1, 100);
            if (colour.Length == 0 && transparency < 0) throw new BridgeException("INVALID_ARGUMENT", "Give a color (#RRGGBB or a name) and/or a transparency (0-100).");

            if (colour.Length > 0) doc.Models.OverrideTemporaryColor(targets, ParseColour(colour));
            if (transparency >= 0) doc.Models.OverrideTemporaryTransparency(targets, transparency / 100.0);
            return new Dictionary<string, object>
            {
                { "items", targets.Count }, { "color", colour }, { "transparency", transparency },
                { "note", "Temporary: not saved with the file. reset_appearance takes it off." }
            };
        }

        public static object ResetAppearance(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            bool all = BridgeWire.Bool(args, "all", false);
            if (all)
            {
                doc.Models.ResetAllTemporaryMaterials();
                return new Dictionary<string, object> { { "reset", "all" } };
            }
            ModelItemCollection targets = Targets(items, doc, args);
            doc.Models.ResetTemporaryMaterials(targets);
            return new Dictionary<string, object> { { "reset", targets.Count } };
        }

        private static readonly Dictionary<string, string> Named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "red", "#E53935" }, { "green", "#43A047" }, { "blue", "#1E88E5" }, { "yellow", "#FDD835" }, { "orange", "#FB8C00" },
            { "purple", "#8E24AA" }, { "cyan", "#00ACC1" }, { "magenta", "#D81B60" }, { "white", "#FFFFFF" }, { "black", "#000000" },
            { "grey", "#9E9E9E" }, { "gray", "#9E9E9E" }, { "teal", "#0B6E6E" }
        };

        private static Autodesk.Navisworks.Api.Color ParseColour(string text)
        {
            string hex;
            if (!Named.TryGetValue(text.Trim(), out hex)) hex = text.Trim();
            hex = hex.TrimStart('#');
            int rgb;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                throw new BridgeException("INVALID_ARGUMENT", "color must be #RRGGBB or one of: " + string.Join(", ", Named.Keys) + ".");
            return Autodesk.Navisworks.Api.Color.FromByteRGB((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        // --------------------------------------------------- go_to_viewpoint

        public static object GoToViewpoint(ItemRegistry items, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(items);
            string wanted = BridgeWire.Text(args, "viewpoint");
            SavedViewpoint vp = FindViewpoint(doc.SavedViewpoints.RootItem, "", wanted);
            if (vp == null) throw new BridgeException("NOT_FOUND", "No saved viewpoint '" + wanted + "'. Use list_viewpoints for the names or paths.");
            doc.SavedViewpoints.CurrentSavedViewpoint = vp;
            return new Dictionary<string, object> { { "viewpoint", vp.DisplayName } };
        }

        internal static SavedViewpoint FindViewpoint(GroupItem group, string prefix, string wanted)
        {
            if (group == null || string.IsNullOrWhiteSpace(wanted)) return null;
            SavedViewpoint byName = null;
            foreach (SavedItem item in group.Children)
            {
                string path = prefix.Length == 0 ? item.DisplayName : prefix + "/" + item.DisplayName;
                if (item is SavedViewpoint)
                {
                    if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) return (SavedViewpoint)item;
                    if (byName == null && string.Equals(item.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)) byName = (SavedViewpoint)item;
                }
                else if (item is GroupItem)
                {
                    SavedViewpoint found = FindViewpoint((GroupItem)item, path, wanted);
                    if (found != null && string.Equals(path + "/" + found.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)) return found;
                    if (found != null && byName == null) byName = found;
                }
            }
            return byName;
        }
    }
}
