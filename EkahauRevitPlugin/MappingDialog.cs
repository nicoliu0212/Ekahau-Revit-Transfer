using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.UI;

// Disambiguate WPF controls vs Revit UI controls with the same name
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// WPF dialog for mapping Revit types to Ekahau presets.
    ///
    /// Req 4:  "Accept All Suggestions" button
    /// Req 5:  Batch "Set All to…" ComboBox per category group
    /// Req 6:  Search / filter by type name
    /// Req 7:  Suggestion-source tooltip on every row
    /// Req 8:  Cancel confirmation when changes exist
    /// Req 10: DockPanel root layout — ScrollViewer never clips buttons
    /// Req 11: No dB values in ComboBox labels, smaller MinWidth
    /// Req 3c: Host-model / link-model subsection headers within each group
    /// </summary>
    public class MappingDialog : Window
    {
        private const string SkipLabel = "-- Skip --";

        // Row index → ComboBox  (Req 5 batch apply, Req 4 accept-all)
        private readonly Dictionary<int, WpfComboBox> _combos = new Dictionary<int, WpfComboBox>();
        // Row index → suggested preset key  (Req 4)
        private readonly Dictionary<int, string>  _suggestions = new Dictionary<int, string>();
        // Row index → initial SelectedIndex  (Req 8)
        private readonly Dictionary<int, int>     _initialSelections = new Dictionary<int, int>();
        // Category → list of row indices  (Req 5)
        private readonly Dictionary<string, List<int>> _indicesByCategory =
            new Dictionary<string, List<int>>
            {
                ["wall"] = new List<int>(), ["door"] = new List<int>(), ["window"] = new List<int>()
            };
        // Req 6: all rows for search filtering
        private readonly List<RowSearchEntry> _searchRows = new List<RowSearchEntry>();
        // Req 6: all groups for collapse logic
        private readonly List<(GroupBox Group, List<RowSearchEntry> Rows)> _searchGroups =
            new List<(GroupBox, List<RowSearchEntry>)>();

        /// <summary>rowIndex → chosen preset key (null key = skipped)</summary>
        public Dictionary<int, string> Result { get; private set; }

        // ── Constructor ──────────────────────────────────────────────────

        public MappingDialog(List<TypeItem> typeItems, string viewName, string linkName = null)
        {
            Title = "Ekahau Wall Type Configuration";
            Width = 720;
            Height = 700;       // Req 10: taller default
            MinWidth = 620;
            MinHeight = 500;    // Req 10: taller minimum
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            Background = Brush("#FFFFFF");

            bool hasLinks = typeItems.Any(t => t.Source == "link");

            // ── Root: DockPanel (Req 10 — replaces StackPanel) ───────────
            var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

            // ── Header section (dock top) ────────────────────────────────
            var headerPanel = new StackPanel();

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Active View:  " + viewName,
                FontSize = 13, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            int nW = typeItems.Count(t => t.Category == "wall");
            int nD = typeItems.Count(t => t.Category == "door");
            int nN = typeItems.Count(t => t.Category == "window");
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"{nW} Wall types,  {nD} Door types,  {nN} Window types",
                FontSize = 12, Foreground = Brush("#555555"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Req 11: removed dB reference from hint
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Select Ekahau type for each element.",
                FontSize = 11, Foreground = Brush("#888888"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            DockPanel.SetDock(headerPanel, Dock.Top);
            root.Children.Add(headerPanel);

            // ── Search box (Req 6, dock top) ─────────────────────────────
            var searchBox = new WpfTextBox
            {
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Filter by type name..."
            };
            // Placeholder via watermark style
            ApplyPlaceholder(searchBox, "Filter by type name...");
            searchBox.TextChanged += (s, e) => FilterRows(searchBox.Text);
            DockPanel.SetDock(searchBox, Dock.Top);
            root.Children.Add(searchBox);

            // ── Note (dock bottom, MUST be before ScrollViewer) ──────────
            var note = new TextBlock
            {
                Text = "* = already has Ekahau_WallType value (will be overwritten if changed)",
                FontSize = 10, Foreground = Brush("#999999"),
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(note, Dock.Bottom);
            root.Children.Add(note);

            // ── Button panel (dock bottom, MUST be before ScrollViewer) ──
            var bp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            // Req 4: Accept All Suggestions button
            var btnAcceptAll = new Button
            {
                Content = "  Accept All Suggestions  ",
                FontSize = 12,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 12, 0)
            };
            btnAcceptAll.Click += OnAcceptAll;

            var btnOk = new Button
            {
                Content = "  Apply  ",
                FontSize = 13,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnOk.Click += OnApply;

            var btnCancel = new Button
            {
                Content = "  Cancel  ",
                FontSize = 13,
                Padding = new Thickness(16, 6, 16, 6)
            };
            btnCancel.Click += OnCancel;

            bp.Children.Add(btnAcceptAll);
            bp.Children.Add(btnOk);
            bp.Children.Add(btnCancel);
            DockPanel.SetDock(bp, Dock.Bottom);
            root.Children.Add(bp);

            // ── Scrollable content (LastChildFill — fills remaining space) ─
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

            var catOrder = new[] { ("wall", "Walls"), ("door", "Doors"), ("window", "Windows") };
            var catColors = new Dictionary<string, string>
            {
                ["wall"] = "#E8F0FE", ["door"] = "#FEF3E8", ["window"] = "#E8FEF0"
            };

            int rowIndex = 0; // global row index for dict keys

            foreach (var (catKey, catTitle) in catOrder)
            {
                var catItems = typeItems.Where(t => t.Category == catKey).ToList();
                if (catItems.Count == 0) continue;

                // ── GroupBox with batch-apply header (Req 5) ─────────────
                var group = new GroupBox
                {
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(6, 4, 6, 4)
                };

                // Header = horizontal panel with title + batch ComboBox
                var groupHeader = new StackPanel { Orientation = Orientation.Horizontal };
                groupHeader.Children.Add(new TextBlock
                {
                    Text = $"  {catTitle}  ({catItems.Count})",
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                groupHeader.Children.Add(new TextBlock
                {
                    Text = "    Set all: ",
                    FontSize = 11, Foreground = Brush("#777777"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var batchCombo = new WpfComboBox
                {
                    FontSize = 11, MinWidth = 180,
                    VerticalAlignment = VerticalAlignment.Center
                };
                batchCombo.Items.Add(new WpfComboBoxItem
                {
                    Content = "(select to batch apply...)",
                    IsEnabled = false, FontStyle = FontStyles.Italic
                });
                foreach (string pk in EkahauPresets.GetPresetsForCategory(catKey))
                {
                    batchCombo.Items.Add(new WpfComboBoxItem
                    { Content = EkahauPresets.DisplayLabel(pk), Tag = pk });
                }
                batchCombo.SelectedIndex = 0;

                string capturedCatKey = catKey;
                batchCombo.SelectionChanged += (s, e) =>
                {
                    if (batchCombo.SelectedItem is WpfComboBoxItem ci && ci.Tag is string pk)
                    {
                        // Set all combos in this category
                        foreach (int idx in _indicesByCategory[capturedCatKey])
                            SetComboToPreset(_combos[idx], pk);
                        batchCombo.SelectedIndex = 0; // reset placeholder
                    }
                };

                groupHeader.Children.Add(batchCombo);
                group.Header = groupHeader;

                var inner = new StackPanel();

                // Column header row
                var hdrGrid = MakeColumnHeaderGrid();
                inner.Children.Add(hdrGrid);

                string bg = catColors.TryGetValue(catKey, out var bgc) ? bgc : "#F5F5F5";

                // Req 3c: split items into host / link subsections
                var hostItems = catItems.Where(t => t.Source == "host").OrderBy(t => t.Name).ToList();
                var linkItems = catItems.Where(t => t.Source == "link").OrderBy(t => t.Name).ToList();

                var groupSearchRows = new List<RowSearchEntry>();

                if (hasLinks)
                {
                    // Host sub-header
                    if (hostItems.Count > 0)
                        inner.Children.Add(MakeSubHeader($"── Host Model ({hostItems.Count}) ──"));

                    AddItemRows(inner, hostItems, catKey, bg, ref rowIndex, groupSearchRows);

                    // Link sub-header
                    if (linkItems.Count > 0)
                    {
                        string lName = linkItems[0].LinkName;
                        inner.Children.Add(MakeSubHeader($"── Link: {lName} ({linkItems.Count}) ──"));
                        AddItemRows(inner, linkItems, catKey, bg, ref rowIndex, groupSearchRows);
                    }
                }
                else
                {
                    // No links: classic layout, no sub-headers
                    AddItemRows(inner, hostItems, catKey, bg, ref rowIndex, groupSearchRows);
                }

                group.Content = inner;
                content.Children.Add(group);
                _searchGroups.Add((group, groupSearchRows));
            }

            // Req 8: record initial state for cancel confirmation
            foreach (var kvp in _combos)
                _initialSelections[kvp.Key] = kvp.Value.SelectedIndex;

            scroll.Content = content;
            root.Children.Add(scroll); // LastChildFill — added LAST

            Content = root;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Row-building helper
        // ─────────────────────────────────────────────────────────────────

        private void AddItemRows(StackPanel inner, List<TypeItem> items,
            string catKey, string bg, ref int rowIndex, List<RowSearchEntry> groupSearchRows)
        {
            int localIdx = 0;
            foreach (var item in items)
            {
                int capturedIdx = rowIndex;
                _indicesByCategory[catKey].Add(capturedIdx);

                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // Req 11: reduced column width 320→250
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

                // Type name
                string displayName = !string.IsNullOrEmpty(item.CurrentValue)
                    ? item.Name + "  *" : item.Name;

                // Req 7: tooltip with suggestion source
                string tooltip = !string.IsNullOrEmpty(item.CurrentValue)
                    ? $"{item.Name}\nCurrent: {item.CurrentValue}\nSuggestion: {item.Suggested} — {item.SuggestSource}"
                    : $"{item.Name}\nSuggestion: {item.Suggested} — {item.SuggestSource}";

                var tb = new TextBlock
                {
                    Text = displayName,
                    FontSize = 12, FontWeight = FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 4, 8, 4),
                    ToolTip = tooltip
                };
                Grid.SetColumn(tb, 0);
                row.Children.Add(tb);

                // ComboBox  — Req 11: MinWidth 300→220
                var cb = new WpfComboBox
                {
                    FontSize = 11, FontWeight = FontWeights.Normal,
                    Margin = new Thickness(0, 2, 4, 2), MinWidth = 220
                };

                var skipCi = new WpfComboBoxItem { Content = SkipLabel, Tag = null };
                cb.Items.Add(skipCi);

                var presets = EkahauPresets.GetPresetsForCategory(catKey);
                int selectedIdx = 0;
                for (int pi = 0; pi < presets.Count; pi++)
                {
                    string pk = presets[pi];
                    var ci = new WpfComboBoxItem
                    { Content = EkahauPresets.DisplayLabel(pk), Tag = pk };
                    cb.Items.Add(ci);
                    if (pk == item.Suggested) selectedIdx = pi + 1;
                }
                cb.SelectedIndex = selectedIdx;
                Grid.SetColumn(cb, 1);
                row.Children.Add(cb);

                _combos[capturedIdx]     = cb;
                _suggestions[capturedIdx] = item.Suggested;

                // Alternating background
                FrameworkElement rowContainer;
                if (localIdx % 2 == 0)
                {
                    var bdr = new Border
                    {
                        Background = Brush(bg),
                        CornerRadius = new CornerRadius(3),
                        Child = row
                    };
                    inner.Children.Add(bdr);
                    rowContainer = bdr;
                }
                else
                {
                    inner.Children.Add(row);
                    rowContainer = row;
                }

                // Req 6: track for search
                var entry = new RowSearchEntry
                {
                    Container = rowContainer,
                    TypeName = item.Name
                };
                _searchRows.Add(entry);
                groupSearchRows.Add(entry);

                localIdx++;
                rowIndex++;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Column header grid
        // ─────────────────────────────────────────────────────────────────

        private static Grid MakeColumnHeaderGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

            var l1 = new TextBlock
            {
                Text = "Revit Type Name", FontSize = 11,
                FontWeight = FontWeights.SemiBold, Foreground = Brush("#666666")
            };
            Grid.SetColumn(l1, 0); g.Children.Add(l1);

            var l2 = new TextBlock
            {
                Text = "Ekahau Type", FontSize = 11,
                FontWeight = FontWeights.SemiBold, Foreground = Brush("#666666")
            };
            Grid.SetColumn(l2, 1); g.Children.Add(l2);
            return g;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Req 3c — Subsection header
        // ─────────────────────────────────────────────────────────────────

        private static TextBlock MakeSubHeader(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#888888"),
            Margin = new Thickness(2, 8, 0, 2)
        };

        // ─────────────────────────────────────────────────────────────────
        //  Req 6 — Search / filter
        // ─────────────────────────────────────────────────────────────────

        private class RowSearchEntry
        {
            public FrameworkElement Container { get; set; }
            public string TypeName { get; set; }
        }

        private void FilterRows(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                foreach (var r in _searchRows)  r.Container.Visibility = Visibility.Visible;
                foreach (var g in _searchGroups) g.Group.Visibility = Visibility.Visible;
                return;
            }

            string lower = searchText.ToLowerInvariant();
            foreach (var (grp, grpRows) in _searchGroups)
            {
                bool anyMatch = false;
                foreach (var r in grpRows)
                {
                    bool match = r.TypeName.ToLowerInvariant().Contains(lower);
                    r.Container.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) anyMatch = true;
                }
                grp.Visibility = anyMatch ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Req 4 — Accept All Suggestions
        // ─────────────────────────────────────────────────────────────────

        private void OnAcceptAll(object sender, RoutedEventArgs e)
        {
            foreach (var kvp in _combos)
            {
                if (_suggestions.TryGetValue(kvp.Key, out string pk))
                    SetComboToPreset(kvp.Value, pk);
            }
        }

        private static void SetComboToPreset(WpfComboBox cb, string presetKey)
        {
            foreach (WpfComboBoxItem ci in cb.Items)
            {
                if (ci.Tag is string tag && tag == presetKey)
                {
                    cb.SelectedItem = ci;
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Button handlers
        // ─────────────────────────────────────────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            Result = new Dictionary<int, string>();
            foreach (var kvp in _combos)
            {
                if (kvp.Value.SelectedItem is WpfComboBoxItem ci && ci.Tag is string pk)
                    Result[kvp.Key] = pk;
            }
            DialogResult = true;
            Close();
        }

        /// <summary>Req 8: Confirm if user changed anything before closing.</summary>
        private void OnCancel(object sender, RoutedEventArgs e)
        {
            bool hasChanges = _combos.Any(kvp =>
                kvp.Value.SelectedIndex != _initialSelections.GetValueOrDefault(kvp.Key, -1));

            if (hasChanges)
            {
                var dlg = new TaskDialog("Discard Changes?");
                dlg.MainContent = "You have unsaved changes. Discard all changes and close?";
                dlg.CommonButtons =
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                if (dlg.Show() != TaskDialogResult.Yes)
                    return; // stay in dialog
            }

            Result = null;
            DialogResult = false;
            Close();
        }

        // ─────────────────────────────────────────────────────────────────
        //  Placeholder text helper (Req 6)
        // ─────────────────────────────────────────────────────────────────

        private static void ApplyPlaceholder(WpfTextBox tb, string placeholder)
        {
            tb.Foreground = Brush("#AAAAAA");
            tb.Text = placeholder;

            tb.GotFocus += (s, e) =>
            {
                if (tb.Text == placeholder && tb.Foreground.ToString() == Brush("#AAAAAA").ToString())
                {
                    tb.Text = "";
                    tb.Foreground = Brush("#000000");
                }
            };
            tb.LostFocus += (s, e) =>
            {
                if (string.IsNullOrEmpty(tb.Text))
                {
                    tb.Foreground = Brush("#AAAAAA");
                    tb.Text = placeholder;
                }
            };
            // Make sure TextChanged fires correctly by clearing placeholder before raising event
            tb.GotFocus += (s, e) =>
            {
                if (tb.Text == placeholder)
                    tb.Text = "";
            };
        }

        // ─────────────────────────────────────────────────────────────────
        //  Brush factory
        // ─────────────────────────────────────────────────────────────────

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // Small extension to avoid KeyNotFoundException
    internal static class DictExt
    {
        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> d, TKey key, TValue def = default)
            => d.TryGetValue(key, out var v) ? v : def;
    }
}
