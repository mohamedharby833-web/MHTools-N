using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Navisworks.Api;
using MHToolsN.Model;
using MHToolsN.Services;
using Microsoft.Win32;

namespace MHToolsN.UI
{
    /// <summary>
    /// The Clash Matrix window: a Search Sets tab (the files and their disciplines, the
    /// planned sets - tick, rename, edit, create) and a Clash Tests tab (the planned
    /// tests - tick, rename, tolerance, priority). Nothing in the model changes until
    /// Create Search Sets or Create Clash Tests, and both ask first.
    /// </summary>
    public partial class ClashMatrixWindow : Window
    {
        private const string Caption = "Clash Matrix";
        private const int MaxLinesInMessage = 25;

        private readonly Document _document;
        private readonly bool _hasModel;

        private MatrixParseResult _result;

        /// <summary>True when the matrix was built from the model scan (not imported from Excel).</summary>
        private bool _resultFromScan;

        /// <summary>The appended files and their disciplines, as last scanned.</summary>
        private List<ModelInfo> _models = new List<ModelInfo>();

        /// <summary>True once a discipline was changed after the scan-built matrix was made.</summary>
        private bool _disciplinesChanged;

        private List<SetRow> _setRows = new List<SetRow>();
        private List<TestRow> _testRows = new List<TestRow>();
        private bool _loading = true;

        /// <summary>True while a button ticks many rows: the tables are refreshed once, at the end.</summary>
        private bool _bulk;

        public ClashMatrixWindow(Document document)
        {
            // The MH Tools theme, before InitializeComponent so the window's own
            // styles are read with it already there (see MhTheme.Apply).
            MhTheme.Apply(this);
            InitializeComponent();
            _document = document;

            try
            {
                _hasModel = document != null && !document.IsClear && document.Models.Count > 0;
            }
            catch
            {
                _hasModel = false;
            }

            if (!_hasModel)
            {
                ScanButton.IsEnabled = false;
                CountButton.IsEnabled = false;
                CreateSetsButton.IsEnabled = false;
                ModelsPanel.Visibility = Visibility.Collapsed;
                Footer.Status = "No model open - Import Matrix, Export and Generate XML still work.";
            }
            else
            {
                try { RefreshModels(new List<string>(), null); }
                catch (Exception ex) { Watchdog.LogException("Scan models", ex); }

                int sets = 0;
                try { sets = NavisClashCreator.GetManagedSets(document).Count; }
                catch (Exception ex) { Watchdog.LogException("Read managed sets", ex); }

                Footer.Status = sets > 0
                    ? string.Format("{0} search sets already in the '{1}' folder. Scan Model or Import a matrix.",
                        sets, NavisClashCreator.FolderName)
                    : "Scan Model to list the sets from the model's categories, or Import a matrix.";
            }
            UpdateButtons();
            _loading = false;
        }

        private bool BestPractice
        {
            get { return BestPracticeBox.IsChecked == true; }
        }

        // ================================================================ Search Sets tab

        // Lists one set per discipline and category, and the default tests. The model
        // does not change.
        private void ScanModel_Click(object sender, RoutedEventArgs e)
        {
            double tol;
            if (!RequireModel() || !TryReadTolerance(out tol) || !ConfirmDiscardEdits())
                return;

            Run("Scan Model", () =>
            {
                var warnings = new List<string>();
                Watchdog.Clear();
                Watchdog.Log("=== Scan Model === " + (BestPractice ? "best practice" : "all categories"));

                List<ModelInfo> models;
                using (var progress = new NavisProgress("Scanning model categories"))
                    models = RefreshModels(warnings, progress);

                if (NavisClashCreator.AllCategories(models).Count == 0)
                {
                    ShowMessage("No Revit categories were found in the open model.", warnings, MessageBoxImage.Warning);
                    return;
                }

                MatrixParseResult matrix = NavisClashCreator.BuildFullMatrix(models, tol, BestPractice);
                matrix.Warnings.InsertRange(0, warnings);
                _disciplinesChanged = false;
                SetResult(matrix, true, "(built from the model - not exported yet)");
                Tabs.SelectedIndex = 0;
                Footer.Status = string.Format("{0} sets and {1} tests listed. Nothing is created until you click Create.",
                    matrix.Categories.Count, matrix.Tests.Count);
            });
        }

