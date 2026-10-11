using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Navisworks.Api;
using MHToolsN.Model;
using MHToolsN.Services;
using Microsoft.Win32;

namespace MHToolsN.UI
{
    /// <summary>
    /// Exports Clash Detective results to an Excel workbook and / or a PDF report, with
    /// the same choices as Clash Detective's own Report dialog: contents and statuses.
    /// </summary>
    public partial class ExportResultsWindow : Window
    {
        private const string Caption = "Export Clash Results";

        private readonly Document _document;
        private readonly List<TestChoice> _tests;
        private readonly ReportOptions _options;
        private readonly Dictionary<string, CheckBox> _contentBoxes = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, CheckBox> _statusBoxes = new Dictionary<string, CheckBox>();

        public ExportResultsWindow(Document document)
            : this(document, ClashResultsReader.ListTests(document))
        {
        }

        /// <summary>With the tests already listed (also lets the window be previewed without a model).</summary>
        private ExportResultsWindow(Document document, List<TestChoice> tests)
        {
            MhTheme.Apply(this);
            InitializeComponent();
            _document = document;
            _options = ReportOptions.Load();

            BuildOptionBoxes();

            _tests = tests;
            TestsGrid.ItemsSource = _tests;

            string file = document != null ? document.FileName : null;
            string folder = !string.IsNullOrEmpty(file)
                ? Path.GetDirectoryName(file)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string name = !string.IsNullOrEmpty(file) ? Path.GetFileNameWithoutExtension(file) : "Clash";
            PathBox.Text = Path.Combine(folder ?? "", name + " - Clash Results " + DateTime.Now.ToString("yyyy-MM-dd") + ".xlsx");

            if (_tests.Count == 0)
            {
                ExportButton.IsEnabled = false;
                Footer.Status = "There are no clash tests in this model.";
            }
            UpdateSelectionText();
        }

        /// <summary>The Contents and Include-clashes tick boxes, from the remembered choices.</summary>
        private void BuildOptionBoxes()
        {
            foreach (KeyValuePair<string, string> c in ReportOptions.Contents)
            {
                var box = new CheckBox { Content = c.Value, IsChecked = _options.Has(c.Key) };
#if !NAVIS2026 && !NAVIS2027
                // Priority, Date Resolved and Resolved By are clash fields from Navisworks 2026.
                if (c.Key == "Priority" || c.Key == "DateResolved" || c.Key == "ResolvedBy")
                {
                    box.IsEnabled = false;
                    box.ToolTip = "Clash results have this from Navisworks 2026.";
                }
#endif
                if (c.Key == "Image")
                    box.ToolTip = "A view of every clash in the PDF, beside its details. Takes a moment per clash.";
                _contentBoxes[c.Key] = box;
                ContentsPanel.Children.Add(box);
            }

            foreach (string s in ClashReport.Statuses)
            {
                var box = new CheckBox { Content = s, IsChecked = _options.Statuses.Contains(s) };
                box.Click += (sender, e) => UpdateSelectionText();
                _statusBoxes[s] = box;
                StatusPanel.Children.Add(box);
            }

            ExcelBox.IsChecked = _options.Excel;
            PdfBox.IsChecked = _options.Pdf;
        }

        /// <summary>The choices on screen, into the options (and remembered for next time).</summary>
        private void ReadOptions()
        {
            foreach (KeyValuePair<string, CheckBox> kv in _contentBoxes)
                _options.Set(kv.Key, kv.Value.IsChecked == true);
            _options.Statuses.Clear();
            foreach (KeyValuePair<string, CheckBox> kv in _statusBoxes)
                if (kv.Value.IsChecked == true) _options.Statuses.Add(kv.Key);
            _options.Excel = ExcelBox.IsChecked == true;
            _options.Pdf = PdfBox.IsChecked == true;
            _options.Save();
        }

