using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WPF control aliases — Autodesk.Revit.UI / DB also define ComboBox,
// TextBox, Grid, etc.  Disambiguate every WPF control name we use.
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
    //  ESX Align — Setup Dialog (v2.7.0)
    //
    //  Single WPF window that replaces what used to be 4 separate dialogs:
    //    - OpenFileDialog for .esx
    //    - EsxReadFloorSelectorDialog
    //    - EsxReadViewMatchDialog
    //    - (any pre-flight TaskDialog warnings)
    //
    //  User flow:
    //    1. Click Browse → pick .esx file → background parse + populate
    //       floor list with AP counts
    //    2. Pick a floor → auto-match Revit view in the combo box
    //    3. Click Start Align (enabled only when all three are valid)
    //
    //  Style: matches EsxReadDialogs.cs / LinkedModelSelectorDialog.cs —
    //  procedural WPF, no XAML, DockPanel root, DialogResult = true/false.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxAlignSetupDialog : Window
    {
        private readonly List<ViewPlan> _revitViews;
        private readonly WpfTextBox _txbFile;
        private readonly WpfListBox _lstFloors;
        private readonly WpfComboBox _cmbViews;
        private readonly TextBlock _lblStatus;
        private readonly Button _btnStart;

        public string EsxFilePath { get; private set; }
        public EsxReadResult ParsedEsxData { get; private set; }
        public EsxFloorPlanData SelectedFloor { get; private set; }
        public ViewPlan SelectedView { get; private set; }

        public EsxAlignSetupDialog(List<ViewPlan> revitViews)
        {
            _revitViews = revitViews ?? new List<ViewPlan>();

            Title  = "ESX Align — Setup";
            Width  = 620;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Margin = new Thickness(16, 12, 16, 4) };
            hdr.Children.Add(new TextBlock
            {
                Text       = "ESX Align — Setup",
                FontSize   = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#1976D2"),
                Margin     = new Thickness(0, 0, 0, 6)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Select an Ekahau .esx file, choose a floor plan, and match it to a Revit view.",
                FontSize   = 12,
                FontStyle  = FontStyles.Italic,
                Foreground = Brush("#555555"),
                TextWrapping = TextWrapping.Wrap,
            });
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Buttons (bottom) ──────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(12, 8, 12, 10)
            };
            _btnStart = MakeButton("  Start Align  ", "#1976D2", "#FFFFFF");
            _btnStart.Click += BtnStart_Click;
            _btnStart.IsEnabled = false;
            _btnStart.Margin = new Thickness(0, 0, 8, 0);
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(_btnStart);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── File selection row ────────────────────────────────────
            var filePanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            filePanel.Children.Add(new TextBlock
            {
                Text       = "Ekahau .esx file:",
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 4)
            });

            // Horizontal layout: TextBox (stretches) + Browse button (fixed)
            var fileGrid = new WpfGrid();
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txbFile = new WpfTextBox
            {
                IsReadOnly = true,
                FontSize   = 11,
                Padding    = new Thickness(6, 4, 6, 4),
                Margin     = new Thickness(0, 0, 6, 0),
                MinHeight  = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brush("#555555"),
            };
            WpfGrid.SetColumn(_txbFile, 0);
            fileGrid.Children.Add(_txbFile);

            var btnBrowse = MakeButton("  Browse…  ", "#EEEEEE", "#333333");
            btnBrowse.Click += BtnBrowse_Click;
            btnBrowse.Padding = new Thickness(12, 4, 12, 4);
            WpfGrid.SetColumn(btnBrowse, 1);
            fileGrid.Children.Add(btnBrowse);

            filePanel.Children.Add(fileGrid);
            root.Children.Add(filePanel);

            // ── Floor plan + View selection (side-by-side Grid) ────────
            var selPanel = new StackPanel { Margin = new Thickness(12, 4, 12, 8) };

            var selGrid = new WpfGrid{ Height = 240 };
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            selGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left column: Floor plans
            var leftCol = new DockPanel { LastChildFill = true };
            var leftHdr = new TextBlock
            {
                Text       = "Floor plan (with AP count):",
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(leftHdr, Dock.Top);
            leftCol.Children.Add(leftHdr);
            _lstFloors = new WpfListBox
            {
                FontSize      = 12,
                SelectionMode = SelectionMode.Single,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_lstFloors, ScrollBarVisibility.Auto);
            _lstFloors.SelectionChanged += LstFloors_SelectionChanged;
            leftCol.Children.Add(_lstFloors);
            WpfGrid.SetColumn(leftCol, 0);
            selGrid.Children.Add(leftCol);

            // Right column: Revit views
            var rightCol = new DockPanel { LastChildFill = true };
            var rightHdr = new TextBlock
            {
                Text       = "Revit view:",
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#333333"),
                Margin     = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(rightHdr, Dock.Top);
            rightCol.Children.Add(rightHdr);
            _cmbViews = new WpfComboBox
            {
                FontSize = 12,
                Margin   = new Thickness(0, 0, 0, 4),
            };
            foreach (var view in _revitViews)
            {
                var item = new WpfComboBoxItem
                {
                    Content = view.Name,
                    Tag     = view
                };
                _cmbViews.Items.Add(item);
            }
            _cmbViews.SelectionChanged += CmbViews_SelectionChanged;
            DockPanel.SetDock(_cmbViews, Dock.Top);
            rightCol.Children.Add(_cmbViews);

            // Filler so the ComboBox doesn't stretch to fill 240px tall:
            var rightFiller = new TextBlock
            {
                Text       = "(auto-matched when a floor plan is selected — pick a different view here if the match is wrong)",
                FontSize   = 11,
                Foreground = Brush("#888888"),
                FontStyle  = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(2, 4, 2, 0),
            };
            rightCol.Children.Add(rightFiller);

            WpfGrid.SetColumn(rightCol, 2);
            selGrid.Children.Add(rightCol);

            selPanel.Children.Add(selGrid);
            root.Children.Add(selPanel);

            // ── Status label ──────────────────────────────────────────
            _lblStatus = new TextBlock
            {
                Text         = "",
                FontSize     = 11,
                Foreground   = Brush("#666666"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(12, 4, 12, 8),
                MinHeight    = 18,
            };
            root.Children.Add(_lblStatus);

            Content = root;
        }

        // ──────────────────────────────────────────────────────────────
        //  Event handlers
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
                    _lblStatus.Text = "No floor plans found in this .esx file.";
                    _lblStatus.Foreground = Brush("#C62828");
                    UpdateStartButtonState();
                    return;
                }

                _lstFloors.Items.Clear();
                int firstFloorWithAps = -1;

                for (int i = 0; i < ParsedEsxData.FloorPlans.Count; i++)
                {
                    var fp = ParsedEsxData.FloorPlans[i];
                    int apCount = ParsedEsxData.AccessPoints.Count(a => a.FloorPlanId == fp.Id);

                    var item = new WpfListBoxItem
                    {
                        Content = $"{fp.Name}  ({apCount} AP{(apCount != 1 ? "s" : "")})",
                        Tag     = fp,
                    };
                    _lstFloors.Items.Add(item);

                    if (firstFloorWithAps < 0 && apCount > 0)
                        firstFloorWithAps = i;
                }

                // Auto-select first floor with APs (falls back to first floor overall).
                if (firstFloorWithAps >= 0)
                    _lstFloors.SelectedIndex = firstFloorWithAps;
                else
                    _lstFloors.SelectedIndex = 0;

                _lblStatus.Text = $"Parsed {ParsedEsxData.FloorPlans.Count} floor plan(s), " +
                                  $"{ParsedEsxData.AccessPoints.Count} access point(s) total.";
                _lblStatus.Foreground = Brush("#2E7D32");
                UpdateStartButtonState();
            }
            catch (Exception ex)
            {
                ParsedEsxData = null;
                _lstFloors.Items.Clear();
                _cmbViews.SelectedIndex = -1;
                _lblStatus.Text = $"Error parsing .esx file: {ex.Message}";
                _lblStatus.Foreground = Brush("#C62828");
                UpdateStartButtonState();
            }
        }

        private void LstFloors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lstFloors.SelectedItem is WpfListBoxItem item && item.Tag is EsxFloorPlanData fp)
            {
                var matchedView = AutoMatchView(fp.Name, _revitViews);
                if (matchedView != null)
                {
                    long targetId = VersionCompat.GetIdValue(matchedView.Id);
                    for (int i = 0; i < _cmbViews.Items.Count; i++)
                    {
                        if (_cmbViews.Items[i] is WpfComboBoxItem cmbi &&
                            cmbi.Tag is ViewPlan view &&
                            VersionCompat.GetIdValue(view.Id) == targetId)
                        {
                            _cmbViews.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            UpdateStartButtonState();
        }

        private void CmbViews_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStartButtonState();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (ParsedEsxData == null)
            {
                MessageBox.Show(this,
                    "Please select an Ekahau .esx file first.",
                    "ESX Align",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!(_lstFloors.SelectedItem is WpfListBoxItem floorItem &&
                  floorItem.Tag is EsxFloorPlanData floor))
            {
                MessageBox.Show(this,
                    "Please select a floor plan.",
                    "ESX Align",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!(_cmbViews.SelectedItem is WpfComboBoxItem viewItem &&
                  viewItem.Tag is ViewPlan view))
            {
                MessageBox.Show(this,
                    "Please select a Revit view.",
                    "ESX Align",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
                _cmbViews.SelectedItem != null;
        }

        // ──────────────────────────────────────────────────────────────
        //  Helpers
        //
        //  AutoMatchView — case-insensitive substring matching.  Uses
        //  IndexOf instead of string.Contains(string, StringComparison)
        //  because the latter is .NET Core 2.1+ only (not net48).
        // ──────────────────────────────────────────────────────────────

        private static ViewPlan AutoMatchView(string esxName, List<ViewPlan> views)
        {
            if (string.IsNullOrEmpty(esxName) || views == null || views.Count == 0)
                return null;

            // 1. Exact (case-sensitive) match.
            var exact = views.FirstOrDefault(v => v.Name == esxName);
            if (exact != null) return exact;

            // 2. Case-insensitive exact match.
            var ci = views.FirstOrDefault(v =>
                v.Name.Equals(esxName, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;

            // 3. Bidirectional substring match (unique).
            var contains = views.Where(v =>
                v.Name.IndexOf(esxName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                esxName.IndexOf(v.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (contains.Count == 1) return contains[0];
            if (contains.Count > 1)  return contains[0];   // best-effort first match

            return null;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text,
                FontSize   = 12,
                Padding    = new Thickness(14, 5, 14, 5),
                Background = Brush(bg),
                Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