        // Best practice / All categories.
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _result == null || !_hasModel)
                return;

            double tol;
            if (!TryReadTolerance(out tol))
                return;

            if (_resultFromScan)
            {
                if (!ConfirmDiscardEdits())
                {
                    _loading = true;
                    (_result.BestPractice ? BestPracticeBox : AllCategoriesBox).IsChecked = true;
                    _loading = false;
                    return;
                }
                Run("Rebuild", () =>
                {
                    MatrixParseResult matrix = NavisClashCreator.BuildFullMatrix(_models, tol, BestPractice);
                    SetResult(matrix, true, "(built from the model - not exported yet)");
                    Footer.Status = (BestPractice ? "Best practice" : "All categories") + ": " +
                                    matrix.Categories.Count + " sets, " + matrix.Tests.Count + " tests.";
                });
            }
            else
            {
                _result.BestPractice = BestPractice;
                NavisClashCreator.AssignModelScopes(_result, _models);
                RefreshRows();
            }
        }

        private void CountItems_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireModel() || !RequireResult())
                return;
            Run("Count Items", () =>
            {
                Dictionary<string, int> counts;
                using (var progress = new NavisProgress("Counting set items"))
                    counts = NavisClashCreator.CountItems(_document, _result.Categories, progress);
                foreach (SetRow row in _setRows)
                {
                    int n;
                    row.Items = counts.TryGetValue(row.Name, out n) ? n.ToString("N0") : "";
                }
                int empty = counts.Count(kv => kv.Value == 0);
                Footer.Status = counts.Count + " sets counted, " + counts.Values.Sum().ToString("N0") + " items" +
                                (empty > 0 ? ", " + empty + " empty" : "") + ".";
            });
        }

        // Creates / updates the ticked sets and deletes the unticked ones that exist.
        private void CreateSets_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireModel() || !RequireResult())
                return;

            Run("Create Search Sets", () =>
            {
                List<ModelInfo> models;
                using (var progress = new NavisProgress("Scanning model categories"))
                    models = RefreshModels(new List<string>(), progress);
                RebuildIfDisciplinesChanged(models);

                Dictionary<string, SelectionSet> existing = NavisClashCreator.GetManagedSets(_document);
                MatrixParseResult effective = _result.Effective();
                int create = effective.Categories.Count(c => !existing.ContainsKey(c));
                int update = effective.Categories.Count(c => existing.ContainsKey(c));
                List<string> delete = _result.ExcludedSets.Where(existing.ContainsKey).OrderBy(s => s).ToList();

                var question = new StringBuilder();
                question.AppendFormat("In the '{0}' folder:\n  create {1} search set(s)\n  update {2} existing set(s) in place (tests stay linked)\n",
                    NavisClashCreator.FolderName, create, update);
                if (delete.Count > 0)
                    question.AppendFormat("  delete {0} unticked set(s) - unless a clash test still uses them:\n    {1}\n",
                        delete.Count, string.Join("\n    ", Trim(delete, 10)));
                question.Append("\nContinue?");
                if (MessageBox.Show(this, question.ToString(), Caption, MessageBoxButton.OKCancel,
                        MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                    return;

                var warnings = new List<string>();
                SetSyncResult sets;
                using (var progress = new NavisProgress("Creating search sets"))
                    sets = NavisClashCreator.EnsureSearchSets(_document, effective, true, warnings, progress);
                var inUse = new List<string>();
                int removed = delete.Count > 0 ? NavisClashCreator.RemoveSets(_document, delete, inUse) : 0;
                if (inUse.Count > 0)
                    warnings.Add("Kept - a clash test still uses them (remove those tests first, or untick them and Create Clash Tests): " +
                                 string.Join(", ", inUse));

                RefreshStatuses();
                ShowMessage(string.Format("Search sets created: {0}, updated: {1}, deleted: {2}\n\nOne Undo (Ctrl+Z) reverts each step.",
                    sets.Created, sets.Updated, removed), warnings, warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                Footer.Status = string.Format("Sets created {0}, updated {1}, deleted {2}.", sets.Created, sets.Updated, removed);
            });
        }

        // ---------------------------------------------------------------- tick boxes

        /// <summary>The rows selected when a row's tick box was pressed - before the click can change the selection.</summary>
        private List<object> _selectionAtClick;

        /// <summary>
        /// The row whose tick box was pressed, and whether it was ticked - taken on the press,
        /// as the click itself can rebuild the table under the tick box.
        /// </summary>
        private object _rowAtClick;
        private bool _rowWasTicked;

        private void Grid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _selectionAtClick = ((DataGrid)sender).SelectedItems.Cast<object>().ToList();
            _rowAtClick = null;
            CheckBox box = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && !(d is DataGridRow); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is CheckBox) box = (CheckBox)d;
            if (box != null && (box.DataContext is SetRow || box.DataContext is TestRow))
            {
                _rowAtClick = box.DataContext;
                _rowWasTicked = box.IsChecked == true;
            }
        }

        // The header tick box: every row the search lets through.
        private void SetsAllBox_Click(object sender, RoutedEventArgs e)
        {
            bool value = SetsAllBox.IsChecked == true;
            Bulk(true, () => { foreach (SetRow r in SetsGrid.Items.OfType<SetRow>().ToList()) r.Include = value; });
        }

        private void TestsAllBox_Click(object sender, RoutedEventArgs e)
        {
            bool value = TestsAllBox.IsChecked == true;
            Bulk(false, () => { foreach (TestRow r in TestsGrid.Items.OfType<TestRow>().ToList()) r.Include = value; });
        }

        // A row's tick box. With several rows selected, and this one among them, all of them follow it.
        private void SetBox_Click(object sender, RoutedEventArgs e)
        {
            RowBoxClicked(SetsGrid, (CheckBox)sender, true);
        }

        private void TestBox_Click(object sender, RoutedEventArgs e)
        {
            RowBoxClicked(TestsGrid, (CheckBox)sender, false);
        }

        private void RowBoxClicked(DataGrid grid, CheckBox box, bool sets)
        {
            List<object> chosen = _selectionAtClick;
            object row = _rowAtClick ?? box.DataContext;
            bool value = _rowAtClick != null ? !_rowWasTicked : box.IsChecked == true;
            _selectionAtClick = null;
            _rowAtClick = null;
            if (chosen == null || chosen.Count < 2 || !chosen.Contains(row))
            {
                UpdateHeaderBoxes();
                return;
            }

            Bulk(sets, () =>
            {
                foreach (object o in chosen)
                {
                    if (sets) ((SetRow)o).Include = value;
                    else ((TestRow)o).Include = value;
                }
            });

            // Keep the rows selected, so they can be ticked again. (Ticking sets rebuilds the
            // tests table, never this one.)
            grid.SelectedItems.Clear();
            foreach (object o in chosen)
                if (grid.Items.Contains(o)) grid.SelectedItems.Add(o);
        }

        /// <summary>Each header tick box: ticked when every row shown is, empty when none is, a dash in between.</summary>
        private void UpdateHeaderBoxes()
        {
            HeaderBox(SetsAllBox, SetsGrid == null ? null : SetsGrid.Items.OfType<SetRow>().Select(r => r.Include));
            HeaderBox(TestsAllBox, TestsGrid == null ? null : TestsGrid.Items.OfType<TestRow>().Where(r => r.SetsIncluded).Select(r => r.Include));
        }

        private static void HeaderBox(CheckBox box, IEnumerable<bool> ticks)
        {
            if (box == null) return;
            List<bool> all = ticks == null ? new List<bool>() : ticks.ToList();
            box.IsEnabled = all.Count > 0;
            box.IsChecked = all.Count > 0 && all.All(t => t) ? true : all.Any(t => t) ? (bool?)null : false;
        }

        // A set was ticked, renamed or edited.
        private void SetEdited(RowBase row)
        {
            if (_bulk) return;
            // A rename changes the tests' names; ticking a set changes which tests can be made.
            MatrixLayout.ApplyTestNames(_result);
            BuildTestRows();
            RefreshStatuses();
            UpdateSummary();
        }

        // A discipline was picked in the Models table (the choice itself is saved by ModelInfo).
        private void Discipline_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count == 0 || Footer == null)
                return; // the table filling in, not a change

            _disciplinesChanged = true;
            Footer.Status = _resultFromScan
                ? "Discipline changed. Scan Model (or Create) rebuilds the sets by discipline."
                : "Discipline changed. Create limits each set to the models of its discipline.";
        }

        // ================================================================ Clash Tests tab

        private void Renumber_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireResult()) return;
            MatrixLayout.ApplyTestNames(_result);
            BuildTestRows();
            RefreshStatuses();
        }

        private void TestEdited(RowBase row)
        {
            if (_bulk) return;
            RefreshStatuses();
            UpdateSummary();
        }

        private void Tolerance_Changed(object sender, TextChangedEventArgs e)
        {
            if (ToleranceText == null || Footer == null)
                return;

            double tol;
            if (UnitConverter.TryParseMm(ToleranceText.Text, out tol))
            {
                ToleranceText.ClearValue(Control.BorderBrushProperty);
                foreach (TestRow r in _testRows) r.Refresh();
            }
            else
            {
                ToleranceText.BorderBrush = MhTheme.Brush(MhTheme.Danger);
            }
        }

        // Creates the ticked sets (if missing) and adds / updates / renames the ticked
        // tests (linked to the add-in's sets), after the user has confirmed.
        // ---------------------------------------------------------------- one step at a time

        // The footer's main button: "Next" on Search Sets, "Create Clash Tests" on Clash Tests.
        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedIndex == 0)
                Tabs.SelectedIndex = 1;
            else
                CreateTests_Click(sender, e);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            Tabs.SelectedIndex = 0;
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged also bubbles up from the tables and drop-downs inside the tabs.
            if (!ReferenceEquals(e.OriginalSource, Tabs))
                return;
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (CreateTestsButton == null || Tabs == null)
                return;
            bool testsTab = Tabs.SelectedIndex == 1;
            bool hasTests = _result != null && _result.Tests.Count > 0;
            BackButton.Visibility = testsTab ? Visibility.Visible : Visibility.Collapsed;
            CreateTestsButton.Content = testsTab ? "Create Clash Tests" : "Next: Clash Tests  →";
            CreateTestsButton.ToolTip = testsTab
                ? "Create the ticked sets and the ticked tests between them. Only tests linked to the " + NavisClashCreator.FolderName + " sets are changed. You confirm first."
                : "Review and edit the tests.";
            CreateTestsButton.IsEnabled = testsTab ? _hasModel && hasTests : _result != null;
        }

        // ---------------------------------------------------------------- search

        private void SetsSearch_Changed(object sender, TextChangedEventArgs e)
        {
            SetsSearchHint.Visibility = SetsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilters(true, false);
        }

        private void TestsSearch_Changed(object sender, TextChangedEventArgs e)
        {
            TestsSearchHint.Visibility = TestsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilters(false, true);
        }

        /// <summary>
        /// Shows only the rows where one column holds the text typed in the tab's search
        /// box, as typed (any case): "AR WALLS" finds "AR WALLS - ST FLOORS" but not
        /// "AR CURTAIN WALLS".
        /// </summary>
        /// <remarks>
        /// Only the table asked for: setting a filter rebuilds that table's rows, which loses
        /// its selection and the row under a tick box being clicked.
        /// </remarks>
        private void ApplyFilters(bool setsTable, bool testsTable)
        {
            if (setsTable && _setRows != null && SetsSearch != null)
            {
                ICollectionView sets = CollectionViewSource.GetDefaultView(_setRows);
                string setText = SetsSearch.Text.Trim();
                sets.Filter = setText.Length == 0 ? (Predicate<object>)null
                    : o => { var r = (SetRow)o; return Matches(setText, r.Name, r.Discipline, r.Categories, r.FilesTip, r.Status); };
            }
            if (testsTable && _testRows != null && TestsSearch != null)
            {
                ICollectionView tests = CollectionViewSource.GetDefaultView(_testRows);
                string testText = TestsSearch.Text.Trim();
                tests.Filter = testText.Length == 0 ? (Predicate<object>)null
                    : o => { var r = (TestRow)o; return Matches(testText, r.Name, r.LeftName, r.RightName, r.Priority, r.Status); };
            }
            UpdateHeaderBoxes();
        }

        private static bool Matches(string text, params string[] fields)
        {
            return fields.Any(f => f != null && f.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void CreateTests_Click(object sender, RoutedEventArgs e)
        {
            double tol;
            if (!RequireModel() || !RequireResult() || !TryReadTolerance(out tol))
                return;

            Run("Create Clash Tests", () =>
            {
                Watchdog.Log("=== Create Clash Tests === tolerance " + tol + " mm");
                var scanWarnings = new List<string>();
                List<ModelInfo> models;
                using (var progress = new NavisProgress("Scanning model categories"))
                    models = RefreshModels(scanWarnings, progress);
                RebuildIfDisciplinesChanged(models);

                MatrixParseResult effective = _result.Effective();
                TestPlan plan = NavisClashCreator.PlanTests(_document, effective.Tests, tol);

                var question = new StringBuilder();
                question.Append("Clash Detective:\n");
                question.AppendFormat("  add {0} test(s)\n  update {1} test(s)\n  rename {2} test(s), keeping their results\n" +
                    "  keep {3} unchanged test(s) and their results\n",
                    plan.ToAdd, plan.ToUpdate, plan.ToRename, plan.Unchanged);
                if (plan.Conflicts.Count > 0)
                    question.AppendFormat("  skip {0} test(s) whose name is already used by a test not linked to the '{1}' sets\n",
                        plan.Conflicts.Count, NavisClashCreator.FolderName);
                if (plan.OtherTests > 0)
                    question.AppendFormat("\n{0} other clash test(s) are not touched.\n", plan.OtherTests);

                bool removeStale = false;
                if (plan.Stale.Count > 0)
                {
                    question.AppendFormat("\n{0} Clash Matrix test(s) are not in the ticked tests:\n  {1}\n\nRemove them " +
                        "(and any Clash Matrix search set nothing uses any more)?\n" +
                        "Yes = remove, No = keep them, Cancel = do nothing.",
                        plan.Stale.Count, string.Join("\n  ", Trim(plan.Stale, 10)));
                    MessageBoxResult answer = MessageBox.Show(this, question.ToString(), Caption,
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                    if (answer == MessageBoxResult.Cancel)
                        return;
                    removeStale = answer == MessageBoxResult.Yes;
                }
                else
                {
                    question.Append("\nContinue?");
                    if (MessageBox.Show(this, question.ToString(), Caption, MessageBoxButton.OKCancel,
                            MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                        return;
                }

                var warnings = new List<string>(_result.Warnings);
                warnings.AddRange(scanWarnings);
                AddNotInModelWarning(effective, NavisClashCreator.AllCategories(models), warnings);

                SetSyncResult sets;
                TestApplyResult tests;
                using (var progress = new NavisProgress("Creating search sets"))
                    sets = NavisClashCreator.EnsureSearchSets(_document, effective, true, warnings, progress);
                using (var progress = new NavisProgress("Creating clash tests"))
                    tests = NavisClashCreator.ApplyTests(_document, effective, tol, removeStale, warnings, progress);

                foreach (string w in warnings)
                    Watchdog.Log("  WARN: " + w);

                RefreshStatuses();
                ShowMessage(string.Format(
                        "Search sets created: {0}, removed: {1}\nClash tests added: {2}, updated: {3}, renamed: {4}, unchanged: {5}, " +
                        "removed: {6}, skipped: {7}\n\nOne Undo (Ctrl+Z) reverts this.",
                        sets.Created, tests.SetsRemoved, tests.Added, tests.Updated, tests.Renamed, tests.Unchanged,
                        tests.Removed, tests.Skipped),
                    warnings, warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                Footer.Status = string.Format("Tests added {0}, updated {1}, renamed {2}, unchanged {3}.",
                    tests.Added, tests.Updated, tests.Renamed, tests.Unchanged);
            });
        }

        // ================================================================ matrix in / out

        // Loads an Excel matrix for preview. Nothing in the document changes here.
        private void ImportMatrix_Click(object sender, RoutedEventArgs e)
        {
            double tol;
            if (!TryReadTolerance(out tol) || !ConfirmDiscardEdits())
                return;

            var dlg = new OpenFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                Title = "Select the clash matrix"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            Run("Import Matrix", () =>
            {
                Watchdog.Log("=== Import Matrix === " + dlg.FileName);
                MatrixParseResult matrix = XlsxMatrixReader.Parse(dlg.FileName, tol);
                matrix.BestPractice = BestPractice;
                if (_hasModel)
                    NavisClashCreator.AssignModelScopes(matrix, _models);

                List<string> unsafeNames = ClashXmlWriter.FindUnsafeNames(matrix.Categories);
                if (unsafeNames.Count > 0)
                    matrix.Warnings.Add("Names containing '/' cannot be used in the exchange XML: " + string.Join(", ", unsafeNames));

                SetResult(matrix, false, dlg.FileName);
                Footer.Status = matrix.Tests.Count == 0
                    ? "The matrix has no filled cells."
                    : "Matrix loaded. Review the sets and tests, then Create.";
            });
        }

        // Exports what is ticked to a workbook the user can edit and re-import.
        private void ExportMatrix_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireResult())
                return;

            var dlg = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = SafeFileName(_resultFromScan ? "Clash Matrix" : _result.BatchName) + ".xlsx",
                Title = "Export the clash matrix"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            Run("Export Matrix", () =>
            {
                XlsxMatrixWriter.Write(dlg.FileName, _result.Effective());
                if (_resultFromScan)
                    PathText.Text = dlg.FileName;
                Footer.Status = "Exported " + dlg.FileName;
            });
        }

        private void GenerateXml_Click(object sender, RoutedEventArgs e)
        {
            double tol;
            if (!RequireResult() || !TryReadTolerance(out tol))
                return;

            MatrixParseResult effective = _result.Effective();
            List<string> unsafeNames = ClashXmlWriter.FindUnsafeNames(effective.Categories);
            if (unsafeNames.Count > 0)
            {
                MessageBox.Show(this, "These set names contain '/', which the exchange XML uses as a separator. " +
                    "Rename them first:\n\n" + string.Join("\n", unsafeNames),
                    Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Navisworks exchange (*.xml)|*.xml",
                FileName = SafeFileName(string.IsNullOrEmpty(_result.BatchName) ? "Clash Detection Matrix" : _result.BatchName) + ".xml",
                Title = "Save the clash-test XML"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            Run("Generate XML", () =>
            {
                Units? units = _hasModel ? _document.Units : (Units?)null;
                ClashXmlWriter.Write(dlg.FileName, effective.BatchName, effective.Tests, UnitConverter.XmlUnit(units), tol);
                Footer.Status = "Wrote " + dlg.FileName;
                MessageBox.Show(this, "Saved " + Path.GetFileName(dlg.FileName) + ".\n\nThe tests refer to search sets in the '" +
                    NavisClashCreator.FolderName + "' folder - create them (Create Search Sets) before " +
                    "importing the file in Clash Detective > Batch > Import.",
                    Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ================================================================ helpers

        /// <summary>Runs a change to many rows, then refreshes the tables once.</summary>
        private void Bulk(bool sets, Action change)
        {
            if (_result == null) return;
            _bulk = true;
            try { change(); }
            finally { _bulk = false; }
            if (sets) SetEdited(null);
            else TestEdited(null);
            foreach (TestRow r in _testRows) r.Refresh();
        }

        private void SetResult(MatrixParseResult result, bool fromScan, string source)
        {
            _result = result;
            _resultFromScan = fromScan;
            PathText.Text = source;

            _loading = true;
            (result.BestPractice ? BestPracticeBox : AllCategoriesBox).IsChecked = true;
            _loading = false;

            _setRows = MatrixLayout.Order(result).Select(name => new SetRow(result, name) { Edited = SetEdited }).ToList();
            SetsGrid.ItemsSource = _setRows;
            ApplyFilters(true, false);
            BuildTestRows();
            RefreshStatuses();
            ShowWarnings(result.Warnings);
            UpdateSummary();
            UpdateButtons();
        }

        private void RefreshRows()
        {
            foreach (SetRow r in _setRows) r.Refresh();
            foreach (TestRow r in _testRows) r.Refresh();
            RefreshStatuses();
            UpdateSummary();
        }

        private void BuildTestRows()
        {
            Func<double> tolerance = () =>
            {
                double tol;
                return UnitConverter.TryParseMm(ToleranceText.Text, out tol) ? tol : double.NaN;
            };
            _testRows = _result.Tests.Select(t => new TestRow(_result, t, tolerance) { Edited = TestEdited }).ToList();
            TestsGrid.ItemsSource = _testRows;
            ApplyFilters(false, true);
        }

        /// <summary>The Status columns: what exists in the model already, what is new, renamed or left out.</summary>
        private void RefreshStatuses()
        {
            if (_result == null)
                return;

            Dictionary<string, SelectionSet> sets = null;
            Dictionary<string, string> tests = null;
            if (_hasModel)
            {
                try
                {
                    sets = NavisClashCreator.GetManagedSets(_document);
                    tests = NavisClashCreator.ExistingTestsBySets(_document);
                }
                catch (Exception ex)
                {
                    Watchdog.LogException("Read statuses", ex);
                }
            }

            foreach (SetRow r in _setRows)
            {
                bool exists = sets != null && sets.ContainsKey(r.Name);
                r.Status = !r.Include ? (exists ? "Will delete" : "Excluded") : exists ? "In model" : "New";
            }
            foreach (TestRow r in _testRows)
            {
                string current = null;
                if (tests != null) tests.TryGetValue(NavisClashCreator.SetPairKey(r.LeftName, r.RightName), out current);
                if (!r.SetsIncluded) r.Status = "Set excluded";
                else if (!r.Include) r.Status = current != null ? "Unticked" : "Excluded";
                else if (current == null) r.Status = "New";
                else r.Status = current == r.Name ? "In model" : "Rename";
            }
        }

        private void UpdateSummary()
        {
            if (_result == null)
            {
                SummaryText.Text = "No matrix yet.";
                return;
            }
            int sets = _result.Categories.Count(_result.IsIncluded);
            int tests = _result.Tests.Count(_result.IsIncluded);
            SetsSummary.Text = string.Format("{0} of {1} sets ticked ({2}). Double-click a name or the categories to edit; untick a set to leave it out.",
                sets, _result.Categories.Count, _result.BestPractice ? "best practice" : "all categories");
            SummaryText.Text = string.Format("{0} of {1} tests ticked.", tests, _result.Tests.Count);
#if !NAVIS2026 && !NAVIS2027
            // ClashTest.Priority only exists from Navisworks 2026.
            SummaryText.Text += " Priorities are kept in the matrix and its export; Navisworks 2024/2025 cannot store them on a test.";
#endif
            UpdateHeaderBoxes();
        }

        /// <summary>A scan-built matrix follows the disciplines: rebuilt when one changed; an imported one gets its scopes again.</summary>
        private void RebuildIfDisciplinesChanged(List<ModelInfo> models)
        {
            if (_resultFromScan && _disciplinesChanged)
            {
                double tol;
                if (!UnitConverter.TryParseMm(ToleranceText.Text, out tol)) tol = 10;
                SetResult(NavisClashCreator.BuildFullMatrix(models, tol, BestPractice), true, "(built from the model - not exported yet)");
                _disciplinesChanged = false;
            }
            else if (!_resultFromScan)
            {
                NavisClashCreator.AssignModelScopes(_result, models);
                RefreshRows();
            }
        }

        /// <summary>True when there is nothing edited to lose, or the user agrees to lose it.</summary>
        private bool ConfirmDiscardEdits()
        {
            if (_result == null)
                return true;
            bool edited = _result.ExcludedSets.Count > 0 || _result.Tests.Any(t => !t.Include || t.CustomName);
            if (!edited)
                return true;
            return MessageBox.Show(this, "This rebuilds the list of sets and tests; the ticks and names you changed are lost.\n\nContinue?",
                Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        }

        private void UpdateButtons()
        {
            bool hasTests = _result != null && _result.Tests.Count > 0;
            ExportButton.IsEnabled = hasTests;
            XmlButton.IsEnabled = hasTests;
            CountButton.IsEnabled = _hasModel && _result != null;
            CreateSetsButton.IsEnabled = _hasModel && _result != null;
            UpdateFooter();
        }

        private void ShowWarnings(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                WarningsText.Text = "";
                WarningsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                WarningsText.Text = string.Join(Environment.NewLine, lines);
                WarningsPanel.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Sets of the matrix whose Revit categories are all absent from the scanned model.</summary>
        private static void AddNotInModelWarning(MatrixParseResult matrix, IList<string> modelCategories,
            List<string> warnings)
        {
            var present = new HashSet<string>(modelCategories, StringComparer.OrdinalIgnoreCase);
            List<string> absent = matrix.Categories
                .Where(set => !matrix.GetMembers(set).Any(m => present.Contains(NavisClashCreator.GetMainCategory(m))))
                .ToList();
            if (absent.Count > 0)
                warnings.Add("Not in the current model (their sets fill in when such a file is appended): " +
                    string.Join(", ", absent));
        }

        /// <summary>Scans the appended files (categories + disciplines) and shows them in the Models table.</summary>
        private List<ModelInfo> RefreshModels(List<string> warnings, NavisProgress progress)
        {
            _models = NavisClashCreator.ScanModels(_document, warnings, progress);
            ModelsGrid.ItemsSource = _models;
            return _models;
        }

        private bool RequireModel()
        {
            if (_hasModel)
                return true;
            MessageBox.Show(this, "Open a model in Navisworks first.", Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool RequireResult()
        {
            if (_result != null && _result.Categories.Count > 0)
                return true;
            MessageBox.Show(this, "Scan Model or Import a matrix first.", Caption, MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        private bool TryReadTolerance(out double tol)
        {
            if (UnitConverter.TryParseMm(ToleranceText.Text, out tol))
                return true;
            MessageBox.Show(this, "Tolerance must be a number of millimetres, 0 or more (e.g. 10 or 2.5).",
                Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            Tabs.SelectedIndex = 1;
            ToleranceText.Focus();
            ToleranceText.SelectAll();
            return false;
        }

        /// <summary>Runs an action with the wait cursor, reporting cancellation and errors the same way everywhere.</summary>
        private void Run(string name, Action action)
        {
            System.Windows.Input.Cursor previous = Cursor;
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                action();
            }
            catch (OperationCanceledException)
            {
                Watchdog.Log(name + ": cancelled");
                Footer.Status = name + " cancelled. Use Undo in Navisworks to revert any partial change.";
            }
            catch (IOException ex)
            {
                Watchdog.LogException(name, ex);
                MessageBox.Show(this, ex.Message + "\n\nIf the file is open in another program, close it and try again.",
                    Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Watchdog.LogException(name, ex);
                MessageBox.Show(this, ex.Message + "\n\nLog: " + Watchdog.LogPath, Caption,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = previous;
            }
        }

        private void ShowMessage(string text, IList<string> warnings, MessageBoxImage icon)
        {
            string message = text;
            if (warnings != null && warnings.Count > 0)
                message += "\n\n" + string.Join("\n", Trim(warnings, 10));
            MessageBox.Show(this, message, Caption, MessageBoxButton.OK, icon);
        }

        private static List<string> Trim(IList<string> lines, int max)
        {
            if (lines.Count <= max)
                return lines.ToList();
            List<string> list = lines.Take(max - 1).ToList();
            list.Add("... and " + (lines.Count - max + 1) + " more (see " + Watchdog.LogPath + ")");
            return list;
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
