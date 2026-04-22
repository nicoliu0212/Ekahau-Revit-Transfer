using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Req 3a: Simple WPF dialog for selecting a linked model to include in
    /// wall-type configuration. The user may also choose "Host model only".
    /// </summary>
    public class LinkedModelSelectorDialog : Window
    {
        private readonly ListBox _listBox;
        public int SelectedIndex { get; private set; } = -1; // -1 = host only

        public LinkedModelSelectorDialog(List<string> linkNames)
        {
            Title = "Select Linked Model for Wall Configuration";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFFFFF");

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "Choose a linked model to include its wall types in the\n" +
                       "configuration dialog, or select \"Host model only\".",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#444444"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            _listBox = new ListBox
            {
                FontSize = 12,
                MaxHeight = 300,
                SelectionMode = SelectionMode.Single,
                Margin = new Thickness(0, 0, 0, 12)
            };

            // First item: host only
            _listBox.Items.Add(new ListBoxItem
            {
                Content = "(None — host model only)",
                FontStyle = FontStyles.Italic,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#888888")
            });

            foreach (string name in linkNames)
                _listBox.Items.Add(new ListBoxItem { Content = name });

            _listBox.SelectedIndex = 0; // default to host only
            root.Children.Add(_listBox);

            // Buttons
            var bp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnOk = new Button
            {
                Content = "  OK  ",
                FontSize = 12,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnOk.Click += (s, e) =>
            {
                int idx = _listBox.SelectedIndex;
                SelectedIndex = idx <= 0 ? -1 : idx - 1; // -1 = host only; ≥0 = link index
                DialogResult = true;
                Close();
            };

            var btnCancel = new Button
            {
                Content = "  Cancel  ",
                FontSize = 12,
                Padding = new Thickness(14, 5, 14, 5)
            };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };

            bp.Children.Add(btnOk);
            bp.Children.Add(btnCancel);
            root.Children.Add(bp);

            Content = root;
        }
    }
}
