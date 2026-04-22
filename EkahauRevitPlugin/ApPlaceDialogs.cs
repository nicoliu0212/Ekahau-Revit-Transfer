using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;

// WPF control aliases — Autodesk.Revit.UI also defines ComboBox, TextBox, etc.
using WpfComboBox     = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfCheckBox     = System.Windows.Controls.CheckBox;
using WpfTextBox      = System.Windows.Controls.TextBox;
using WpfListBox      = System.Windows.Controls.ListBox;
using WpfListBoxItem  = System.Windows.Controls.ListBoxItem;
using WpfGrid         = System.Windows.Controls.Grid;
using WpfColumnDef    = System.Windows.Controls.ColumnDefinition;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  AP Place — Family Picker Dialog (REQ 14)
    //  Three-column layout: Category → Family → Type
    //  With search box and last-selection memory (REQ 7).
    // ═══════════════════════════════════════════════════════════════════════

    public class ApPlaceFamilyPickerDialog : Window
    {
        private readonly Dictionary<string, Dictionary<string, List<FamilyTypeInfo>>> _data;
        private readonly WpfListBox _catList;
        private readonly WpfListBox _famList;
        private readonly WpfListBox _typeList;
        private readonly WpfTextBox _searchBox;
        private readonly TextBlock  _statusLabel;

        // Filtered view
        private List<string> _filteredCategories = new List<string>();
        private List<string> _filteredFamilies   = new List<string>();
        private List<FamilyTypeInfo> _filteredTypes = new List<FamilyTypeInfo>();

        public ElementId SelectedSymbolId   { get; private set; }
        public string SelectedCategoryName  { get; private set; } = "";
        public string SelectedFamilyName    { get; private set; } = "";
        public string SelectedTypeName      { get; private set; } = "";

        public ApPlaceFamilyPickerDialog(
            Dictionary<string, Dictionary<string, List<FamilyTypeInfo>>> data,
            string lastCategory, string lastFamily, string lastType)
        {
            _data = data;

            Title  = "AP Place — Select Family Type";
            Width  = 740;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            MinWidth  = 600;
            MinHeight = 400;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Select Family Type for AP Placement",
                FontSize   = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Choose the family type to use when placing access points in the model.",
                FontSize   = 11,
                Foreground = Brush("#BBDEFB"),
                Margin     = new Thickness(16, 0, 16, 10)
            });
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Search box ────────────────────────────────────────────
            var searchPanel = new DockPanel { Margin = new Thickness(12, 8, 12, 4) };
            searchPanel.Children.Add(new TextBlock
            {
                Text              = "Search: ",
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            });
            _searchBox = new WpfTextBox { FontSize = 12 };
            _searchBox.TextChanged += (s, e) => ApplySearch();
            searchPanel.Children.Add(_searchBox);
            DockPanel.SetDock(searchPanel, Dock.Top);
            root.Children.Add(searchPanel);

            // ── Status label ──────────────────────────────────────────
            _statusLabel = new TextBlock
            {
                FontSize = 11,
                Foreground = Brush("#666666"),
                Margin = new Thickness(14, 0, 14, 0),
            };
            DockPanel.SetDock(_statusLabel, Dock.Top);
            root.Children.Add(_statusLabel);

            // ── Buttons (bottom) ──────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(12, 8, 12, 10)
            };
            var btnOk = MakeButton("  Select  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                if (_typeList.SelectedItem is WpfListBoxItem item &&
                    item.Tag is FamilyTypeInfo info)
                {
                    SelectedSymbolId     = info.SymbolId;
                    SelectedCategoryName = info.CategoryName;
                    SelectedFamilyName   = info.FamilyName;
                    SelectedTypeName     = info.TypeName;
                    DialogResult = true;
                }
                else
                {
                    MessageBox.Show("Please select a family type.",
                        "AP Place", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };
            btnOk.Margin = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Column headers ────────────────────────────────────────
            var colHdrGrid = new WpfGrid { Margin = new Thickness(12, 4, 12, 2) };
            colHdrGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });
            colHdrGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(4) });
            colHdrGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });
            colHdrGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(4) });
            colHdrGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });

            var lbl1 = new TextBlock { Text = "Category", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brush("#555555") };
            var lbl2 = new TextBlock { Text = "Family",   FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brush("#555555") };
            var lbl3 = new TextBlock { Text = "Type",     FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brush("#555555") };
            WpfGrid.SetColumn(lbl1, 0);
            WpfGrid.SetColumn(lbl2, 2);
            WpfGrid.SetColumn(lbl3, 4);
            colHdrGrid.Children.Add(lbl1);
            colHdrGrid.Children.Add(lbl2);
            colHdrGrid.Children.Add(lbl3);
            DockPanel.SetDock(colHdrGrid, Dock.Top);
            root.Children.Add(colHdrGrid);

            // ── Three-column list boxes ───────────────────────────────
            var listGrid = new WpfGrid { Margin = new Thickness(12, 2, 12, 4) };
            listGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });
            listGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(4) });
            listGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });
            listGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(4) });
            listGrid.ColumnDefinitions.Add(new WpfColumnDef { Width = new GridLength(1, GridUnitType.Star) });

            _catList  = new WpfListBox { FontSize = 12, BorderThickness = new Thickness(1), BorderBrush = Brush("#CCCCCC") };
            _famList  = new WpfListBox { FontSize = 12, BorderThickness = new Thickness(1), BorderBrush = Brush("#CCCCCC") };
            _typeList = new WpfListBox { FontSize = 12, BorderThickness = new Thickness(1), BorderBrush = Brush("#CCCCCC") };

            WpfGrid.SetColumn(_catList, 0);
            WpfGrid.SetColumn(_famList, 2);
            WpfGrid.SetColumn(_typeList, 4);
            listGrid.Children.Add(_catList);
            listGrid.Children.Add(_famList);
            listGrid.Children.Add(_typeList);

            root.Children.Add(listGrid);

            // ── Wire up selection cascading ───────────────────────────
            _catList.SelectionChanged += (s, e) =>
            {
                if (_catList.SelectedItem is WpfListBoxItem item)
                    OnCategorySelected(item.Content.ToString());
            };
            _famList.SelectionChanged += (s, e) =>
            {
                if (_famList.SelectedItem is WpfListBoxItem item)
                    OnFamilySelected(item.Content.ToString());
            };

            Content = root;

            // ── Populate categories ───────────────────────────────────
            PopulateCategories();

            // ── REQ 7: Restore last selection ─────────────────────────
            if (!string.IsNullOrEmpty(lastCategory))
                TrySelect(_catList, lastCategory);
            if (!string.IsNullOrEmpty(lastFamily))
                TrySelect(_famList, lastFamily);
            if (!string.IsNullOrEmpty(lastType))
                TrySelect(_typeList, lastType);

            UpdateStatus();
        }

        private void PopulateCategories()
        {
            _catList.Items.Clear();
            _filteredCategories = _data.Keys.OrderBy(k => k).ToList();
            foreach (var cat in _filteredCategories)
            {
                int famCount = _data[cat].Count;
                _catList.Items.Add(new WpfListBoxItem
                {
                    Content = cat,
                    ToolTip = $"{famCount} families",
                });
            }
        }

        private void OnCategorySelected(string category)
        {
            _famList.Items.Clear();
            _typeList.Items.Clear();

            if (!_data.TryGetValue(category, out var families)) return;

            string search = _searchBox.Text?.Trim() ?? "";
            _filteredFamilies = families.Keys
                .Where(f => string.IsNullOrEmpty(search) ||
                            f.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            foreach (var fam in _filteredFamilies)
            {
                int typeCount = families[fam].Count;
                _famList.Items.Add(new WpfListBoxItem
                {
                    Content = fam,
                    ToolTip = $"{typeCount} type(s)",
                });
            }
            UpdateStatus();
        }

        private void OnFamilySelected(string family)
        {
            _typeList.Items.Clear();

            if (_catList.SelectedItem is not WpfListBoxItem catItem) return;
            string category = catItem.Content.ToString();

            if (!_data.TryGetValue(category, out var families)) return;
            if (!families.TryGetValue(family, out var types)) return;

            _filteredTypes = types;
            foreach (var t in types)
            {
                _typeList.Items.Add(new WpfListBoxItem
                {
                    Content = t.TypeName,
                    Tag     = t,
                });
            }

            // Auto-select first type
            if (_typeList.Items.Count > 0)
                _typeList.SelectedIndex = 0;

            UpdateStatus();
        }

        private void ApplySearch()
        {
            string search = _searchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(search))
            {
                PopulateCategories();
                return;
            }

            _catList.Items.Clear();
            _famList.Items.Clear();
            _typeList.Items.Clear();

            // Filter: show categories that have matching families or types
            var matchingCats = new List<string>();
            foreach (var cat in _data)
            {
                bool hasMatch = cat.Value.Any(f =>
                    f.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    f.Value.Any(t =>
                        t.TypeName.Contains(search, StringComparison.OrdinalIgnoreCase)));
                if (hasMatch)
                    matchingCats.Add(cat.Key);
            }

            matchingCats.Sort();
            foreach (var cat in matchingCats)
            {
                _catList.Items.Add(new WpfListBoxItem { Content = cat });
            }

            // Auto-select first matching category
            if (_catList.Items.Count > 0)
                _catList.SelectedIndex = 0;

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            int totalCats = _data.Count;
            int totalFams = _data.Values.Sum(c => c.Count);
            int totalTypes = _data.Values.Sum(c => c.Values.Sum(f => f.Count));
            _statusLabel.Text = $"{totalCats} categories, {totalFams} families, {totalTypes} types";
        }

        private static void TrySelect(WpfListBox list, string text)
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (list.Items[i] is WpfListBoxItem item &&
                    item.Content.ToString() == text)
                {
                    list.SelectedIndex = i;
                    list.ScrollIntoView(item);
                    return;
                }
            }
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(14, 5, 14, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AP Place — Confirmation Dialog (REQ 8+9+17)
    //  Scrollable list of APs grouped by floor, each with a checkbox.
    //  Includes marker cleanup option and workset selection.
    // ═══════════════════════════════════════════════════════════════════════

    public class ApPlaceConfirmDialog : Window
    {
        private readonly List<(WpfCheckBox Check, ApStagingEntry Ap)> _apChecks
            = new List<(WpfCheckBox, ApStagingEntry)>();
        private readonly WpfComboBox _worksetCombo;
        private readonly TextBlock   _countLabel;

        /// <summary>
        /// Always true — preview-marker cleanup is now unconditional in
        /// AP Place (the dialog used to expose this as an opt-in, but it
        /// served no real purpose: there's no reason to keep the
        /// crosshair markers and overlay image after real APs are placed).
        /// </summary>
        public bool CleanupMarkers => true;

        public string SelectedWorkset { get; private set; } = "";

        public ApPlaceConfirmDialog(
            string familyLabel,
            List<ApPlaceConfirmFloor> floors,
            List<string> worksetNames,
            string activeWorkset)
        {
            Title  = "AP Place — Confirm Placement";
            Width  = 640;
            Height = Math.Min(720, 260 + floors.Sum(f => f.AccessPoints.Count) * 26);
            if (Height < 400) Height = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            MinHeight = 350;
            MinWidth  = 500;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Confirm AP Placement",
                FontSize   = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text         = $"Family: {familyLabel}",
                FontSize     = 11,
                Foreground   = Brush("#BBDEFB"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(16, 0, 16, 4)
            });
            int totalAps = floors.Sum(f => f.AccessPoints.Count);
            hdr.Children.Add(new TextBlock
            {
                Text       = $"{totalAps} access point(s) across {floors.Count} floor(s)",
                FontSize   = 11,
                Foreground = Brush("#BBDEFB"),
                Margin     = new Thickness(16, 0, 16, 10)
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
            var btnOk = MakeButton("  Place  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                // Apply checkbox states to the AP entries (REQ 9)
                foreach (var (check, ap) in _apChecks)
                    ap.Include = check.IsChecked == true;

                SelectedWorkset = _worksetCombo?.SelectedItem?.ToString() ?? "";
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };
            btnOk.Margin = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Options panel (bottom, above buttons) ─────────────────
            var optPanel = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };

            // (Marker-cleanup checkbox removed — cleanup is now unconditional.)

            // Workset selector (REQ 12) — only if workshared
            if (worksetNames.Count > 0)
            {
                var wsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 4, 0, 2),
                };
                wsPanel.Children.Add(new TextBlock
                {
                    Text              = "Workset: ",
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                _worksetCombo = new WpfComboBox
                {
                    FontSize = 11,
                    Width    = 200,
                };
                foreach (var ws in worksetNames)
                    _worksetCombo.Items.Add(ws);
                if (!string.IsNullOrEmpty(activeWorkset))
                    _worksetCombo.SelectedItem = activeWorkset;
                else if (_worksetCombo.Items.Count > 0)
                    _worksetCombo.SelectedIndex = 0;
                wsPanel.Children.Add(_worksetCombo);
                optPanel.Children.Add(wsPanel);
            }
            else
            {
                _worksetCombo = new WpfComboBox(); // dummy to avoid null
            }

            DockPanel.SetDock(optPanel, Dock.Bottom);
            root.Children.Add(optPanel);

            // ── Count label ───────────────────────────────────────────
            _countLabel = new TextBlock
            {
                FontSize  = 11,
                Margin    = new Thickness(14, 4, 14, 0),
                Foreground = Brush("#666666"),
            };
            DockPanel.SetDock(_countLabel, Dock.Bottom);
            root.Children.Add(_countLabel);

            // ── Select All / Deselect All ────────────────────────────
            var selPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(12, 6, 12, 2)
            };
            var btnAll  = MakeSmallButton("Select All");
            btnAll.Click += (s, e) =>
            {
                foreach (var (check, _) in _apChecks)
                    check.IsChecked = true;
                UpdateCount();
            };
            var btnNone = MakeSmallButton("Deselect All");
            btnNone.Click += (s, e) =>
            {
                foreach (var (check, _) in _apChecks)
                    check.IsChecked = false;
                UpdateCount();
            };
            btnAll.Margin = new Thickness(0, 0, 6, 0);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            DockPanel.SetDock(selPanel, Dock.Top);
            root.Children.Add(selPanel);

            // ── AP list grouped by floor (REQ 17) ─────────────────────
            var listPanel = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };

            foreach (var floor in floors)
            {
                // Floor header
                listPanel.Children.Add(new TextBlock
                {
                    Text       = $"{floor.FloorName}  ({floor.ViewName})  " +
                                 $"— {floor.AccessPoints.Count} AP(s)",
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#1976D2"),
                    Margin     = new Thickness(0, 8, 0, 2),
                });

                // Separator
                listPanel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 0, 0, 4),
                });

                // APs
                foreach (var ap in floor.AccessPoints)
                {
                    ap.Include = true; // Default to included

                    string bandStr = EsxMarkerOps.FormatBands(ap.Bands);
                    string vendorModel = "";
                    if (!string.IsNullOrEmpty(ap.Vendor) || !string.IsNullOrEmpty(ap.Model))
                        vendorModel = $"  {ap.Vendor} {ap.Model}".Trim();

                    string label = $"{ap.Name}{vendorModel}  [{bandStr}]  {ap.MountingHeight:F1} m";

                    var cb = new WpfCheckBox
                    {
                        Content   = label,
                        FontSize  = 11,
                        IsChecked = true,
                        Margin    = new Thickness(10, 1, 2, 1),
                        ToolTip   = $"ID: {ap.Id}\n" +
                                    $"Position: ({ap.WorldX:F2}, {ap.WorldY:F2}) ft\n" +
                                    $"Tags: {(ap.Tags.Count > 0 ? string.Join(", ", ap.Tags) : "(none)")}",
                    };
                    cb.Checked   += (s, e) => UpdateCount();
                    cb.Unchecked += (s, e) => UpdateCount();
                    _apChecks.Add((cb, ap));
                    listPanel.Children.Add(cb);
                }
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
            };
            root.Children.Add(scroll);

            Content = root;
            UpdateCount();
        }

        private void UpdateCount()
        {
            int selected = _apChecks.Count(x => x.Check.IsChecked == true);
            _countLabel.Text = $"{selected} of {_apChecks.Count} AP(s) selected for placement";
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(14, 5, 14, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static Button MakeSmallButton(string text)
            => new Button
            {
                Content  = text, FontSize = 11,
                Padding  = new Thickness(8, 3, 8, 3),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AP Place — Summary Dialog
    //  Shows placement results: count per floor, warnings, marker cleanup.
    // ═══════════════════════════════════════════════════════════════════════

    public class ApPlaceSummaryDialog : Window
    {
        public ApPlaceSummaryDialog(
            int totalPlaced,
            Dictionary<string, int> placedByFloor,
            List<string> warnings,
            string familyLabel,
            int markersDeleted)
        {
            Title  = "AP Place — Results";
            Width  = 500;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            bool success = totalPlaced > 0;
            var hdr = new StackPanel
            {
                Background = success ? Brush("#1976D2") : Brush("#E65100"),
            };
            hdr.Children.Add(new TextBlock
            {
                Text       = success
                    ? $"Placed {totalPlaced} Access Point(s)"
                    : "No Access Points Placed",
                FontSize   = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            if (success)
            {
                hdr.Children.Add(new TextBlock
                {
                    Text         = $"Family: {familyLabel}",
                    FontSize     = 11,
                    Foreground   = Brush("#BBDEFB"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(16, 0, 16, 10)
                });
            }
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Close button ──────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(12, 8, 12, 10)
            };
            var btnClose = MakeButton("  Close  ", "#1976D2", "#FFFFFF");
            btnClose.Click += (s, e) => { DialogResult = true; };
            btnPanel.Children.Add(btnClose);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Details ───────────────────────────────────────────────
            var detailPanel = new StackPanel { Margin = new Thickness(16, 10, 16, 4) };

            // Per-floor breakdown
            if (placedByFloor.Count > 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text       = "Per-floor breakdown:",
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(0, 0, 0, 4),
                });

                foreach (var kv in placedByFloor.OrderBy(x => x.Key))
                {
                    detailPanel.Children.Add(new TextBlock
                    {
                        Text   = $"  {kv.Key}: {kv.Value} AP(s)",
                        FontSize = 11,
                        Margin = new Thickness(8, 1, 0, 1),
                    });
                }
            }

            // Marker cleanup
            if (markersDeleted > 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text       = $"\nPreview markers removed: {markersDeleted}",
                    FontSize   = 11,
                    Foreground = Brush("#666666"),
                    Margin     = new Thickness(0, 4, 0, 0),
                });
            }

            // Warnings
            if (warnings.Count > 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text       = "\nWarnings:",
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#E65100"),
                    Margin     = new Thickness(0, 8, 0, 2),
                });

                foreach (var w in warnings)
                {
                    detailPanel.Children.Add(new TextBlock
                    {
                        Text         = $"  {w}",
                        FontSize     = 11,
                        Foreground   = Brush("#E65100"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(8, 1, 0, 1),
                    });
                }
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = detailPanel,
            };
            root.Children.Add(scroll);

            Content = root;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(14, 5, 14, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
