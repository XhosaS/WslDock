using System.Windows.Media.Imaging;

namespace WslDock;

public static class Ui
{
    public static SolidColorBrush Brush(string hex) => Theme.Brush(hex);
    public static TextBlock Text(string value, double size = 14, string color = "#242424") => new() { Text = value, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Glyph(string value, double size = 17) => new() { Text = value, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    public static Button Button(string text, Action action, bool quiet = false)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        if (quiet) b.Style = (Style)App.Current.FindResource("QuietButton");
        b.Click += (_, _) => action(); return b;
    }
    public static FrameworkElement Icon(DockApp app, double size = 32)
    {
        if (!string.IsNullOrEmpty(app.IconPng))
        {
            try
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(Convert.FromBase64String(app.IconPng)); image.EndInit(); image.Freeze();
                return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            }
            catch (Exception ex) { Config.Log("Icon: " + ex.Message); }
        }
        var terminal = app.IsTerminal;
        var fallback = terminal ? Ui.Text(">_", size * .52, "#FFFFFF") : Ui.Glyph(app.Name == "Codex" ? "\uE943" : "\uE8A5", size * .62);
        if (terminal) fallback.Foreground = Brushes.White;
        if (terminal) fallback.Foreground = Brushes.White;
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), Background = Brush(terminal ? "#20272E" : "#FDFDFD"), BorderBrush = Brush("#CFCFCF"), BorderThickness = new Thickness(1), Child = fallback };
    }
    public static Border Card(UIElement child) => new() { Background = Brush("#FBFBFB"), BorderBrush = Brush("#E2E2E2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 4), Child = child };
}
