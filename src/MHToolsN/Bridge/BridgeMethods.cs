using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using MHToolsN.Services;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// What the bridge can do in Navisworks: one entry per method the server may
    /// ask for. Every body runs on Navisworks' UI thread (through UiDispatcher) and
    /// returns plain data - dictionaries, lists, strings, numbers - for the wire.
    ///
    /// Navisworks collections are walked with foreach only: their CopyTo is broken
    /// ("Index is larger than provided array"), so no ToList / new List(collection).
    /// </summary>
    internal static class BridgeMethods
    {
        public static Func<object> Find(string method, BridgeHost host, Dictionary<string, object> args)
        {
            switch (method)
            {
                case "get_context": return () => Context(host);

                // model (ModelMethods)
                case "get_model_tree": return () => ModelMethods.Tree(host.Items, args);
                case "find_items": return () => ModelMethods.Find(host.Items, args);
                case "get_item_properties": return () => ModelMethods.Properties(host.Items, args);
                case "get_selection": return () => ModelMethods.Selection(host.Items, args);
                case "list_sets": return () => ModelMethods.Sets(host.Items, args);
                case "get_set_items": return () => ModelMethods.SetItems(host.Items, args);
                case "list_viewpoints": return () => ModelMethods.Viewpoints(host.Items, args);

                // clash (ClashMethods)
                case "get_clash_summary": return () => ClashMethods.Summary(host.Items, args);
                case "list_clash_tests": return () => ClashMethods.Tests(host.Items, args);
                case "get_clash_results": return () => ClashMethods.Results(host.Items, args);
                case "get_clash_image": return () => ClashMethods.Image(host.Items, args);

                // view (ViewMethods)
                case "capture_view": return () => ViewMethods.Capture(host.Items, args);
                case "select_items": return () => ViewMethods.Select(host.Items, args);
                case "zoom_to_items": return () => ViewMethods.Zoom(host.Items, args);
                case "set_visibility": return () => ViewMethods.Visibility(host.Items, args);
                case "color_items": return () => ViewMethods.Color(host.Items, args);
                case "reset_appearance": return () => ViewMethods.ResetAppearance(host.Items, args);
                case "go_to_viewpoint": return () => ViewMethods.GoToViewpoint(host.Items, args);

                // model changes (ChangeMethods): preview by default, then ChangeGate
                case "create_set": return () => ChangeMethods.CreateSet(host, args);
                case "save_viewpoint": return () => ChangeMethods.SaveViewpoint(host, args);
                case "set_clash_status": return () => ChangeMethods.SetClashStatus(host, args);
                case "run_clash_tests": return () => ChangeMethods.RunTests(host, args);
                case "preview_clash_matrix": return () => ChangeMethods.PreviewMatrix(host, args);
                case "apply_clash_matrix": return () => ChangeMethods.ApplyMatrix(host, args);

                // Trusted C# (ScriptRunner)
                case "execute_code": return () => ScriptRunner.Execute(host, args);

                // Quantification (TakeoffMethods)
                case "get_takeoff_info": return () => TakeoffMethods.Info(host, args);
                case "preview_takeoff": return () => TakeoffMethods.Preview(host, args);
                case "apply_takeoff": return () => TakeoffMethods.Apply(host, args);
                case "get_takeoff_summary": return () => TakeoffMethods.Summary(host, args);

                default: return null;
            }
        }

        // ----------------------------------------------------------- get_context

        private static object Context(BridgeHost host)
        {
            Document doc = NavisApp.ActiveDocument;
            var version = NavisApp.Version;
            var context = new Dictionary<string, object>
            {
                { "navisworks", new Dictionary<string, object>
                    {
                        { "product", string.IsNullOrWhiteSpace(version.RuntimeProductName) ? "Navisworks" : version.RuntimeProductName },
                        { "version", BridgeHost.NavisworksYear },
                        { "api", version.ApiMajor + "." + version.ApiMinor },
                        { "language", version.RuntimeLanguage }
                    }
                },
                { "addin", new Dictionary<string, object> { { "name", "MHTools-N" }, { "version", UI.MhTheme.Version } } },
                { "sessionId", host.SessionId },
                { "codeMode", host.ModeName }
            };

            if (doc == null || doc.IsClear)
            {
                context["document"] = null;
                context["note"] = "No model is open in Navisworks. Ask the user to open or append the coordination model.";
                return context;
            }

            var models = new List<object>();
            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
            {
                models.Add(new Dictionary<string, object>
                {
                    { "name", SafeFileName(model.FileName) ?? (model.RootItem != null ? model.RootItem.DisplayName : "") },
                    { "path", model.FileName },
                    { "source", model.SourceFileName },
                    { "units", model.Units.ToString() },
                    { "creator", model.Creator }
                });
            }

            context["document"] = new Dictionary<string, object>
            {
                { "title", doc.Title },
                { "file", doc.FileName },
                { "units", doc.Units.ToString() },
                { "modified", doc.IsModified },
                { "models", models },
                { "selectedItems", doc.CurrentSelection.SelectedItems.Count },
                { "savedSets", CountSets(doc.SelectionSets.RootItem) },
                { "clashMatrixFolder", HasFolder(doc.SelectionSets.RootItem, NavisClashCreator.FolderName) },
                { "savedViewpoints", CountViewpoints(doc.SavedViewpoints.RootItem) },
                { "clashTests", CountTests(doc) }
            };
            return context;
        }

        private static string SafeFileName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path); }
            catch (Exception) { return path; }
        }

        private static int CountSets(GroupItem folder)
        {
            int count = 0;
            if (folder == null) return 0;
            foreach (SavedItem item in folder.Children)
            {
                var sub = item as GroupItem;
                if (sub != null && !(item is SelectionSet)) count += CountSets(sub);
                else if (item is SelectionSet) count++;
            }
            return count;
        }

        private static bool HasFolder(GroupItem root, string name)
        {
            if (root == null) return false;
            foreach (SavedItem item in root.Children)
                if (item is FolderItem && item.DisplayName == name) return true;
            return false;
        }

        private static int CountViewpoints(GroupItem folder)
        {
            int count = 0;
            if (folder == null) return 0;
            foreach (SavedItem item in folder.Children)
            {
                if (item is SavedViewpoint) count++;
                else if (item is GroupItem) count += CountViewpoints((GroupItem)item);
            }
            return count;
        }

        private static int CountTests(Document doc)
        {
            try
            {
                int count = 0;
                foreach (SavedItem item in doc.GetClash().TestsData.Value.TestsRoot.Children)
                    if (item is ClashTest) count++;
                return count;
            }
            catch (Exception)
            {
                return 0; // Navisworks Simulate / Freedom: no Clash Detective
            }
        }
    }
}
