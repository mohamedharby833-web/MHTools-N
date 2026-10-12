using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using Autodesk.Navisworks.Api;
using MHToolsN.Services;

namespace MHToolsN.UI
{
    /// <summary>
    /// The Quantities window: scans the model (TakeoffService.Plan), shows one row per
    /// discipline and Revit category with its types and elements, and takes the ticked
    /// rows off into Quantification (TakeoffService.Apply - one undo step). The second
    /// tab shows the quantities Quantification holds now. The AI Bridge's
    /// preview_takeoff / apply_takeoff do the same work.
    /// </summary>
    public partial class QuantitiesWindow : Window
    {
        private const string Caption = "Quantities";

        /// <summary>One row of the Take off tab: a discipline's Revit category.</summary>
        public sealed class CategoryRow : INotifyPropertyChanged
        {
            private bool _include = true;
            public List<TakeoffLine> Lines = new List<TakeoffLine>();
            public string DisciplineName { get; set; }
            public string Category { get; set; }
            public int Types { get { return Lines.Count; } }
            public int Elements { get { return Lines.Sum(l => l.ToAdd.Count + l.AlreadyTakenOff); } }
            public int ToAdd { get { return Lines.Sum(l => l.ToAdd.Count); } }
            public int Already { get { return Lines.Sum(l => l.AlreadyTakenOff); } }

            public bool Include
            {
                get { return _include; }
                set { if (_include == value) return; _include = value; Raise("Include"); }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void Raise(string name)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new PropertyChangedEventArgs(name));
            }
        }

        /// <summary>One row of the Quantities tab.</summary>
        public sealed class QuantityRow
        {
            public string Group { get; set; }
            public string Item { get; set; }
            public long Objects { get; set; }
            public string Length { get; set; }
            public string Area { get; set; }
            public string Volume { get; set; }
            public string Count { get; set; }
        }

        private readonly Document _document;
        private TakeoffPlan _plan;
        private List<CategoryRow> _rows = new List<CategoryRow>();

        public QuantitiesWindow(Document document)
        {
            MhTheme.Apply(this);
            InitializeComponent();
            _document = document;
            CheckReady();
            RefreshQuantities();
            UpdateButtons();
        }

        private bool CheckReady()
        {
            string why = null;
            bool ready = _document != null && !_document.IsClear && TakeoffService.IsReady(_document, out why);
            if (_document == null || _document.IsClear) why = "Open the coordination model first.";
            NotReadyPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            NotReadyText.Text = why ?? "";
            ScanButton.IsEnabled = _document != null && !_document.IsClear;
            return ready;
        }

        // ---------------------------------------------------------------- scan

        private void OnScan(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                using (var progress = new NavisProgress("Reading the model for Quantification"))
                    _plan = TakeoffService.Plan(_document, null, null, null, progress);
                _rows = _plan.Lines
                    .GroupBy(l => l.Discipline + "|" + l.Category)
                    .Select(g => new CategoryRow { DisciplineName = g.First().DisciplineName, Category = g.First().Category, Lines = g.ToList() })
                    .OrderBy(r => r.DisciplineName).ThenBy(r => r.Category)
                    .ToList();
                foreach (CategoryRow row in _rows) row.Include = row.ToAdd > 0;
                CategoriesGrid.ItemsSource = _rows;
                ScanText.Text = _rows.Count + " categories, " + _plan.Lines.Count + " types, " + _plan.Elements + " elements (" +
                                _plan.ToAdd + " to take off, " + (_plan.Elements - _plan.ToAdd) + " already)." +
                                (_plan.Warnings.Count > 0 ? "  " + string.Join("  ", _plan.Warnings.Take(2)) : "");
                Tabs.SelectedIndex = 0;
            }
            catch (OperationCanceledException)
            {
                Footer.Status = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Quantities: scan", ex);
                MessageBox.Show(this, ex.Message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = null;
                UpdateButtons();
            }
        }