        private void All_Click(object sender, RoutedEventArgs e) { Select(t => true); }
        private void None_Click(object sender, RoutedEventArgs e) { Select(t => false); }
        private void ClashMatrix_Click(object sender, RoutedEventArgs e) { Select(t => t.IsClashMatrix); }
        private void WithClashes_Click(object sender, RoutedEventArgs e) { Select(t => t.Total > 0); }
        private void Tick_Click(object sender, RoutedEventArgs e) { UpdateSelectionText(); }

        private void ContentsAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _contentBoxes.Values) if (box.IsEnabled) box.IsChecked = true;
        }

        private void ContentsNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _contentBoxes.Values) box.IsChecked = false;
        }

        private void Select(Func<TestChoice, bool> pick)
        {
            foreach (TestChoice t in _tests)
                t.IsSelected = pick(t);
            UpdateSelectionText();
        }

        private void UpdateSelectionText()
        {
            List<TestChoice> chosen = _tests.Where(t => t.IsSelected).ToList();
            SelectionText.Text = string.Format("{0} of {1} selected, {2} clashes", chosen.Count, _tests.Count, chosen.Sum(t => t.Total));
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Excel workbook and PDF report (*.xlsx)|*.xlsx",
                Title = "Save the clash results",
                FileName = Path.GetFileName(PathBox.Text)
            };
            try { dlg.InitialDirectory = Path.GetDirectoryName(PathBox.Text); } catch { }
            if (dlg.ShowDialog(this) == true)
                PathBox.Text = dlg.FileName;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            ReadOptions();
            List<string> names = _tests.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            if (names.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one clash test.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_options.Excel && !_options.Pdf)
            {
                MessageBox.Show(this, "Choose the Excel workbook, the PDF report, or both.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_options.Statuses.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one status under Include clashes.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string basePath;
            try
            {
                basePath = Path.Combine(Path.GetDirectoryName(PathBox.Text.Trim()), Path.GetFileNameWithoutExtension(PathBox.Text.Trim()));
            }
            catch
            {
                MessageBox.Show(this, "Choose where to save (Browse...).", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool images = _options.Pdf && _options.Image;
            int clashes = _tests.Where(t => t.IsSelected).Sum(t => t.Total);
            if (images && clashes > 400 &&
                MessageBox.Show(this, "Up to " + clashes + " clashes: drawing a picture of each takes a while (Cancel on the progress bar stops it).\n\nContinue with pictures?",
                    Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                images = false;

            System.Windows.Input.Cursor previous = Cursor;
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                Watchdog.Log("=== Export Results === " + names.Count + " test(s), statuses " + string.Join("/", _options.Statuses) +
                             ", images=" + images + " -> " + basePath);
                ClashReport report;
                using (var progress = new NavisProgress(images ? "Reading clash results and pictures" : "Reading clash results"))
                    report = ClashResultsReader.Read(_document, names, _options.Statuses, images, progress);

                var written = new List<string>();
                if (_options.Excel)
                {
                    ResultsExporter.WriteExcel(basePath + ".xlsx", report, _options);
                    written.Add(basePath + ".xlsx");
                }
                if (_options.Pdf)
                {
                    ResultsExporter.WritePdf(basePath + ".pdf", report, MhTheme.Product, _options);
                    written.Add(basePath + ".pdf");
                }

                Footer.Status = "Wrote " + string.Join(" and ", written.Select(Path.GetFileName));
                if (MessageBox.Show(this, "Exported " + report.Tests.Sum(t => t.Results.Count) + " clashes from " + report.Tests.Count +
                        " test(s):\n\n" + string.Join("\n", written) + "\n\nOpen the folder?", Caption,
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start("explorer.exe", "/select,\"" + written[written.Count - 1] + "\"");
                }
            }
            catch (OperationCanceledException)
            {
                Footer.Status = "Export cancelled.";
            }
            catch (IOException ex)
            {
                Watchdog.LogException("Export Results", ex);
                MessageBox.Show(this, ex.Message + "\n\nIf the file is open (Excel, a PDF viewer), close it and try again.",
                    Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Export Results", ex);
                MessageBox.Show(this, ex.Message + "\n\nLog: " + Watchdog.LogPath, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = previous;
            }
        }
    }
}
