using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GerberHandler;

internal static class LayerStyleEditor
{
    internal static bool TryColor(string text, out Color color)
    {
        text = text.Trim();
        color = default;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static Dictionary<string, string>? Show(Window owner, IReadOnlyList<string> roles, IReadOnlyDictionary<string, string> current)
    {
        var form = new StackPanel { Margin = new Thickness(16) };
        var dialog = new Window { Title = "레이어 색상", Owner = owner, Width = 450, Height = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        form.Children.Add(new TextBlock { Text = "색을 #RRGGBB 형식으로 입력하세요.", Margin = new Thickness(0, 0, 0, 8) });
        var edits = new Dictionary<string, TextBox>();
        foreach (var role in roles)
        {
            var row = new DockPanel();
            var reset = new Button { Content = "기본값", Margin = new Thickness(6, 0, 0, 0) };
            var entry = new TextBox { Width = 84, Text = current.GetValueOrDefault(role) ?? PanelView.DefaultLayerHex(role) };
            var swatch = new Border { Width = 19, Height = 19, Margin = new Thickness(4, 0, 6, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            foreach (var element in new UIElement[] { reset, entry, swatch })
            {
                DockPanel.SetDock(element, Dock.Right);
                row.Children.Add(element);
            }
            row.Children.Add(new TextBlock { Text = role, VerticalAlignment = VerticalAlignment.Center });
            form.Children.Add(row);
            edits.Add(role, entry);
            entry.TextChanged += (_, _) => Preview();
            reset.Click += (_, _) => entry.Text = PanelView.DefaultLayerHex(role);
            Preview();
            void Preview() => swatch.Background = TryColor(entry.Text, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
        }
        var error = new TextBlock { Foreground = Brushes.DarkRed };
        actions.Children.Add(error);
        var save = new Button { Content = "저장", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(save);
        Dictionary<string, string>? result = null;
        save.Click += (_, _) =>
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (role, entry) in edits)
            {
                if (!TryColor(entry.Text, out var color))
                {
                    error.Text = $"{role} 색상은 #RRGGBB 형식이어야 합니다.";
                    entry.Focus();
                    return;
                }
                var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                if (hex != PanelView.DefaultLayerHex(role))
                    settings.Add(role, hex);
            }
            result = settings;
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
        return result;
    }
}
