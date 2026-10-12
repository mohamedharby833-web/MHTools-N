using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using MHToolsN.Model;

namespace MHToolsN.Services
{
    /// <summary>A clash test as listed in the Export Results window.</summary>
    public sealed class TestChoice : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _selected = true;

        public string Name { get; set; }
        public string Status { get; set; }
        public int Total { get; set; }
        public int Open { get; set; }

        /// <summary>True when both sides are linked to the Clash Matrix sets.</summary>
        public bool IsClashMatrix { get; set; }

        public bool IsSelected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                var h = PropertyChanged;
                if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs("IsSelected"));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Reads clash tests and their results out of Clash Detective for the report.</summary>
    public static class ClashResultsReader
    {
        private const string ElementCategoryInternal = "LcRevitData_Element";
        private const string CategoryPropertyInternal = "LcRevitPropertyElementCategory";

        /// <summary>Every clash test, with its result counts.</summary>
        public static List<TestChoice> ListTests(Document document)
        {
            var list = new List<TestChoice>();
            HashSet<Guid> managed = NavisClashCreator.ManagedSetGuidsOf(document);
            foreach (ClashTest test in AllTests(document))
            {
                int total = 0, open = 0;
                foreach (ClashResult r in Results(test))
                {
                    total++;
                    if (IsOpen(r.Status)) open++;
                }
                list.Add(new TestChoice
                {
                    Name = test.DisplayName,
                    Status = test.Status.ToString(),
                    Total = total,
                    Open = open,
                    IsClashMatrix = NavisClashCreator.IsManagedTest(document, test, managed)
                });
            }
            return list;
        }

        /// <summary>New, Active or Reviewed - still to be dealt with.</summary>
        public static bool IsOpen(ClashResultStatus status)
        {
            return status == ClashResultStatus.New || status == ClashResultStatus.Active || status == ClashResultStatus.Reviewed;
        }

        /// <summary>
        /// Reads the named tests' results with one of the given <paramref name="statuses"/>
        /// (New, Active, ...); <paramref name="images"/> also renders a view of every clash
        /// (slow on a large test - the progress bar can cancel).
        /// </summary>
        public static ClashReport Read(Document document, ICollection<string> testNames, ICollection<string> statuses, bool images,
            NavisProgress progress)
        {
            var wanted = new HashSet<string>(testNames, StringComparer.Ordinal);
            double mmPerUnit = UnitConverter.MillimetresPerUnit(document.Units);
            DocumentClashTests testsData = document.GetClash().TestsData;

            var report = new ClashReport
            {
                DocumentName = !string.IsNullOrEmpty(document.FileName) ? Path.GetFileName(document.FileName) : document.Title
            };

            List<ClashTest> tests = AllTests(document).Where(t => wanted.Contains(t.DisplayName)).ToList();
            int totalResults = tests.Sum(t => Results(t).Count());
            int done = 0;

            // Pictures zoomed onto each clash; the view and colours are put back at the end.
            using (ClashImager imager = images ? new ClashImager(document, testsData, mmPerUnit) : null)
            foreach (ClashTest test in tests)
            {
                var tr = new TestReport
                {
                    Name = test.DisplayName,
                    TestType = test.TestType.ToString(),
                    Status = test.Status.ToString(),
                    ToleranceMm = test.Tolerance * mmPerUnit,
                    LastRun = test.LastRun,
                    Priority = PriorityText(test)
                };

                foreach (SavedItem child in test.Children)
                {
                    var group = child as ClashResultGroup;
                    IEnumerable<ClashResult> results = group != null
                        ? group.Children.OfType<ClashResult>()
                        : (child is ClashResult ? new[] { (ClashResult)child } : new ClashResult[0]);

                    foreach (ClashResult r in results)
                    {
                        done++;
                        if (progress != null)
                            progress.Report(done / (double)Math.Max(1, totalResults));
                        if (statuses != null && !statuses.Contains(r.Status.ToString()))
                            continue;

                        var row = new ResultRow
                        {
                            Name = r.DisplayName,
                            Group = group != null ? group.DisplayName : "",
                            Status = r.Status.ToString(),
                            DistanceMm = r.Distance * mmPerUnit,
                            X = r.Center.X * mmPerUnit / 1000.0,
                            Y = r.Center.Y * mmPerUnit / 1000.0,
                            Z = r.Center.Z * mmPerUnit / 1000.0,
                            Item1 = Describe(r.CompositeItem1 ?? r.Item1),
                            Item2 = Describe(r.CompositeItem2 ?? r.Item2),
                            AssignedTo = Who(r.AssignedTo),
                            ApprovedBy = Who(r.ApprovedBy),
                            ApprovedTime = RealDate(r.ApprovedTime),
                            Created = RealDate(r.CreatedTime),
                            Description = r.Description,
                            Comments = CommentsText(r)
                        };

#if NAVIS2026 || NAVIS2027
                        // Resolved by / when, and the clash's own priority, exist from Navisworks 2026.
                        row.ResolvedBy = Who(r.ResolvedBy);
                        row.ResolvedTime = RealDate(r.ResolvedTime);
                        row.Priority = r.Priority == 1 ? "High" : r.Priority == 2 ? "Medium" : r.Priority == 3 ? "Low" : "";
#endif

                        if (images)
                            AddImage(imager, r, row);

                        tr.Results.Add(row);
                    }
                }
                report.Tests.Add(tr);
            }
            return report;
        }

        internal static IEnumerable<ClashTest> AllTests(Document document)
        {
            var stack = new Stack<SavedItem>();
            var root = new List<SavedItem>();
            foreach (SavedItem item in document.GetClash().TestsData.Value.TestsRoot.Children)
                root.Add(item);
            for (int i = root.Count - 1; i >= 0; i--)
                stack.Push(root[i]);

            while (stack.Count > 0)
            {
                SavedItem item = stack.Pop();
                var test = item as ClashTest;
                if (test != null)
                {
                    yield return test;
                    continue;
                }
                var folder = item as GroupItem;
                if (folder == null) continue;
                var children = new List<SavedItem>();
                foreach (SavedItem c in folder.Children) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
        }

        internal static IEnumerable<ClashResult> Results(ClashTest test)
        {
            foreach (SavedItem child in test.Children)
            {
                var group = child as ClashResultGroup;
                if (group != null)
                {
                    foreach (SavedItem c in group.Children)
                        if (c is ClashResult) yield return (ClashResult)c;
                }
                else if (child is ClashResult)
                {
                    yield return (ClashResult)child;
                }
            }
        }

        internal static string PriorityText(ClashTest test)
        {
#if NAVIS2026 || NAVIS2027
            switch (test.Priority)
            {
                case 1: return "High";
                case 2: return "Medium";
                case 3: return "Low";
            }
#endif
            return "";
        }

        /// <summary>
        /// A person as text. Before Navisworks 2026 "assigned to", "approved by" and a
        /// comment's author are strings; from 2026 they are Assignee objects.
        /// </summary>
        internal static string Who(object person)
        {
            if (person == null) return "";
            var text = person as string;
            if (text != null) return text;
            System.Reflection.PropertyInfo name = person.GetType().GetProperty("DisplayName");
            return (name != null ? name.GetValue(person, null) as string : null) ?? person.ToString();
        }

        internal static string CommentsText(ClashResult r)
        {
            try
            {
                var parts = new List<string>();
                foreach (Comment c in r.Comments)
                {
                    if (!string.IsNullOrWhiteSpace(c.Body))
                        parts.Add((string.IsNullOrWhiteSpace(Who(c.Author)) ? "" : Who(c.Author) + ": ") + c.Body.Trim());
                }
                return string.Join(" / ", parts);
            }
            catch
            {
                return "";
            }
        }

        private static void AddImage(ClashImager imager, ClashResult r, ResultRow row)
        {
            try
            {
                using (Bitmap bitmap = imager.Image(r, 600, 450))
                {
                    if (bitmap == null) return;
                    row.Image = Jpeg(bitmap);
                    row.ImageWidth = bitmap.Width;
                    row.ImageHeight = bitmap.Height;
                }
            }
            catch (Exception ex)
            {
                Watchdog.Log("No image for " + r.DisplayName + ": " + ex.Message);
            }
        }

        internal static byte[] Jpeg(Bitmap bitmap)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var parameters = new EncoderParameters(1))
            using (var stream = new MemoryStream())
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                // Through a 24-bit copy: a JPEG cannot carry the alpha channel.
                using (var rgb = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(rgb))
                    {
                        g.Clear(System.Drawing.Color.White);
                        g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                    }
                    rgb.Save(stream, codec, parameters);
                }
                return stream.ToArray();
            }
        }

        // ------------------------------------------------------------- items

        internal static ItemInfo Describe(ModelItem item)
        {
            if (item == null)
                return ItemInfo.Empty;

            // Each detail on its own: one property that cannot be read leaves the others.
            return new ItemInfo
            {
                Name = Safe("name", () => item.DisplayName),
                Category = Safe("category", () => Property(item, p => p.FindPropertyByName(ElementCategoryInternal, CategoryPropertyInternal))
                                                   ?? Property(item, p => p.FindPropertyByDisplayName("Element", "Category"))
                                                   ?? CategoryNode(item)),
                Type = Safe("type", () => Property(item, p => p.FindPropertyByDisplayName("Element", "Type"))
                                          ?? Property(item, p => p.FindPropertyByDisplayName("Element", "Family"))),
                ElementId = Safe("element ID", () => Property(item, p => p.FindPropertyByDisplayName("Element ID", "Value"))
                                                    ?? Property(item, p => p.FindPropertyByDisplayName("Element", "Id"))),
                Level = Safe("level", () => Property(item, p => p.FindPropertyByDisplayName("Element", "Level"))
                                            ?? Property(item, p => p.FindPropertyByDisplayName("Element", "Reference Level"))
                                            ?? Property(item, p => p.FindPropertyByDisplayName("Element", "Base Constraint"))),
                File = Safe("file", () => SourceFile(item)),
                Layer = Safe("layer", () => Layer(item)),
                Path = Safe("path", () => TreePath(item))
            };
        }

        private static string Safe(string what, Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                Watchdog.Log("Item " + what + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>A property of the item or, failing that, of its nearest ancestors (the element above a geometry node).</summary>
        internal static string Property(ModelItem item, Func<PropertyCategoryCollection, DataProperty> find)
        {
            ModelItem current = item;
            for (int depth = 0; current != null && depth < 4; depth++, current = current.Parent)
            {
                DataProperty p = find(current.PropertyCategories);
                if (p != null && p.Value != null)
                {
                    string text = ValueText(p.Value);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// A property value as text, whatever its type. ToDisplayString only works on a
        /// text value - on a number (the Element ID is one) it throws.
        /// </summary>
        internal static string ValueText(VariantData v)
        {
            if (v.IsDisplayString) return CleanReference(v.ToDisplayString());
            if (v.IsIdentifierString) return v.ToIdentifierString();
            if (v.IsInt32) return v.ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.IsNamedConstant) return v.ToNamedConstant().DisplayName;
            if (v.IsBoolean) return v.ToBoolean() ? "Yes" : "No";
            if (v.IsAnyDouble) return v.ToAnyDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (v.IsDateTime) return v.ToDateTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (v.IsNone) return null;
            return v.ToString();
        }

        // Revit exports a reference to another element as its kind, name and id:
        // Level "L00 GROUND LEVEL", #2787 - Phase "New Construction", #86961.
        private static readonly System.Text.RegularExpressions.Regex Reference =
            new System.Text.RegularExpressions.Regex("^[A-Za-z][A-Za-z0-9_]* \"(.*)\", #-?\\d+$");

        /// <summary>A Revit element reference as just its name ("L00 GROUND LEVEL"); any other text as it is.</summary>
        internal static string CleanReference(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(", #", StringComparison.Ordinal) < 0) return text;
            var m = Reference.Match(text.Trim());
            return m.Success ? m.Groups[1].Value : text;
        }

        /// <summary>
        /// A clash date, or null when it was never set: Navisworks reports an unset
        /// date (an approval that never happened) as 1 January 1970.
        /// </summary>
        internal static DateTime? RealDate(DateTime? value)
        {
            return value.HasValue && value.Value.Year > 1971 ? value : (DateTime?)null;
        }

        internal static string CategoryNode(ModelItem item)
        {
            for (ModelItem current = item; current != null; current = current.Parent)
            {
                if (current.ClassDisplayName == "Category")
                    return current.DisplayName;
            }
            return null;
        }

        /// <summary>The nearest Layer (or Level) node above the item in the selection tree, else its Level property.</summary>
        internal static string Layer(ModelItem item)
        {
            for (ModelItem current = item.Parent; current != null; current = current.Parent)
            {
                if (current.ClassDisplayName == "Layer" || current.ClassDisplayName == "Level")
                    return current.DisplayName;
            }
            return null;
        }

        /// <summary>"file.nwc > Level 1 > Walls > Basic Wall > ...": the selection-tree path down to the item.</summary>
        internal static string TreePath(ModelItem item)
        {
            var names = new List<string>();
            for (ModelItem current = item; current != null && names.Count < 16; current = current.Parent)
            {
                if (!string.IsNullOrWhiteSpace(current.DisplayName))
                    names.Add(current.DisplayName);
            }
            names.Reverse();
            return string.Join(" > ", names);
        }

        internal static string SourceFile(ModelItem item)
        {
            for (ModelItem current = item; current != null; current = current.Parent)
            {
                Autodesk.Navisworks.Api.Model model = current.Model;
                if (model == null) continue;
                string path = !string.IsNullOrEmpty(model.FileName) ? model.FileName : model.SourceFileName;
                if (!string.IsNullOrEmpty(path))
                    return Path.GetFileName(path);
            }
            return null;
        }
    }
}
