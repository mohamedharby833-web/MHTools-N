using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Quantification through the bridge: what is set up, what a takeoff would add,
    /// doing it (a model change: dryRun first, then ChangeGate), and the quantities.
    /// The work itself is TakeoffService's - the ribbon's Quantities window uses the same.
    /// </summary>
    internal static class TakeoffMethods
    {
        public static object Info(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            var info = TakeoffService.Describe(doc);
            string why;
            info["ready"] = TakeoffService.IsReady(doc, out why);
            if (why != null) info["whyNotReady"] = why;
            return info;
        }

        public static object Preview(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            TakeoffPlan plan = Plan(doc, args);
            return Report(doc, plan, BridgeWire.Int(args, "limit", 80, 0, 5000));
        }

        public static object Apply(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            string why;
            if (!TakeoffService.IsReady(doc, out why)) throw new BridgeException("NOT_SET_UP", why);

            TakeoffPlan plan = Plan(doc, args);
            var report = Report(doc, plan, 0);
            bool replace = BridgeWire.Bool(args, "replace", false);
            int again = replace ? plan.Lines.Sum(l => l.Already.Count) : 0;
            if (plan.ToAdd + again == 0)
            {
                report["nothingToDo"] = "Every element found is already taken off (replace=true takes them off again).";
                return report;
            }

            int types = plan.Lines.Count(l => l.ToAdd.Count > 0);
            string what = "Quantification: take off " + plan.ToAdd + " element(s) onto " + types + " item(s) (one per Revit type; ducts, pipes, their fittings, accessories and insulation one per size under their type, " +
                          "with sheet thickness and weight for ducts), under item groups per discipline and category (" +
                          string.Join(", ", plan.Lines.Where(l => l.ToAdd.Count > 0).Select(l => l.Discipline + " " + l.Category).Distinct().Take(8)) +
                          (plan.Lines.Select(l => l.Discipline + l.Category).Distinct().Count() > 8 ? ", ..." : "") +
                          "). " + (replace ? again + " element(s) already taken off are removed from their items and taken off again; items and groups left empty are removed." : "Elements already taken off are left as they are.");
            if (BridgeWire.Bool(args, "dryRun", true))
            {
                report["dryRun"] = true;
                report["wouldDo"] = what;
                report["next"] = "Nothing was changed. Show the user what would change; call again with dryRun=false to do it.";
                return report;
            }

            ChangeGate.Allow(host, what);
            TakeoffResult result;
            using (var progress = new NavisProgress("MHTools-N: Quantification takeoff"))
                result = TakeoffService.Apply(doc, plan, progress, null, replace);

            return new Dictionary<string, object>
            {
                { "done", true },
                { "itemGroupsCreated", result.GroupsCreated },
                { "itemsCreated", result.ItemsCreated },
                { "objectsTakenOff", result.ObjectsAdded },
                { "objectsRemovedFirst", result.ObjectsRemoved }, { "emptyItemsRemoved", result.ItemsRemoved }, { "emptyGroupsRemoved", result.GroupsRemoved },
                { "failed", result.Failed },
                { "errors", result.Errors },
                { "next", "get_takeoff_summary shows the quantities. In Navisworks: Quantification workbook (Item view)." }
            };
        }

        public static object Summary(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            string why;
            if (!TakeoffService.IsReady(doc, out why)) throw new BridgeException("NOT_SET_UP", why);
            string filter = BridgeWire.Text(args, "filter");
            int limit = BridgeWire.Int(args, "limit", 200, 1, 5000);
            var rows = TakeoffService.Summary(doc, int.MaxValue)
                .Where(r => filter.Length == 0 || ((string)r["group"] + " / " + (string)r["item"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            return new Dictionary<string, object>
            {
                { "items", rows.Take(limit).ToList() },
                { "total", rows.Count },
                { "note", "Per item: Length, Area, Volume, Weight, Count summed over its objects; Width, Height, Thickness, Perimeter per piece (average). Units as chosen at Quantification Project Setup." }
            };
        }

        // -------------------------------------------------------------- helpers

        private static TakeoffPlan Plan(Document doc, Dictionary<string, object> args)
        {
            using (var progress = new NavisProgress("MHTools-N: reading the model for Quantification"))
                return TakeoffService.Plan(doc, BridgeWire.Strings(args, "categories"), BridgeWire.Strings(args, "disciplines"),
                    BridgeWire.Text(args, "model"), progress);
        }

        private static Dictionary<string, object> Report(Document doc, TakeoffPlan plan, int limit)
        {
            string why;
            bool ready = TakeoffService.IsReady(doc, out why);
            var report = new Dictionary<string, object>
            {
                { "quantificationReady", ready },
                { "elements", plan.Elements },
                { "toTakeOff", plan.ToAdd },
                { "alreadyTakenOff", plan.Lines.Sum(l => l.AlreadyTakenOff) },
                { "items", plan.Lines.Count },
                { "byDiscipline", plan.Lines.GroupBy(l => l.Discipline).ToDictionary(g => g.Key, g => g.Sum(l => l.ToAdd.Count + l.AlreadyTakenOff)) },
                { "byCategory", plan.Lines.GroupBy(l => l.Discipline + " " + l.Category).OrderByDescending(g => g.Sum(l => l.ToAdd.Count))
                    .Take(40).ToDictionary(g => g.Key, g => g.Sum(l => l.ToAdd.Count + l.AlreadyTakenOff)) },
                { "warnings", plan.Warnings.Take(20).ToList() }
            };
            if (!ready) report["whyNotReady"] = why;
            if (limit > 0)
                report["lines"] = plan.Lines.Take(limit).Select(l => (object)new Dictionary<string, object>
                {
                    { "group", l.DisciplineName + " / " + l.Category + (l.Size != null ? " / " + l.Type : "") }, { "item", l.ItemName },
                    { "toAdd", l.ToAdd.Count }, { "already", l.AlreadyTakenOff }
                }).ToList();
            return report;
        }
    }
}
