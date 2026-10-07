using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Navisworks.Api;
using ClashMatrix.Model;
using ClashMatrix.Services;
using Microsoft.Win32;

namespace ClashMatrix.UI
{
    public partial class ClashMatrixWindow : Window
    {
        private readonly Document _document;
        private MatrixParseResult _result;

        public ClashMatrixWindow(Document document)
        {
            InitializeComponent();
            _document = document;

            if (document == null)
            {
                CreateSetsButton.IsEnabled = false;
                UpdateSetsButton.IsEnabled = false;
                CreateTestsButton.IsEnabled = false;
                StatusText.Text = "No document open.";
                return;
            }

            // If search sets already exist (e.g. from a previous session), pick them
            // up so "Create Clash Tests" works without re-creating them.
            try
            {
                List<string> existing = NavisClashCreator.GetExistingSetNames(document);
                if (existing.Count > 0)
                {
                    _result = NavisClashCreator.BuildFullMatrix(existing, ReadTolerance());
                    TestsGrid.ItemsSource = _result.Tests;
                    PathText.Text = "(existing search sets)";
                    RefreshSummary();
                    StatusText.Text = string.Format("Found {0} existing search sets - ready to create tests.",
                        existing.Count);
                }
            }
            catch
            {
                // best effort
            }
        }

