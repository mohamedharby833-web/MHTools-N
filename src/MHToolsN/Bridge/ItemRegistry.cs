using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Navisworks.Api;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Short ids ("n17") for the model items the bridge hands to an agent, so it can
    /// ask about them again (properties, a set's items, later selection and view).
    /// Navisworks has no stable id of its own for every node: InstanceGuid is often
    /// empty, and a path of child indexes is slow to compute. The id lives as long as
    /// the bridge and the document it came from; opening, appending or reloading
    /// models clears them all, and an old id then says so.
    ///
    /// Used on the UI thread only (from the bridge methods), so no locking.
    /// </summary>
    internal sealed class ItemRegistry
    {
        private const int Limit = 500000;

        private readonly Dictionary<string, ModelItem> _byId = new Dictionary<string, ModelItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ModelItem, string> _byItem = new Dictionary<ModelItem, string>();
        private int _next;
        private string _documentKey;

        /// <summary>Clears the ids when the document is not the one they came from.</summary>
        public void Check(Document doc)
        {
            string key = Key(doc);
            if (key == _documentKey) return;
            _documentKey = key;
            _byId.Clear();
            _byItem.Clear();
        }

        public string Id(ModelItem item)
        {
            string id;
            if (_byItem.TryGetValue(item, out id)) return id;
            if (_byId.Count >= Limit)
            {
                // A runaway agent listing millions of nodes; start again rather than eat memory.
                _byId.Clear();
                _byItem.Clear();
            }
            id = "n" + (++_next);
            _byId[id] = item;
            _byItem[item] = id;
            return id;
        }

        public ModelItem Get(string id)
        {
            ModelItem item;
            if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out item)) return item;
            throw new BridgeException("UNKNOWN_ITEM",
                "No item '" + id + "'. Item ids come from get_model_tree, find_items, get_selection, get_set_items or the clash tools, " +
                "and are lost when models are opened, appended or reloaded: look the item up again.");
        }

        private static string Key(Document doc)
        {
            if (doc == null) return "";
            var key = new StringBuilder(doc.FileName ?? "");
            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
                key.Append('|').Append(model.FileName);
            return key.ToString();
        }
    }
}
