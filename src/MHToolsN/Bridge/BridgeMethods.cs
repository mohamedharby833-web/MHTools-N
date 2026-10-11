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
