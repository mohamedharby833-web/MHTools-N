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
            public string Width { get; set; }
            public string Thickness { get; set; }
            public string Height { get; set; }
            public string Perimeter { get; set; }
            public string Area { get; set; }
            public string Volume { get; set; }
            public string Weight { get; set; }
            public string Count { get; set; }
        }

        private readonly Document _document;
        private TakeoffPlan _plan;
        private List<CategoryRow> _rows = new List<CategoryRow>();
        private DuctSheet _sheet = DuctSheet.Load();

        public QuantitiesWindow(Document document)
        {
            MhTheme.Apply(this);
            InitializeComponent();
            _document = document;
            ShowSheet();
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
                CategoriesGrid.ItemsSource = _rows;
                FilterCategories();
                ScanText.Text = _rows.Count + " categories, " + _plan.Lines.Count + " items, " + _plan.Elements + " elements (" +
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
            // The rows the search shows.
            foreach (CategoryRow row in CategoriesGrid.Items.OfType<CategoryRow>().ToList()) row.Include = value;
            UpdateButtons();
        }

        // A row's tick box. With several rows selected, and this one among them, all of
        // them follow it (as in the Clash Matrix window).

        /// <summary>The rows selected when a tick box was pressed - before the click can change the selection.</summary>
        private List<CategoryRow> _selectionAtClick;
        private CategoryRow _rowAtClick;
        private bool _rowWasTicked;

        private void OnGridPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _selectionAtClick = CategoriesGrid.SelectedItems.OfType<CategoryRow>().ToList();
            _rowAtClick = null;
            System.Windows.Controls.CheckBox box = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && !(d is System.Windows.Controls.DataGridRow); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is System.Windows.Controls.CheckBox) box = (System.Windows.Controls.CheckBox)d;
            if (box != null && box.DataContext is CategoryRow)
            {
                _rowAtClick = (CategoryRow)box.DataContext;
                _rowWasTicked = box.IsChecked == true;
            }
        }

        private void OnRowTicked(object sender, RoutedEventArgs e)
        {
            var box = (System.Windows.Controls.CheckBox)sender;
            List<CategoryRow> chosen = _selectionAtClick;
            CategoryRow row = _rowAtClick ?? box.DataContext as CategoryRow;
            bool value = _rowAtClick != null ? !_rowWasTicked : box.IsChecked == true;
            _selectionAtClick = null;
            _rowAtClick = null;
            if (chosen != null && chosen.Count > 1 && row != null && chosen.Contains(row))
            {
                foreach (CategoryRow r in chosen) r.Include = value;
                // Keep them selected, to tick again.
                CategoriesGrid.SelectedItems.Clear();
                foreach (CategoryRow r in chosen)
                    if (CategoriesGrid.Items.Contains(r)) CategoriesGrid.SelectedItems.Add(r);
            }
            UpdateButtons();
        }

        // ------------------------------------------------------------ take off

        private void OnTakeOff(object sender, RoutedEventArgs e)
        {
            RunTakeoff(false);
        }

        private void OnUpdate(object sender, RoutedEventArgs e)
        {
            RunTakeoff(true);
        }

        /// <param name="replace">Update Take Off: elements already taken off are taken off again (TakeoffService.Apply).</param>
        private void RunTakeoff(bool replace)
        {
            if (_plan == null || !CheckReady()) return;
            var chosen = new TakeoffPlan();
            foreach (CategoryRow row in _rows.Where(r => r.Include)) chosen.Lines.AddRange(row.Lines);
            int add = chosen.ToAdd, again = replace ? chosen.Lines.Sum(l => l.Already.Count) : 0;
            if (add + again == 0)
            {
                MessageBox.Show(this, replace ? "Nothing ticked to take off." : "Nothing to take off: the ticked categories are already taken off. Update Take Off takes them off again.",
                    Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DuctSheet sheet;
            if (!ReadSheet(out sheet)) return;
            var lines = chosen.Lines.Where(l => l.ToAdd.Count > 0 || (replace && l.Already.Count > 0)).ToList();
            int sized = lines.Count(l => l.Size != null);
            string what = (replace
                    ? "Update the takeoff of " + (add + again) + " element(s) on " + lines.Count + " item(s)?\n\n" +
                      again + " element(s) already taken off are removed from their items and taken off again" + (add > 0 ? ", " + add + " new one(s) added" : "") +
                      "; items and groups left empty are removed."
                    : "Take off " + add + " element(s) onto " + lines.Count + " item(s), under item groups per discipline and category?") + "\n\n" +
                "An item per Revit type" + (sized > 0 ? "; ducts, pipes, their fittings, accessories and insulation an item per size under their type (" + sized + " sizes), " +
                "ducts with sheet thickness and weight by " + sheet.Standard + " (" + sheet.StepsText + "), " + DuctSheet.Num(sheet.Density) + " kg/m³, +" +
                DuctSheet.Num(sheet.AllowancePercent) + " %" : "") + ".\n\n" +
                (replace ? "" : "Existing groups and items of the same name are reused; elements already taken off are left as they are. ") +
                "One Undo (Ctrl+Z) reverts it.";
            if (MessageBox.Show(this, what, Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                TakeoffResult result;
                using (var progress = new NavisProgress("Quantification takeoff"))
                    result = TakeoffService.Apply(_document, chosen, progress, sheet, replace);
                Cursor = null;

                MessageBox.Show(this,
                    (replace ? "Removed first: " + result.ObjectsRemoved + " element(s), " + result.ItemsRemoved + " empty item(s), " + result.GroupsRemoved + " empty group(s)\n\n" : "") +
                    "Item groups created: " + result.GroupsCreated + "\nItems created: " + result.ItemsCreated + "\nElements taken off: " + result.ObjectsAdded +
                    (result.Failed > 0 ? "\nFailed: " + result.Failed : "") +
                    (result.Errors.Count > 0 ? "\n\n" + string.Join("\n", result.Errors) : "") +
                    "\n\nOne Undo (Ctrl+Z) reverts this. Open Quantification to see the items.",
                    Caption, MessageBoxButton.OK, result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                OnScan(this, null);
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
                    Width = Number(r, "Width"),
                    Thickness = Number(r, "Thickness", "#,0.#####"),
                    Height = Number(r, "Height"),
                    Perimeter = Number(r, "Perimeter"),
                    Area = Number(r, "Area"),
                    Volume = Number(r, "Volume"),
                    Weight = Number(r, "Weight"),
                    Count = Number(r, "Count")
                }).ToList();
                FilterQuantities();
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Quantities: reading the quantities", ex);
            }
        }

        private static string Number(Dictionary<string, object> row, string key, string format = "#,0.###")
        {
            object v;
            if (!row.TryGetValue(key, out v) || v == null) return "";
            double d = Convert.ToDouble(v);
            return d == 0 ? "" : d.ToString(format, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------- duct sheet

        private void ShowSheet()
        {
            StandardBox.Text = _sheet.Standard;
            StepsBox.Text = _sheet.StepsText;
            DensityBox.Text = DuctSheet.Num(_sheet.Density);
            AllowanceBox.Text = DuctSheet.Num(_sheet.AllowancePercent);
        }

        private void OnSheetDefaults(object sender, RoutedEventArgs e)
        {
            _sheet = new DuctSheet();
            ShowSheet();
        }

        /// <summary>The duct sheet as typed, remembered for next time; false (with a message) when it does not read.</summary>
        private bool ReadSheet(out DuctSheet sheet)
        {
            sheet = null;
            List<KeyValuePair<double, double>> steps;
            string error;
            double density, allowance;
            if (!DuctSheet.TryParseSteps(StepsBox.Text, out steps, out error))
            {
                MessageBox.Show(this, "Duct sheet thickness: " + error, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                StepsBox.Focus();
                return false;
            }
            if (!DuctSheet.TryNumber(DensityBox.Text, out density) || density <= 0)
            {
                MessageBox.Show(this, "Duct sheet density: give kg/m³, e.g. 7850.", Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                DensityBox.Focus();
                return false;
            }
            if (!DuctSheet.TryNumber(AllowanceBox.Text, out allowance) || allowance < 0)
            {
                MessageBox.Show(this, "Duct sheet allowance: give a percent, e.g. 0 or 10.", Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                AllowanceBox.Focus();
                return false;
            }
            sheet = new DuctSheet
            {
                Standard = string.IsNullOrWhiteSpace(StandardBox.Text) ? DuctSheet.DefaultStandard : StandardBox.Text.Trim(),
                Density = density,
                AllowancePercent = allowance
            };
            sheet.SetSteps(steps);
            sheet.Save();
            _sheet = sheet;
            return true;
        }

        // -------------------------------------------------------------- export

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (_document == null || _document.IsClear || !_document.GetTakeoffReady()) return;
            DuctSheet sheet;
            if (!ReadSheet(out sheet)) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export quantities",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = SafeFileName(System.IO.Path.GetFileNameWithoutExtension(_document.FileName)) + " - Quantities.xlsx"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                QuantitiesExporter.WriteExcel(dialog.FileName, TakeoffService.Summary(_document, int.MaxValue), sheet);
                Cursor = null;
                Footer.Status = "Saved " + dialog.FileName;
                if (MessageBox.Show(this, "Saved:\n" + dialog.FileName + "\n\nOpen it now?", Caption, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Cursor = null;
                Watchdog.LogException("Quantities: export", ex);
                MessageBox.Show(this, "The export failed.\n\n" + ex.Message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Model";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private void UpdateButtons()
        {
            bool ready = _document != null && !_document.IsClear && _document.GetTakeoffReady();
            TakeOffButton.IsEnabled = ready && _plan != null && _rows.Any(r => r.Include && r.ToAdd > 0);
            UpdateButton.IsEnabled = ready && _plan != null && _rows.Any(r => r.Include && r.Elements > 0);
            ExportButton.IsEnabled = ready;
            var ticked = _rows.Where(r => r.Include).ToList();
            int add = ticked.Sum(r => r.ToAdd), already = ticked.Sum(r => r.Already);
            if (_plan == null) Footer.Status = "Scan the model first.";
            else if (ticked.Count == 0) Footer.Status = "Nothing ticked.";
            else if (add == 0) Footer.Status = "All " + already.ToString("#,0") + " ticked element(s) are already taken off - Update Take Off takes them off again (new sizes, sheet rule, model changes).";
            else Footer.Status = add.ToString("#,0") + " element(s) to take off" + (already > 0 ? " (" + already.ToString("#,0") + " already)." : ".");

            AllBox.IsChecked = _rows.Count > 0 && _rows.All(r => r.Include) ? true : _rows.Any(r => r.Include) ? (bool?)null : false;
        }

        // -------------------------------------------------------------- search

        private void OnCategoriesSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            CategoriesSearchHint.Visibility = CategoriesSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterCategories();
        }

        private void OnQuantitiesSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            QuantitiesSearchHint.Visibility = QuantitiesSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterQuantities();
        }

        private void FilterCategories()
        {
            if (CategoriesGrid.ItemsSource == null) return;
            string text = CategoriesSearch.Text.Trim();
            System.Windows.Data.CollectionViewSource.GetDefaultView(CategoriesGrid.ItemsSource).Filter = text.Length == 0 ? (Predicate<object>)null
                : o => Contains(text, ((CategoryRow)o).DisciplineName, ((CategoryRow)o).Category);
        }

        private void FilterQuantities()
        {
            if (QuantitiesGrid.ItemsSource == null) return;
            string text = QuantitiesSearch.Text.Trim();
            System.Windows.Data.CollectionViewSource.GetDefaultView(QuantitiesGrid.ItemsSource).Filter = text.Length == 0 ? (Predicate<object>)null
                : o => Contains(text, ((QuantityRow)o).Group, ((QuantityRow)o).Item);
        }

        private static bool Contains(string text, params string[] fields)
        {
            return fields.Any(f => f != null && f.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
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