        // Scans the model and creates one search set per category (flat, named by
        // category). Also builds the full in-memory matrix for later export/tests.
        private void CreateSets_Click(object sender, RoutedEventArgs e)
        {
            if (_document == null)
            {
                MessageBox.Show(this, "Open a Navisworks document first.", "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                double tol = ReadTolerance();
                var warnings = new List<string>();
                Watchdog.Clear();
                Watchdog.Log("=== Create Search Sets ===");
                Watchdog.Log("Tolerance: " + tol + " mm");

                List<string> categories = NavisClashCreator.GetModelCategories(_document);
                int setsCreated = NavisClashCreator.CreateSearchSets(_document, categories, warnings);
                _result = NavisClashCreator.BuildFullMatrix(categories, tol);

                PathText.Text = "(not exported yet)";
                TestsGrid.ItemsSource = _result.Tests;
                RefreshSummary();

                string msg = string.Format(
                    "Categories found: {0}\nSearch sets created: {1}\nMatrix in memory: {2} tests.",
                    categories.Count, setsCreated, _result.Tests.Count);
                if (warnings.Count > 0)
                    msg += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);
                MessageBox.Show(this, msg, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = string.Format("Sets created: {0}", setsCreated);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Create Search Sets", ex);
                MessageBox.Show(this, ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + Watchdog.LogPath, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Creates the clash tests from the current (in-memory) matrix, replacing any
        // existing tests.
        private void CreateTests_Click(object sender, RoutedEventArgs e)
        {
            if (_document == null)
            {
                MessageBox.Show(this, "Open a Navisworks document first.", "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If no matrix is loaded but search sets already exist, rebuild the full
            // matrix from them so tests can be created without re-creating sets.
            if (_result == null || _result.Tests.Count == 0)
            {
                List<string> existing = NavisClashCreator.GetExistingSetNames(_document);
                if (existing.Count == 0)
                {
                    MessageBox.Show(this, "No search sets found. Click 'Create Search Sets' first.",
                        "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _result = NavisClashCreator.BuildFullMatrix(existing, ReadTolerance());
                TestsGrid.ItemsSource = _result.Tests;
                RefreshSummary();
            }

            try
            {
                var warnings = new List<string>();
                Watchdog.Log("=== Create Clash Tests ===");

                NavisClashCreator.ClearTests(_document);
                int testsCreated = NavisClashCreator.CreateTests(_document, _result.Tests, warnings);
                Watchdog.Log("Clash tests created: " + testsCreated + " of " + _result.Tests.Count);
                foreach (string w in warnings)
                    Watchdog.Log("  WARN: " + w);

                string msg = string.Format("Clash tests created: {0} of {1}.",
                    testsCreated, _result.Tests.Count);
                if (warnings.Count > 0)
                    msg += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);
                MessageBox.Show(this, msg, "Clash Matrix",
                    MessageBoxButton.OK,
                    warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                StatusText.Text = string.Format("Tests created: {0}", testsCreated);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Create Clash Tests", ex);
                MessageBox.Show(this, ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + Watchdog.LogPath, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Re-scans the model and re-packs the search sets (picks up newly appended files).
        private void UpdateSets_Click(object sender, RoutedEventArgs e)
        {
            if (_document == null)
            {
                MessageBox.Show(this, "Open a Navisworks document first.", "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                double tol = ReadTolerance();
                var warnings = new List<string>();
                Watchdog.Log("=== Update Search Sets ===");

                List<string> categories = NavisClashCreator.GetModelCategories(_document);
                int setsUpdated = NavisClashCreator.UpdateSearchSets(_document, categories, warnings);
                _result = NavisClashCreator.BuildFullMatrix(categories, tol);

                PathText.Text = "(not exported yet)";
                TestsGrid.ItemsSource = _result.Tests;
                RefreshSummary();

                string msg = string.Format(
                    "Categories found: {0}\nSearch sets updated: {1}\nMatrix in memory: {2} tests.",
                    categories.Count, setsUpdated, _result.Tests.Count);
                if (warnings.Count > 0)
                    msg += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);
                MessageBox.Show(this, msg, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = string.Format("Sets updated: {0}", setsUpdated);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Update Search Sets", ex);
                MessageBox.Show(this, ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + Watchdog.LogPath, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Exports the current (in-memory) matrix to a workbook the user can edit.
        private void ExportMatrix_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureResult())
                return;

            var dlg = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = "Clash Matrix.xlsx",
                Title = "Export the clash matrix"
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                XlsxMatrixWriter.Write(dlg.FileName, _result);
                PathText.Text = dlg.FileName;
                StatusText.Text = "Exported " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Imports an (edited) matrix workbook and applies it: creates any missing
        // search sets, then replaces the clash tests (with updated priorities).
        private void UpdateMatrix_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                Title = "Select the (edited) clash matrix"
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                double tol = ReadTolerance();
                Watchdog.Log("=== Update Matrix ===");
                Watchdog.Log("File: " + dlg.FileName);

                _result = XlsxMatrixReader.Parse(dlg.FileName, tol);
                PathText.Text = dlg.FileName;
                TestsGrid.ItemsSource = _result.Tests;
                RefreshSummary();

                int setsCreated = 0;
                int testsCreated = 0;
                var warnings = new List<string>();
                if (_document != null)
                {
                    // Ensure search sets exist for the categories the matrix uses.
                    setsCreated = NavisClashCreator.CreateSearchSets(_document, _result.Categories, warnings);
                    // Replace the existing clash tests with the edited matrix.
                    NavisClashCreator.ClearTests(_document);
                    testsCreated = NavisClashCreator.CreateTests(_document, _result.Tests, warnings);
                    Watchdog.Log("Search sets created: " + setsCreated);
                    Watchdog.Log("Clash tests created: " + testsCreated + " of " + _result.Tests.Count);
                    foreach (string w in warnings)
                        Watchdog.Log("  WARN: " + w);
                }

                string msg = string.Format(
                    "Matrix: {0} tests.\nSearch sets created: {1}\nClash tests created: {2} (priorities applied).",
                    _result.Tests.Count, setsCreated, testsCreated);
                if (warnings.Count > 0)
                    msg += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);
                MessageBox.Show(this, msg, "Clash Matrix",
                    MessageBoxButton.OK,
                    warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                StatusText.Text = string.Format("Tests created: {0}", testsCreated);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Update Matrix", ex);
                MessageBox.Show(this, ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + Watchdog.LogPath, "Clash Matrix", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateXml_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureResult())
                return;

            string suggested = string.IsNullOrEmpty(_result.BatchName)
                ? "Clash Detection Matrix.xml"
                : _result.BatchName + ".xml";

            var dlg = new SaveFileDialog
            {
                Filter = "Navisworks exchange (*.xml)|*.xml",
                FileName = suggested,
                Title = "Save the clash-test XML"
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                string units = _document != null ? NavisClashCreator.GetUnitString(_document) : "ft";
                ClashXmlWriter.Write(dlg.FileName, _result.BatchName, _result.Tests, units);
                StatusText.Text = "Wrote " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MatrixButton_Click(object sender, RoutedEventArgs e)
        {
            MatrixButton.ContextMenu.PlacementTarget = MatrixButton;
            MatrixButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            MatrixButton.ContextMenu.IsOpen = true;
        }

        private void RefreshSummary()
        {
            SummaryText.Text = string.Format("{0} clash tests, {1} categories.",
                _result.Tests.Count, _result.Categories.Count);

            var warnings = new List<string>(_result.Warnings);
            ShowWarnings(warnings);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool EnsureResult()
        {
            if (_result == null || _result.Tests.Count == 0)
            {
                MessageBox.Show(this, "Create search sets (or update the matrix) first.", "Clash Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        private void ShowWarnings(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                WarningsText.Text = "";
                WarningsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                WarningsText.Text = string.Join(Environment.NewLine, lines);
                WarningsText.Visibility = Visibility.Visible;
            }
        }

        private double ReadTolerance()
        {
            double tol;
            if (double.TryParse(ToleranceText.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out tol) && tol > 0)
                return tol;
            return 10.0;
        }
    }
}