        private void OnAll(object sender, RoutedEventArgs e)
        {
            bool value = AllBox.IsChecked == true;
            foreach (CategoryRow row in _rows) row.Include = value;
            UpdateButtons();
        }

        private void OnRowTicked(object sender, RoutedEventArgs e)
        {
            UpdateButtons();
        }

        // ------------------------------------------------------------ take off

        private void OnTakeOff(object sender, RoutedEventArgs e)
        {
            if (_plan == null || !CheckReady()) return;
            var chosen = new TakeoffPlan();
            foreach (CategoryRow row in _rows.Where(r => r.Include)) chosen.Lines.AddRange(row.Lines);
            int add = chosen.ToAdd;
            if (add == 0)
            {
                MessageBox.Show(this, "Nothing to take off: the ticked categories are already taken off.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int types = chosen.Lines.Count(l => l.ToAdd.Count > 0);
            if (MessageBox.Show(this,
                    "Take off " + add + " element(s) onto " + types + " item(s), one per Revit type, under item groups per discipline and category?\n\n" +
                    "Existing groups and items of the same name are reused; elements already taken off are left as they are. One Undo (Ctrl+Z) reverts it.",
                    Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                TakeoffResult result;
                using (var progress = new NavisProgress("Quantification takeoff"))
                    result = TakeoffService.Apply(_document, chosen, progress);
                Cursor = null;

                MessageBox.Show(this,
                    "Item groups created: " + result.GroupsCreated + "\nItems created: " + result.ItemsCreated + "\nElements taken off: " + result.ObjectsAdded +
                    (result.Failed > 0 ? "\nFailed: " + result.Failed + "\n\n" + string.Join("\n", result.Errors) : "") +
                    "\n\nOne Undo (Ctrl+Z) reverts this. Open Quantification to see the items.",
                    Caption, MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                OnScan(sender, e);
                RefreshQuantities();
                Tabs.SelectedIndex = 1;
            }
            catch (Exception ex)
            {
                Cursor = null;
                Watchdog.LogException("Quantities: take off", ex);
                MessageBox.Show(this, "The takeoff failed and was not kept.\n\n" + ex.Message + "\n\nLog: " + Watchdog.LogPath,
                    Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --------------------------------------------------------- quantities

        private void RefreshQuantities()
        {
            try
            {
                if (_document == null || _document.IsClear || !_document.GetTakeoffReady()) { QuantitiesGrid.ItemsSource = null; return; }
                QuantitiesGrid.ItemsSource = TakeoffService.Summary(_document, 5000).Select(r => new QuantityRow
                {
                    Group = (string)r["group"],
                    Item = (string)r["item"],
                    Objects = Convert.ToInt64(r["objects"]),
                    Length = Number(r, "Length"),
                    Area = Number(r, "Area"),
                    Volume = Number(r, "Volume"),
                    Count = Number(r, "Count")
                }).ToList();
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Quantities: reading the quantities", ex);
            }
        }

        private static string Number(Dictionary<string, object> row, string key)
        {
            object v;
            if (!row.TryGetValue(key, out v) || v == null) return "";
            double d = Convert.ToDouble(v);
            return d == 0 ? "" : d.ToString("#,0.###", CultureInfo.InvariantCulture);
        }

        private void UpdateButtons()
        {
            bool ready = _document != null && !_document.IsClear && _document.GetTakeoffReady();
            TakeOffButton.IsEnabled = ready && _plan != null && _rows.Any(r => r.Include && r.ToAdd > 0);
            int add = _rows.Where(r => r.Include).Sum(r => r.ToAdd);
            Footer.Status = _plan == null ? "Scan the model first." : add + " element(s) ticked to take off.";
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    internal static class TakeoffReadyExtensions
    {
        public static bool GetTakeoffReady(this Document doc)
        {
            string why;
            try { return TakeoffService.IsReady(doc, out why); }
            catch (Exception) { return false; }
        }
    }
}
