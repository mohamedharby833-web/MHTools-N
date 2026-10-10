using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashMatrix.Model
{
    /// <summary>The clash results of one or more tests, ready to be written to Excel or PDF.</summary>
    public class ClashReport
    {
        public string DocumentName { get; set; }
        public DateTime Exported { get; set; }
        public List<TestReport> Tests { get; private set; }

        public ClashReport()
        {
            Tests = new List<TestReport>();
            Exported = DateTime.Now;
        }

        /// <summary>The result statuses in Clash Detective's order.</summary>
        public static readonly string[] Statuses = { "New", "Active", "Reviewed", "Approved", "Resolved" };
    }

    public class TestReport
    {
        public string Name { get; set; }

        /// <summary>"Hard", "Clearance", ...</summary>
        public string TestType { get; set; }

        /// <summary>"Complete", "Old", "New", "Partial" - whether the results are current.</summary>
        public string Status { get; set; }

        public double ToleranceMm { get; set; }

        /// <summary>High / Medium / Low, or empty (also empty before Navisworks 2026).</summary>
        public string Priority { get; set; }

        public DateTime? LastRun { get; set; }

        public List<ResultRow> Results { get; private set; }

        public TestReport()
        {
            Results = new List<ResultRow>();
        }

        public int Count(string status)
        {
            return Results.Count(r => r.Status == status);
        }
    }

    public class ResultRow
    {
        public string Name { get; set; }

        /// <summary>The clash group it sits in, if any.</summary>
        public string Group { get; set; }

        public string Status { get; set; }

        /// <summary>Distance in mm as Clash Detective shows it (negative = how far the items overlap).</summary>
        public double DistanceMm { get; set; }

        /// <summary>Clash point in metres.</summary>
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public ItemInfo Item1 { get; set; }
        public ItemInfo Item2 { get; set; }

        public string AssignedTo { get; set; }
        public string ApprovedBy { get; set; }
        public string ResolvedBy { get; set; }
        public DateTime? ResolvedTime { get; set; }

        /// <summary>The clash's own priority (High / Medium / Low; Navisworks 2026 and later).</summary>
        public string Priority { get; set; }
        public DateTime? ApprovedTime { get; set; }
        public DateTime? Created { get; set; }
        public string Description { get; set; }
        public string Comments { get; set; }

        /// <summary>A view of the clash (JPEG), when images were asked for.</summary>
        public byte[] Image { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
    }

    /// <summary>One side of a clash: the element and where it comes from.</summary>
    public class ItemInfo
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Type { get; set; }
        public string ElementId { get; set; }
        public string Level { get; set; }

        /// <summary>The layer the item sits on in the selection tree (a level, when the file was split by level).</summary>
        public string Layer { get; set; }

        /// <summary>The selection-tree path from the file down to the item: "file.nwc > Level 1 > Walls > Basic Wall > ...".</summary>
        public string Path { get; set; }
        public string File { get; set; }

        public static readonly ItemInfo Empty = new ItemInfo();

        /// <summary>"Ducts | Rectangular Duct | 123456".</summary>
        public string Summary
        {
            get
            {
                return string.Join(" | ", new[] { Category, string.IsNullOrEmpty(Type) ? Name : Type, ElementId }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
    }
}
