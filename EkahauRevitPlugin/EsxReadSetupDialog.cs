using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WPF control aliases — Revit's API also defines ComboBox, TextBox,
// Grid, etc., so every WPF control name we use is disambiguated.
using WpfComboBox     = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfListBox      = System.Windows.Controls.ListBox;
using WpfListBoxItem  = System.Windows.Controls.ListBoxItem;
using WpfTextBox      = System.Windows.Controls.TextBox;
using WpfGrid         = System.Windows.Controls.Grid;

using Autodesk.Revit.DB;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Read — Unified Setup Dialog (v3.1.0)
    //
    //  Replaces the two separate ribbon buttons "ESX Quick" + "ESX Align"
    //  with a single "ESX Read" entry whose mode is auto-detected:
    //
    //    revitAnchor inside .esx                → Quick
    //    .ekahau-cal.json sidecar next to .esx  → Quick (via DWG calibration)
    //    Neither                                → Align (4-click visual)
    //
    //  Layout (700×600, resizable):
    //    ┌─ ESX Read ──────────────────────────────────────────────┐
    //    │  Ekahau .esx file:                                       │
    //    │  ┌─────────────────────────────────┐ [ Browse… ]         │
    //    │  └─────────────────────────────────┘                     │
    //    │  ┌─ Project Info ──────────────────────────────────────┐ │
    //    │  │ Project: Related Digital Michigan                    │ │
    //    │  │ APs:     360 × Cisco Catalyst 9164                   │ │
    //    │  │ Mode:    ⚡ Quick (revitAnchor found)                │ │
    //    │  └──────────────────────────────────────────────────────┘ │
    //    │  Floor plan (with AP count)     Revit view                │
    //    │  ┌──────────────────────────┐  ┌──────────────────────┐   │
    //    │  │ ▸ Level 1 (360 APs)      │  │ ▸ LEVEL 1 - WAP      │   │
    //    │  │   Level 2 (0 APs)        │  │   LEVEL 1 - PLAN     │   │
    //    │  └──────────────────────────┘  └──────────────────────┘   │
    //    │                                [ Cancel ] [ Start Read ]  │
    //    └─────────────────────────────────────────────────────────────┘
    //
    //  Style mirrors EsxAlignSetupDialog (procedural WPF, no XAML).
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadSetupDialog : Window
    {
        private readonly List<ViewPlan> _revitViews;
        private readonly WpfTextBox  _txbFile;
        private readonly Border      _infoBox;
        private readonly TextBlock   _infoText;
        private readonly WpfListBox  _lstFloors;
        private readonly WpfListBox  _lstViews;
        private readonly Button      _btnStart;

        // ── Output (read after ShowDialog() == true) ─────────────────────
        public string             EsxFilePath   { get; private set; }
        public EsxReadResult      ParsedEsxData { get; private set; }
        public EsxFloorPlanData   SelectedFloor { get; private set; }
        public ViewPlan           SelectedView  { get; private set; }
        public EsxReadMode        DetectedMode  { get; private set; } = EsxReadMode.Align;
        /// <summary>
        /// Path to the .ekahau-cal.json sidecar that made Quick mode
        /// possible — null when the anchor came from inside the .esx
        /// or when Align mode was chosen.
        /// </summary>
        public string             SidecarPath   { get; private set; }

        public EsxReadSetupDialog(List<ViewPlan> revitViews)
        {
            _revitViews = revitViews ?? new List<ViewPlan>();

            Title  = "ESX Read";
            Width  = 700;
            Height = 600;
            MinWidth  = 600;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            hdr.Children.Add(new TextBlock
            {
                Text       = "ESX Read",
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#1F4E79"),
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Import AP positions from Ekahau — Quick mode when calibration is "
                           + "available, Align mode (4-click) otherwise.",
                FontSize   = 12,
                Foreground = Brush("#666666"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 4, 0, 0),
            });
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Buttons (bottom — docked first so they always show) ────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 12, 0, 0),
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnCancel.Margin = new Thickness(0, 0, 8, 0);

            _btnStart = MakeButton("  Start Read  ", "#1F4E79", "#FFFFFF");
            _btnStart.Click += BtnStart_Click;
            _btnStart.FontWeight = FontWeights.SemiBold;
            _btnStart.IsEnabled = false;

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(_btnStart);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── File picker row ────────────────────────────────────────
            var filePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            filePanel.Children.Add(new TextBlock
            {
                Text       = "Ekahau .esx file:",
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 4),
            });

            var fileGrid = new WpfGrid();
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txbFile = new WpfTextBox
            {
                IsReadOnly = true,
                FontSize   = 12,
                Padding    = new Thickness(6, 4, 6, 4),
                Margin     = new Thickness(0, 0, 6, 0),
                MinHeight  = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brush("#555555"),
                Background = Brush("#F5F5F5"),
            };
            WpfGrid.SetColumn(_txbFile, 0);
            fileGrid.Children.Add(_txbFile);

            var btnBrowse = MakeButton("  Browse…  ", "#EEEEEE", "#333333");
            btnBrowse.Click += BtnBrowse_Click;
            WpfGrid.SetColumn(btnBrowse, 1);
            fileGrid.Children.Add(btnBrowse);

            filePanel.Children.Add(fileGrid);
            DockPanel.SetDock(filePanel, Dock.Top);
            root.Children.Add(filePanel);

            // ── Project Info Box ───────────────────────────────────────
            _infoBox = new Border
            {
                BorderBrush     = Brush("#D0D0D0"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(12),
                Margin          = new Thickness(0, 0, 0, 16),
                Background      = Brush("#FAFAFA"),
            };
            _infoText = new TextBlock
            {
                Text         = "Select an .esx file to see project details and detected mode.",
                FontSize     = 12,
                Foreground   = Brush("#888888"),
                TextWrapping = TextWrapping.Wrap,
            };
            _infoBox.Child = _infoText;
            DockPanel.SetDock(_infoBox, Dock.Top);
            root.Children.Add(_infoBox);

            // ── Floor plan + View (center, fills remaining space) ──────
            var selGrid = new WpfGrid();
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: floor plan list
            var leftCol = new DockPanel { LastChildFill = true };
            var leftHdr = new TextBlock
            {
                Text       = "Floor plan (with AP count):",
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 6),
            };
            DockPanel.SetDock(leftHdr, Dock.Top);
            leftCol.Children.Add(leftHdr);

            _lstFloors = new WpfListBox
            {
                FontSize      = 12,
                SelectionMode = SelectionMode.Single,
                BorderBrush   = Brush("#D0D0D0"),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_lstFloors, ScrollBarVisibility.Auto);
            _lstFloors.SelectionChanged += LstFloors_SelectionChanged;
            leftCol.Children.Add(_lstFloors);
            WpfGrid.SetColumn(leftCol, 0);
            selGrid.Children.Add(leftCol);

            // Right: Revit view list
            var rightCol = new DockPanel { LastChildFill = true };
            var rightHdr = new TextBlock
            {
                Text       = "Revit view (auto-matched by name):",
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 6),
            };
            DockPanel.SetDock(rightHdr, Dock.Top);
            rightCol.Children.Add(rightHdr);

            _lstViews = new WpfListBox
            {
                FontSize      = 12,
                SelectionMode = SelectionMode.Single,
                BorderBrush   = Brush("#D0D0D0"),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_lstViews, ScrollBarVisibility.Auto);
            _lstViews.SelectionChanged += LstViews_SelectionChanged;
            foreach (var v in _revitViews)
            {
                _lstViews.Items.Add(new WpfListBoxItem
                {
                    Content = v.Name,
                    Tag     = v,
                    Padding = new Thickness(4, 5, 4, 5),
                });
            }
            rightCol.Children.Add(_lstViews);
            WpfGrid.SetColumn(rightCol, 2);
            selGrid.Children.Add(rightCol);

            root.Children.Add(selGrid);

            Content = root;
        }

        // ──────────────────────────────────────────────────────────────
        //  Events
        // ──────────────────────────────────────────────────────────────

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title       = "Select Ekahau .esx Project File",
                Filter      = "Ekahau Project (*.esx)|*.esx|All Files (*.*)|*.*",
                FilterIndex = 1,
            };
            if (dlg.ShowDialog() == true)
            {
                _txbFile.Text = dlg.FileName;
                ParseEsxAndPopulate(dlg.FileName);
            }
        }

        private void ParseEsxAndPopulate(string filePath)
        {
            try
            {
                EsxFilePath = filePath;
                var entries = EsxZipReader.ReadEntries(filePath);
                ParsedEsxData = EsxZipReader.ParseEsx(entries);

                if (ParsedEsxData == null || ParsedEsxData.FloorPlans == null ||
                    ParsedEsxData.FloorPlans.Count == 0)
                {
                    _lstFloors.Items.Clear();
                    ShowInfo("No floor plans found in this .esx file.", "#C62828");
                    UpdateStartButtonState();
                    return;
                }

                // ── Mode auto-detection ────────────────────────────────
                //   Tier 1: any floor with revitAnchor → Quick (from .esx)
                //   Tier 2: .ekahau-cal.json sidecar in the same folder
                //           with a name match → Quick (from sidecar)
                //   Tier 3: neither → Align
                bool hasAnchor = ParsedEsxData.FloorPlans.Any(f => f.RevitAnchor != null);
                SidecarPath = null;

                if (!hasAnchor)
                {
                    try
                    {
                        // Direct sidecar (project.esx → project.ekahau-cal.json)
                        string directCal = Path.ChangeExtension(filePath, ".ekahau-cal.json");
                        if (File.Exists(directCal)) SidecarPath = directCal;

                        // Fallback: any *.ekahau-cal.json in the same folder
                        if (SidecarPath == null)
                        {
                            string folder = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                            {
                                var sidecars = Directory.GetFiles(folder, "*.ekahau-cal.json");
                                if (sidecars != null && sidecars.Length > 0)
                                    SidecarPath = sidecars[0];
                            }
                        }
                    }
                    catch { /* sidecar discovery is best-effort */ }
                }

                DetectedMode = (hasAnchor || SidecarPath != null)
                    ? EsxReadMode.Quick
                    : EsxReadMode.Align;

                // ── Project info banner ────────────────────────────────
                string projName = string.IsNullOrWhiteSpace(ParsedEsxData.ProjectName)
                    ? "(unnamed project)" : ParsedEsxData.ProjectName;
                int totalAps = ParsedEsxData.AccessPoints.Count;
                string vendorModel = "";
                if (totalAps > 0)
                {
                    var sample = ParsedEsxData.AccessPoints.FirstOrDefault(a =>
                        !string.IsNullOrEmpty(a.Vendor) || !string.IsNullOrEmpty(a.Model));
                    if (sample != null)
                    {
                        string v = string.IsNullOrEmpty(sample.Vendor) ? "" : sample.Vendor;
                        string m = string.IsNullOrEmpty(sample.Model)  ? "" : sample.Model;
                        string combined = string.Join(" ", new[] { v, m }
                            .Where(s => !string.IsNullOrEmpty(s)));
                        if (!string.IsNullOrEmpty(combined))
                            vendorModel = $" × {combined}";
                    }
                }

                string modeIcon = DetectedMode == EsxReadMode.Quick ? "⚡" : "🎯";
                string modeWhy;
                if (DetectedMode == EsxReadMode.Quick)
                {
                    modeWhy = SidecarPath != null
                        ? $"calibration sidecar found ({Path.GetFileName(SidecarPath)})"
                        : "revitAnchor found inside .esx";
                }
                else
                {
                    modeWhy = "no anchor, no sidecar — 4-click visual alignment needed";
                }

                ShowInfo(
                    $"Project:   {projName}\n" +
                    $"APs:       {totalAps}{vendorModel}\n" +
                    $"Mode:      {modeIcon} {DetectedMode} — {modeWhy}",
                    "#222222");

                // ── Populate floor plan list ───────────────────────────
                _lstFloors.Items.Clear();
                int firstWithAps = -1;
                for (int i = 0; i < ParsedEsxData.FloorPlans.Count; i++)
                {
                    var fp = ParsedEsxData.FloorPlans[i];
                    int apCount = ParsedEsxData.AccessPoints.Count(a => a.FloorPlanId == fp.Id);
                    _lstFloors.Items.Add(new WpfListBoxItem
                    {
                        Content = $"{fp.Name}  ({apCount} AP{(apCount != 1 ? "s" : "")})",
                        Tag     = fp,
                        Padding = new Thickness(4, 5, 4, 5),
                    });
                    if (firstWithAps < 0 && apCount > 0) firstWithAps = i;
                }
                _lstFloors.SelectedIndex = (firstWithAps >= 0) ? firstWithAps : 0;
                // (SelectionChanged fires here and auto-matches the view.)

                UpdateStartButtonState();
            }
            catch (Exception ex)
            {
                ParsedEsxData = null;
                _lstFloors.Items.Clear();
                ShowInfo($"Error parsing .esx file:\n{ex.Message}", "#C62828");
                UpdateStartButtonState();
            }
        }

        private void LstFloors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lstFloors.SelectedItem is WpfListBoxItem item &&
                item.Tag is EsxFloorPlanData fp)
            {
                var matched = AutoMatchView(fp.Name, _revitViews);
                if (matched != null)
                {
                    long targetId = VersionCompat.GetIdValue(matched.Id);
                    for (int i = 0; i < _lstViews.Items.Count; i++)
                    {
                        if (_lstViews.Items[i] is WpfListBoxItem vi &&
                            vi.Tag is ViewPlan v &&
                            VersionCompat.GetIdValue(v.Id) == targetId)
                        {
                            _lstViews.SelectedIndex = i;
                            _lstViews.ScrollIntoView(vi);
                            break;
                        }
                    }
                }
            }
            UpdateStartButtonState();
        }

        private void LstViews_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStartButtonState();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (ParsedEsxData == null)
            {
                MessageBox.Show(this, "Please select an Ekahau .esx file first.",
                    "ESX Read", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!(_lstFloors.SelectedItem is WpfListBoxItem floorItem &&
                  floorItem.Tag is EsxFloorPlanData floor))
            {
                MessageBox.Show(this, "Please select a floor plan.",
                    "ESX Read", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!(_lstViews.SelectedItem is WpfListBoxItem viewItem &&
                  viewItem.Tag is ViewPlan view))
            {
                MessageBox.Show(this, "Please select a Revit view.",
                    "ESX Read", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedFloor = floor;
            SelectedView  = view;
            DialogResult  = true;
            Close();
        }

        private void UpdateStartButtonState()
        {
            _btnStart.IsEnabled =
                ParsedEsxData != null &&
                _lstFloors.SelectedItem != null &&
                _lstViews.SelectedItem  != null;
        }

        private void ShowInfo(string text, string hex)
        {
            _infoText.Text = text;
            _infoText.Foreground = Brush(hex);
        }

        // ──────────────────────────────────────────────────────────────
        //  AutoMatchView — case-insensitive substring matching.
        //  Uses IndexOf instead of string.Contains(string, StringComparison)
        //  because the latter is .NET Core 2.1+ only (we still target net48).
        //  Mirrors EsxAlignSetupDialog.AutoMatchView for consistency.
        // ──────────────────────────────────────────────────────────────
        private static ViewPlan AutoMatchView(string esxName, List<ViewPlan> views)
        {
            if (string.IsNullOrEmpty(esxName) || views == null || views.Count == 0)
                return null;

            var exact = views.FirstOrDefault(v => v.Name == esxName);
            if (exact != null) return exact;

            var ci = views.FirstOrDefault(v =>
                v.Name.Equals(esxName, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;

            var contains = views.Where(v =>
                v.Name.IndexOf(esxName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                esxName.IndexOf(v.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (contains.Count >= 1) return contains[0];
            return null;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text,
                FontSize   = 13,
                Padding    = new Thickness(18, 6, 18, 6),
                Background = Brush(bg),
                Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
                Cursor     = System.Windows.Input.Cursors.Hand,
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
