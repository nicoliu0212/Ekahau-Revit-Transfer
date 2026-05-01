using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WPF control aliases — Autodesk.Revit.UI also defines ComboBox, TextBox, etc.
using WpfComboBox     = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfCheckBox     = System.Windows.Controls.CheckBox;
using WpfTextBox      = System.Windows.Controls.TextBox;
using WpfRadioButton  = System.Windows.Controls.RadioButton;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Read — Floor Plan Selector Dialog
    //  Lets the user pick which ESX floor plans to import.
    //  Shows AP count per floor for context.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadFloorSelectorDialog : Window
    {
        private readonly List<WpfCheckBox> _checks = new List<WpfCheckBox>();

        public List<int> SelectedIndices { get; private set; } = new List<int>();

        public EsxReadFloorSelectorDialog(
            string projectName,
            IList<string> floorPlanNames,
            IList<int> apCounts)
        {
            Title  = "ESX Read — Select Floor Plans to Import";
            Width  = 500;
            MaxHeight = 640;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Import from Ekahau Project",
                FontSize   = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = $"Project: {projectName}",
                FontSize   = 11,
                Foreground = Brush("#BBDEFB"),
                Margin     = new Thickness(16, 0, 16, 4)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = "Select which floor plans to import access points from.",
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
            var btnOk = MakeButton("  Import  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                SelectedIndices = _checks
                    .Select((cb, i) => (cb, i))
                    .Where(t => t.cb.IsChecked == true)
                    .Select(t => t.i)
                    .ToList();
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };
            btnOk.Margin = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Select All / Deselect All ────────────────────────────
            var selPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(12, 6, 12, 2)
            };
            var btnAll  = MakeSmallButton("Select All");
            btnAll.Click += (s, e) => _checks.ForEach(c => c.IsChecked = true);
            var btnNone = MakeSmallButton("Deselect All");
            btnNone.Click += (s, e) => _checks.ForEach(c => c.IsChecked = false);
            btnAll.Margin = new Thickness(0, 0, 6, 0);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            DockPanel.SetDock(selPanel, Dock.Top);
            root.Children.Add(selPanel);

            // ── Floor plan list ───────────────────────────────────────
            var listPanel = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };
            for (int i = 0; i < floorPlanNames.Count; i++)
            {
                int apCount = i < apCounts.Count ? apCounts[i] : 0;
                string label = $"{floorPlanNames[i]}  ({apCount} AP{(apCount != 1 ? "s" : "")})";
                var cb = new WpfCheckBox
                {
                    Content   = label,
                    FontSize  = 12,
                    IsChecked = apCount > 0, // Pre-select floors that have APs
                    Margin    = new Thickness(2, 2, 2, 2)
                };
                _checks.Add(cb);
                listPanel.Children.Add(cb);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
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
    //  ESX Read — View Match Dialog
    //  Match each ESX floor plan to a Revit floor plan view.
    //  Auto-matched entries are pre-selected; user can override.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadViewMatchDialog : Window
    {
        private readonly List<WpfComboBox> _combos = new List<WpfComboBox>();

        public List<int> MatchedViewIndices { get; private set; } = new List<int>();

        public EsxReadViewMatchDialog(
            IList<string> esxFloorNames,
            IList<string> revitViewNames,
            IList<int> autoMatchIndices)
        {
            Title  = "ESX Read — Match Floor Plans to Views";
            Width  = 600;
            Height = Math.Min(500, 200 + esxFloorNames.Count * 50);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            MinHeight = 300;

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = "Match Floor Plans to Revit Views",
                FontSize   = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text         = "For each ESX floor plan, select the matching Revit view.\n" +
                               "Auto-matched entries are pre-selected. Select \"(skip)\" to skip a floor.",
                FontSize     = 11,
                Foreground   = Brush("#BBDEFB"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(16, 0, 16, 10)
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
            var btnOk = MakeButton("  Continue  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                MatchedViewIndices = _combos.Select(cb =>
                {
                    if (cb.SelectedItem is WpfComboBoxItem item)
                        return (int)item.Tag;
                    return -1;
                }).ToList();
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };
            btnOk.Margin = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Matching grid ─────────────────────────────────────────
            var scrollContent = new StackPanel { Margin = new Thickness(12, 8, 12, 0) };

            // Column headers
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lblEsx = new TextBlock
            {
                Text       = "ESX Floor Plan",
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#555555"),
            };
            var lblRevit = new TextBlock
            {
                Text       = "Revit View",
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#555555"),
            };
            Grid.SetColumn(lblEsx, 0);
            Grid.SetColumn(lblRevit, 2);
            headerRow.Children.Add(lblEsx);
            headerRow.Children.Add(lblRevit);
            scrollContent.Children.Add(headerRow);

            // One row per ESX floor plan
            for (int i = 0; i < esxFloorNames.Count; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var nameBlock = new TextBlock
                {
                    Text              = esxFloorNames[i],
                    FontSize          = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                    ToolTip           = esxFloorNames[i],
                };

                var arrow = new TextBlock
                {
                    Text              = "\u2192",
                    FontSize          = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground        = Brush("#888888"),
                };

                var combo = new WpfComboBox { FontSize = 11 };
                // "(skip)" option
                combo.Items.Add(new WpfComboBoxItem
                {
                    Content = "(skip this floor)",
                    Tag     = -1,
                    FontStyle = FontStyles.Italic,
                    Foreground = Brush("#999999"),
                });
                for (int vi = 0; vi < revitViewNames.Count; vi++)
                {
                    combo.Items.Add(new WpfComboBoxItem
                    {
                        Content = revitViewNames[vi],
                        Tag     = vi,
                    });
                }

                // Select auto-match or (skip)
                int autoIdx = i < autoMatchIndices.Count ? autoMatchIndices[i] : -1;
                if (autoIdx >= 0 && autoIdx < revitViewNames.Count)
                {
                    combo.SelectedIndex = autoIdx + 1; // +1 for skip item
                    nameBlock.Foreground = Brush("#2E7D32"); // green = matched
                }
                else
                {
                    combo.SelectedIndex = 0; // (skip)
                    nameBlock.Foreground = Brush("#F57F17"); // yellow = unmatched
                }

                Grid.SetColumn(nameBlock, 0);
                Grid.SetColumn(arrow, 1);
                Grid.SetColumn(combo, 2);
                row.Children.Add(nameBlock);
                row.Children.Add(arrow);
                row.Children.Add(combo);
                scrollContent.Children.Add(row);
                _combos.Add(combo);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = scrollContent,
            };
            root.Children.Add(scroll);

            Content = root;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(12, 5, 12, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Read — AP Review Dialog
    //  Review access points before placement, with band and model info.
    //  User can toggle individual APs on/off.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadApReviewDialog : Window
    {
        private readonly List<(EsxAccessPointData Ap, WpfCheckBox Cb)> _rows
            = new List<(EsxAccessPointData, WpfCheckBox)>();

        public EsxReadApReviewDialog(string floorName, List<EsxAccessPointData> aps)
        {
            Title  = $"ESX Read — Review APs: {floorName}";
            Width  = 560;
            MaxHeight = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new DockPanel { LastChildFill = true };

            // ── Header ────────────────────────────────────────────────
            var hdr = new StackPanel { Background = Brush("#1976D2") };
            hdr.Children.Add(new TextBlock
            {
                Text       = $"Access Points on {floorName}",
                FontSize   = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                Margin     = new Thickness(16, 10, 16, 2)
            });
            hdr.Children.Add(new TextBlock
            {
                Text       = $"{aps.Count} access point(s) found. Uncheck any you want to skip.",
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
            var btnOk = MakeButton("  Place Markers  ", "#1976D2", "#FFFFFF");
            btnOk.Click += (s, e) =>
            {
                foreach (var (ap, cb) in _rows) ap.Include = cb.IsChecked == true;
                DialogResult = true;
            };
            var btnSkip = MakeButton("  Skip Floor  ", "#FF8F00", "#FFFFFF");
            btnSkip.Click += (s, e) =>
            {
                foreach (var (ap, _) in _rows) ap.Include = false;
                DialogResult = true;
            };
            var btnCancel = MakeButton("  Cancel  ", "#EEEEEE", "#333333");
            btnCancel.Click += (s, e) => { DialogResult = false; };
            btnOk.Margin   = new Thickness(0, 0, 8, 0);
            btnSkip.Margin = new Thickness(0, 0, 8, 0);
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnSkip);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            // ── Select All / None ─────────────────────────────────────
            var selPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(12, 6, 12, 2)
            };
            var btnAll  = MakeSmallButton("Select All");
            var btnNone = MakeSmallButton("Select None");
            btnAll.Click  += (s, e) => _rows.ForEach(r => r.Cb.IsChecked = true);
            btnNone.Click += (s, e) => _rows.ForEach(r => r.Cb.IsChecked = false);
            btnAll.Margin  = new Thickness(0, 0, 6, 0);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            DockPanel.SetDock(selPanel, Dock.Top);
            root.Children.Add(selPanel);

            // ── AP list ───────────────────────────────────────────────
            var listPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 0) };

            foreach (var ap in aps)
            {
                string bandStr = EsxMarkerOps.FormatBands(ap.Bands);
                var bandColor  = EsxMarkerOps.GetBandColor(ap.Bands);

                // Build row
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 2, 0, 2),
                };

                var cb = new WpfCheckBox
                {
                    IsChecked = ap.Include,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };

                // Band colour indicator
                var colorDot = new Border
                {
                    Width           = 12,
                    Height          = 12,
                    CornerRadius    = new CornerRadius(6),
                    Background      = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(bandColor.Red, bandColor.Green, bandColor.Blue)),
                    Margin          = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var nameBlock = new TextBlock
                {
                    Text              = ap.Name,
                    FontSize          = 12,
                    FontWeight        = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width             = 160,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };

                var bandBlock = new TextBlock
                {
                    Text              = bandStr,
                    FontSize          = 11,
                    Foreground        = Brush("#666666"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width             = 80,
                };

                string modelStr = "";
                if (!string.IsNullOrEmpty(ap.Vendor))
                    modelStr = ap.Vendor;
                if (!string.IsNullOrEmpty(ap.Model))
                    modelStr += (modelStr.Length > 0 ? " " : "") + ap.Model;

                var modelBlock = new TextBlock
                {
                    Text              = modelStr,
                    FontSize          = 10,
                    Foreground        = Brush("#999999"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };

                var heightBlock = new TextBlock
                {
                    Text              = $"H={ap.MountingHeight:F1}m",
                    FontSize          = 10,
                    Foreground        = Brush("#888888"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0),
                };

                rowPanel.Children.Add(cb);
                rowPanel.Children.Add(colorDot);
                rowPanel.Children.Add(nameBlock);
                rowPanel.Children.Add(bandBlock);
                rowPanel.Children.Add(modelBlock);
                rowPanel.Children.Add(heightBlock);

                rowPanel.ToolTip = $"{ap.Name}  |  {bandStr}  |  {modelStr}  |  H={ap.MountingHeight:F1}m";
                listPanel.Children.Add(rowPanel);
                _rows.Add((ap, cb));
            }

            var scroll = new ScrollViewer
            {
                MaxHeight = 350,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel,
            };
            root.Children.Add(scroll);

            Content = root;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(12, 5, 12, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static Button MakeSmallButton(string text)
            => new Button
            {
                Content = text, FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Read — Progress Window (modeless)
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadProgressWindow : Window
    {
        private readonly TextBlock _status;
        private readonly TextBlock _detail;

        public EsxReadProgressWindow()
        {
            Title  = "ESX Read — Working...";
            Width  = 400;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost    = true;

            var root = new StackPanel { Margin = new Thickness(20) };
            _status = new TextBlock
            {
                Text         = "Initialising...",
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
    //  ESX Read — Summary Dialog
    //  Shows import results with per-floor details.
    // ═══════════════════════════════════════════════════════════════════════

    public class EsxReadSummaryDialog : Window
    {
        public EsxReadSummaryDialog(
            string projectName, string esxPath,
            List<EsxReadFloorResult> floorResults,
            int totalPlaced, int totalSkipped,
            string stagingDir)
        {
            Title  = "ESX Read — Import Complete";
            Width  = 520;
            MaxHeight = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FFFFFF");

            var root = new StackPanel { Margin = new Thickness(20) };

            // ── Header ────────────────────────────────────────────────
            bool anyPlaced = totalPlaced > 0;
            root.Children.Add(new TextBlock
            {
                Text       = anyPlaced ? "Import Successful" : "Import Complete (No APs Placed)",
                FontSize   = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(anyPlaced ? "#2E7D32" : "#F57F17"),
                Margin     = new Thickness(0, 0, 0, 10)
            });

            // ── Summary stats ─────────────────────────────────────────
            void AddRow(string label, string value)
            {
                var p = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 2, 0, 2)
                };
                p.Children.Add(new TextBlock
                {
                    Text       = label,
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Width      = 180,
                });
                p.Children.Add(new TextBlock { Text = value, FontSize = 12 });
                root.Children.Add(p);
            }

            AddRow("Project:", projectName);
            AddRow("APs placed:", totalPlaced.ToString());
            if (totalSkipped > 0)
                AddRow("APs skipped:", totalSkipped.ToString());
            AddRow("Floors processed:",
                floorResults.Count(r => r.ApsPlaced > 0).ToString() +
                " of " + floorResults.Count);

            root.Children.Add(new TextBlock
            {
                Text       = esxPath,
                FontSize   = 10,
                Foreground = Brush("#999999"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 6, 0, 8)
            });

            // ── Per-floor details ─────────────────────────────────────
            if (floorResults.Count > 0)
            {
                var gb = new GroupBox
                {
                    Header  = "Floor Details",
                    Margin  = new Thickness(0, 4, 0, 8),
                    Padding = new Thickness(6),
                };
                var floorPanel = new StackPanel();
                foreach (var fr in floorResults)
                {
                    var fp = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin      = new Thickness(0, 2, 0, 2)
                    };
                    fp.Children.Add(new TextBlock
                    {
                        Text  = fr.FloorPlanName,
                        Width = 150,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    fp.Children.Add(new TextBlock
                    {
                        Text  = $"\u2192 {fr.MatchedViewName}",
                        Width = 150,
                        FontSize = 11,
                        Foreground = Brush("#555555"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    fp.Children.Add(new TextBlock
                    {
                        Text     = $"{fr.ApsPlaced} AP{(fr.ApsPlaced != 1 ? "s" : "")}",
                        FontSize = 11,
                        Foreground = Brush(fr.ApsPlaced > 0 ? "#2E7D32" : "#999999"),
                    });
                    if (!string.IsNullOrEmpty(fr.Warning))
                    {
                        fp.ToolTip = fr.Warning;
                        fp.Children.Add(new TextBlock
                        {
                            Text       = " (!)",
                            FontSize   = 11,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brush("#F57F17"),
                        });
                    }
                    floorPanel.Children.Add(fp);
                }
                gb.Content = floorPanel;
                root.Children.Add(gb);
            }

            // ── Buttons ───────────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 4, 0, 0)
            };

            if (!string.IsNullOrEmpty(stagingDir) && System.IO.Directory.Exists(stagingDir))
            {
                var btnStaging = MakeButton("  Open Staging Folder  ", "#EEEEEE", "#333333");
                btnStaging.Click += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", stagingDir); }
                    catch { }
                };
                btnStaging.Margin = new Thickness(0, 0, 8, 0);
                btnPanel.Children.Add(btnStaging);
            }

            var btnClose = MakeButton("  Close  ", "#1976D2", "#FFFFFF");
            btnClose.Click += (s, e) => DialogResult = true;
            btnPanel.Children.Add(btnClose);
            root.Children.Add(btnPanel);

            Content = root;
        }

        private static Button MakeButton(string text, string bg, string fg)
            => new Button
            {
                Content    = text, FontSize = 12,
                Padding    = new Thickness(12, 5, 12, 5),
                Background = Brush(bg), Foreground = Brush(fg),
                BorderThickness = new Thickness(0),
            };

        private static SolidColorBrush Brush(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

}
