using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WPF control aliases — Autodesk.Revit.UI also defines ComboBox, TextBox, etc.
using WpfComboBox     = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfTextBox      = System.Windows.Controls.TextBox;
using WpfCheckBox     = System.Windows.Controls.CheckBox;
using WpfRadioButton  = System.Windows.Controls.RadioButton;
using WpfListBox      = System.Windows.Controls.ListBox;
using WpfListBoxItem  = System.Windows.Controls.ListBoxItem;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Req 10 — View Selector Dialog
    //  Lets the user pick which floor-plan views to export and whether to
    //  merge all floors into a single .esx or create one .esx per floor.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxViewSelectorDialog : Window
    {
        private readonly List<WpfCheckBox> _checks = new List<WpfCheckBox>();
        private WpfRadioButton _rbMerge;
        private WpfRadioButton _rbSeparate;

        public List<int> SelectedIndices { get; private set; } = new List<int>();
        public ExportMode ExportMode { get; private set; } = ExportMode.MergeAll;

        public EsxViewSelectorDialog(IList<string> viewNames)
        {
            Title  = "ESX Export — Select Floor Plan Views";
            Width  = 480;
            MaxHeight = 640;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────────
            var hdr = new StackPanel
            {
                Background = Brush("#1976D2"),
                Margin     = new Thickness(0)
            };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Select Floor Plan Views",
                FontSize   = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Only views with an active crop box are listed.",
                FontSize   = 11,
                Foreground = Brush("#BBDEFB"),
                Margin     = new Thickness(16, 0, 16, 10)
            });
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Buttons (bottom) ──────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(12, 8, 12, 10)
            };
            var btnOk = MakeButton("  Export  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                SelectedIndices = _checks
                    .Select((cb, i) => (cb, i))
                    .Where(t => t.cb.IsChecked == true)
                    .Select(t => t.i)
                    .ToList();
                ExportMode = _rbSeparate?.IsChecked == true ? ExportMode.Separate : ExportMode.MergeAll;
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };

            btnOk.Margin     = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);

            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Export mode (Req 10: only shown when >1 view available) ──
            GroupBox modeBox = null;
            if (viewNames.Count > 1)
            {
                modeBox = new GroupBox
                {
                    Header  = "Export mode",
                    Margin  = new Thickness(12, 8, 12, 4),
                    Padding = new Thickness(6)
                };
                var modePanel = new StackPanel();
                _rbMerge = new WpfRadioButton
                {
                    Content   = "Merge all selected floors into one .esx file",
                    IsChecked = true,
                    Margin    = new Thickness(0, 0, 0, 4)
                };
                _rbSeparate = new WpfRadioButton
                {
                    Content = "Create a separate .esx file for each floor"
                };
                modePanel.Children.Add(_rbMerge);
                modePanel.Children.Add(_rbSeparate);
                modeBox.Content = modePanel;
                DockPanel.SetDock(modeBox, Dock.Bottom);
                root.Children.Add(modeBox);

            }

            // ── Select All / Deselect All ────────────────────────────────
            var selPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(12, 6, 12, 2)
            };
            var btnAll = MakeSmallButton("Select All");
            btnAll.Click += (s, e) => _checks.ForEach(c => c.IsChecked = true);
            var btnNone = MakeSmallButton("Deselect All");
            btnNone.Click += (s, e) => _checks.ForEach(c => c.IsChecked = false);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            btnAll.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(selPanel, Dock.Top);
            root.Children.Add(selPanel);

            // ── View list ─────────────────────────────────────────────────
            var listPanel = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };
            for (int i = 0; i < viewNames.Count; i++)
            {
                var cb = new WpfCheckBox
                {
                    Content   = viewNames[i],
                    FontSize  = 12,
                    IsChecked = true,
                    Margin    = new Thickness(2, 2, 2, 2)
                };
                _checks.Add(cb);
                listPanel.Children.Add(cb);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
                Margin  = new Thickness(0)
            };
            root.Children.Add(scroll);

            // Req 10: Wire up check events to show/hide export-mode box
            // (must be done after _checks list is populated)
            if (modeBox != null)
            {
                var mb = modeBox; // capture for closures
                void UpdateModeVis()
                {
                    int n = _checks.Count(c => c.IsChecked == true);
                    mb.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                foreach (var cb in _checks)
                {
                    cb.Checked   += (s2, e2) => UpdateModeVis();
                    cb.Unchecked += (s2, e2) => UpdateModeVis();
                }
            }

            Content = root;
        }

        private static Button MakeButton(string text, string bg, string fg)
        {
            return new Button
            {
                Content    = text,
                FontSize   = 12,
                Padding    = new Thickness(14, 5, 14, 5),
                Background = Brush(bg),
                Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };
        }

        private static Button MakeSmallButton(string text)
        {
            return new Button
            {
                Content = text,
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
            };
        }

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 14 — Resolution Selector Dialog
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxResolutionDialog : Window
    {
        private WpfRadioButton _r2000, _r4000, _r8000, _r10000, _r15000;

        public int SelectedResolution { get; private set; } = 4000;

        public EsxResolutionDialog()
        {
            Title  = "ESX Export — Image Resolution";
            Width  = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text        = "Select the longest image dimension for the exported floor plan:",
                FontSize    = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 12)
            });

            var rowMargin = new Thickness(0, 0, 0, 4);
            _r2000  = new WpfRadioButton { Content =  "2 000 px   (draft — fast export)",                    FontSize = 12, Margin = rowMargin };
            _r4000  = new WpfRadioButton { Content =  "4 000 px   (standard — recommended)",                 FontSize = 12, Margin = rowMargin, IsChecked = true };
            _r8000  = new WpfRadioButton { Content =  "8 000 px   (high quality)",                           FontSize = 12, Margin = rowMargin };
            _r10000 = new WpfRadioButton { Content = "10 000 px   (very high quality — slower)",             FontSize = 12, Margin = rowMargin };
            _r15000 = new WpfRadioButton { Content = "15 000 px   (maximum quality — large file)",           FontSize = 12, Margin = rowMargin };

            root.Children.Add(_r2000);
            root.Children.Add(_r4000);
            root.Children.Add(_r8000);
            root.Children.Add(_r10000);
            root.Children.Add(_r15000);

            var note = new TextBlock
            {
                Text        = "Larger images improve wall precision but increase file size and " +
                              "export time.  Default 4 000 px is suitable for most projects.",
                FontSize    = 11,
                Foreground  = (SolidColorBrush)new BrushConverter().ConvertFromString("#777777"),
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(0, 8, 0, 16)
            };
            root.Children.Add(note);

            var btnOk = new Button
            {
                Content  = "  OK  ",
                FontSize = 12,
                Padding  = new Thickness(20, 5, 20, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            btnOk.Click += (s, e) =>
            {
                if      (_r2000.IsChecked  == true) SelectedResolution =  2000;
                else if (_r8000.IsChecked  == true) SelectedResolution =  8000;
                else if (_r10000.IsChecked == true) SelectedResolution = 10000;
                else if (_r15000.IsChecked == true) SelectedResolution = 15000;
                else                                SelectedResolution =  4000;
                DialogResult = true;
            };
            root.Children.Add(btnOk);

            Content = root;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 11 — Mapping Review Dialog
    //  Shows all unique wall/door/window types found in a view, grouped by
    //  resolution source (Parameter → Keyword → Fallback).
    //  Fallback rows are highlighted yellow.  All preset ComboBoxes are
    //  editable.  Buttons: Export / Skip View / Cancel All.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxMappingReviewDialog : Window
    {
        private readonly List<MappingEntry> _entries;
        private readonly Dictionary<string, WpfComboBox> _combos
            = new Dictionary<string, WpfComboBox>();

        public MappingReviewResult Result { get; private set; } = new MappingReviewResult();

        public EsxMappingReviewDialog(string viewName, List<MappingEntry> entries)
        {
            _entries = entries;

            Title  = $"ESX Export — Mapping Review: {viewName}";
            Width  = 600;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            MinHeight = 400;

            var root = new DockPanel { LastChildFill = true };

            // ── Header ───────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Review Wall Type Presets",
                FontSize   = 14, FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Adjust the Ekahau material preset for any type below, then click Export.",
                FontSize   = 11, Foreground = Brush("#BBDEFB"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(16, 0, 16, 10)
            });
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);

            // ── Buttons (bottom) ──────────────────────────────────────────
            var btnPanel = new WrapPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(12, 8, 12, 10)
            };

            var btnExport = MakeButton("  Export  ", "#1976D2", "#FFFFFF");
            btnExport.Click += (s, e) =>
            {
                Result = BuildResult(MappingReviewAction.Export);
                DialogResult = true;
            };
            var btnSkip = MakeButton("  Skip View  ", "#FF8F00", "#FFFFFF");
            btnSkip.Click += (s, e) =>
            {
                Result = new MappingReviewResult { Action = MappingReviewAction.SkipView };
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel All  ", "#C62828", "#FFFFFF");
            btnCancel.Click += (s, e) =>
            {
                Result = new MappingReviewResult { Action = MappingReviewAction.CancelAll };
                DialogResult = true;
            };

            btnExport.Margin = new Thickness(0, 0, 8, 0);
            btnSkip.Margin   = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnExport);
            btnPanel.Children.Add(btnSkip);
            btnPanel.Children.Add(btnCancel);

            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Scrollable content ────────────────────────────────────────
            var scrollContent = new StackPanel { Margin = new Thickness(8, 8, 8, 0) };

            var groups = new[]
            {
                ("Parameter",  "#E8F5E9", "#2E7D32", entries.Where(e => e.Source == "Parameter").ToList()),
                ("Keyword",    "#E3F2FD", "#1565C0", entries.Where(e => e.Source == "Keyword").ToList()),
                ("Fallback",   "#FFF9C4", "#F57F17", entries.Where(e => e.Source == "Fallback").ToList()),
            };

            foreach (var (src, bg, accent, groupEntries) in groups)
            {
                if (groupEntries.Count == 0) continue;

                var gb = new GroupBox
                {
                    Margin  = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(4),
                    Background = Brush(bg),
                };

                var hdrPanel = new StackPanel { Orientation = Orientation.Horizontal };
                hdrPanel.Children.Add(new TextBlock
                {
                    Text       = src,
                    FontSize   = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush(accent),
                });
                hdrPanel.Children.Add(new TextBlock
                {
                    Text       = $"  ({groupEntries.Count})",
                    FontSize   = 11,
                    Foreground = Brush(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                gb.Header = hdrPanel;

                var rows = new StackPanel();
                foreach (var entry in groupEntries)
                {
                    rows.Children.Add(BuildEntryRow(entry, bg));
                }
                gb.Content = rows;
                scrollContent.Children.Add(gb);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = scrollContent
            };
            root.Children.Add(scroll);

            Content = root;
        }

        private FrameworkElement BuildEntryRow(MappingEntry entry, string bgHex)
        {
            var row = new Grid { Margin = new Thickness(2, 3, 2, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

            // Category badge
            string catLabel = entry.Category switch
            {
                "door"   => "DOOR",
                "window" => "WIN",
                _        => "WALL"
            };
            string catColor = entry.Category switch
            {
                "door"   => "#795548",
                "window" => "#0097A7",
                _        => "#546E7A"
            };

            var catBadge = new Border
            {
                Background        = Brush(catColor),
                CornerRadius      = new CornerRadius(2),
                Padding           = new Thickness(4, 1, 4, 1),
                Margin            = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = catLabel,
                    FontSize   = 9,
                    Foreground = Brush("#FFFFFF"),
                    FontWeight = FontWeights.Bold,
                }
            };

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(catBadge);
            var nameBlock = new TextBlock
            {
                Text              = entry.TypeName,
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            if (entry.IsLinked)
                nameBlock.Foreground = Brush("#5C6BC0");
            if (entry.Source == "Fallback")
                nameBlock.FontStyle = FontStyles.Italic;
            namePanel.Children.Add(nameBlock);
            namePanel.ToolTip = entry.TypeName + (entry.IsLinked ? " [Linked]" : "");

            var srcBlock = new TextBlock
            {
                Text              = entry.IsLinked ? "Linked" : entry.Source,
                FontSize          = 10,
                Foreground        = Brush("#888888"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0)
            };

            var combo = new WpfComboBox { FontSize = 11 };
            foreach (var key in EkahauPresets.All.Keys)
                combo.Items.Add(new WpfComboBoxItem { Content = EkahauPresets.DisplayLabel(key), Tag = key });

            SetComboToPreset(combo, entry.InitialPreset);
            _combos[entry.TypeUniqueId] = combo;

            Grid.SetColumn(namePanel, 0);
            Grid.SetColumn(srcBlock,  1);
            Grid.SetColumn(combo,     2);

            row.Children.Add(namePanel);
            row.Children.Add(srcBlock);
            row.Children.Add(combo);

            // If fallback: yellow background already comes from GroupBox; add warning icon
            if (entry.Source == "Fallback")
            {
                row.ToolTip = "No matching parameter or keyword found — using category default.";
            }

            return row;
        }

        private static void SetComboToPreset(WpfComboBox combo, string presetKey)
        {
            foreach (WpfComboBoxItem item in combo.Items)
            {
                if ((string)item.Tag == presetKey)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private MappingReviewResult BuildResult(MappingReviewAction action)
        {
            var overrides = new Dictionary<string, string>();
            foreach (var entry in _entries)
            {
                if (!_combos.TryGetValue(entry.TypeUniqueId, out var combo)) continue;
                if (combo.SelectedItem is WpfComboBoxItem item)
                {
                    string chosen = (string)item.Tag;
                    if (chosen != entry.InitialPreset)
                        overrides[entry.TypeUniqueId] = chosen;
                }
            }
            return new MappingReviewResult { Action = action, Overrides = overrides };
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content = text, FontSize = 12,
                Padding = new Thickness(12, 5, 12, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 20 — AP Confirmation Dialog
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxApConfirmDialog : Window
    {
        private readonly List<(ApCandidate Ap, WpfCheckBox Cb)> _rows
            = new List<(ApCandidate, WpfCheckBox)>();

        public bool SkipAps { get; private set; } = false;

        public EsxApConfirmDialog(List<ApCandidate> candidates)
        {
            Title  = "ESX Export — Access Points Found";
            Width  = 480;
            MaxHeight = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text         = $"{candidates.Count} access point(s) detected. Select which to include:",
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8)
            });

            // Select All / None
            var selPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var btnAll  = SmallBtn("Select All");
            var btnNone = SmallBtn("Select None");
            btnAll.Click  += (s, e) => _rows.ForEach(r => r.Cb.IsChecked = true);
            btnNone.Click += (s, e) => _rows.ForEach(r => r.Cb.IsChecked = false);
            btnAll.Margin = new Thickness(0, 0, 6, 0);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            root.Children.Add(selPanel);

            var listPanel = new StackPanel();
            foreach (var ap in candidates)
            {
                var cb = new WpfCheckBox
                {
                    Content   = $"{ap.Name}  (H={ap.HeightMeters:F1} m)",
                    FontSize  = 12,
                    IsChecked = true,
                    Margin    = new Thickness(0, 2, 0, 2)
                };
                _rows.Add((ap, cb));
                listPanel.Children.Add(cb);
            }
            var scroll = new ScrollViewer
            {
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
                Margin  = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(scroll);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOk   = new Button { Content = "  Include Selected  ", FontSize = 12, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            var btnSkip = new Button { Content = "  Skip APs  ",         FontSize = 12, Padding = new Thickness(12, 5, 12, 5) };
            btnOk.Click += (s, e) =>
            {
                foreach (var (ap, cb) in _rows) ap.Include = cb.IsChecked == true;
                DialogResult = true;
            };
            btnSkip.Click += (s, e) => { SkipAps = true; DialogResult = true; };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnSkip);
            root.Children.Add(btnPanel);

            Content = root;
        }

        private static Button SmallBtn(string text)
            => new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 12 — Progress Window (modeless, updated via DoEvents)
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxProgressWindow : Window
    {
        private readonly TextBlock _status;
        private readonly TextBlock _detail;

        public EsxProgressWindow()
        {
            Title  = "ESX Export — Working…";
            Width  = 400;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost    = true;

            var root = new StackPanel { Margin = new Thickness(20) };

            _status = new TextBlock
            {
                Text         = "Initialising…",
                FontSize     = 13,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6)
            };
            _detail = new TextBlock
            {
                Text         = "",
                FontSize     = 11,
                Foreground   = (SolidColorBrush)new BrushConverter().ConvertFromString("#666666"),
                TextWrapping = TextWrapping.Wrap,
            };

            root.Children.Add(_status);
            root.Children.Add(_detail);
            Content = root;
        }

        public void Update(string status, string detail = "")
        {
            _status.Text = status;
            _detail.Text = detail ?? "";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 13 — Summary Dialog (with Open Output Folder button)
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxSummaryDialog : Window
    {
        public EsxSummaryDialog(
            int viewCount, int wallSegCount, int apCount,
            string outputPath, bool hasDebugLog, string debugLogPath)
        {
            Title  = "ESX Export — Complete";
            Width  = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text       = "✔  Export Successful",
                FontSize   = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#2E7D32"),
                Margin     = new Thickness(0, 0, 0, 10)
            });

            void AddRow(string label, string value)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                p.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 150 });
                p.Children.Add(new TextBlock { Text = value, FontSize = 12 });
                root.Children.Add(p);
            }

            AddRow("Floors exported:",   viewCount.ToString());
            AddRow("Wall segments:",      wallSegCount.ToString());
            AddRow("Access points:",      apCount.ToString());

            root.Children.Add(new TextBlock
            {
                Text       = outputPath,
                FontSize   = 11,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#555555"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 8, 0, 12)
            });

            var btnPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };

            // Req 13: "Open Output Folder" button
            var btnFolder = new Button
            {
                Content = "  Open Output Folder  ",
                FontSize = 12,
                Padding  = new Thickness(12, 5, 12, 5),
                Margin   = new Thickness(0, 0, 8, 0)
            };
            btnFolder.Click += (s, e) =>
            {
                try
                {
                    string folder = System.IO.Path.GetDirectoryName(outputPath);
                    System.Diagnostics.Process.Start("explorer.exe", folder);
                }
                catch { }
            };

            if (hasDebugLog)
            {
                var btnLog = new Button
                {
                    Content = "  View Debug Log  ",
                    FontSize = 12,
                    Padding  = new Thickness(12, 5, 12, 5),
                    Margin   = new Thickness(0, 0, 8, 0)
                };
                btnLog.Click += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start("notepad.exe", debugLogPath); }
                    catch { }
                };
                btnPanel.Children.Add(btnLog);
            }

            var btnClose = new Button { Content = "  Close  ", FontSize = 12, Padding = new Thickness(12, 5, 12, 5) };
            btnClose.Click += (s, e) => DialogResult = true;

            btnPanel.Children.Add(btnFolder);
            btnPanel.Children.Add(btnClose);
            root.Children.Add(btnPanel);

            Content = root;
        }
    }
}
