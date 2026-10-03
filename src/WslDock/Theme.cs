using Microsoft.Win32;
using System.Windows.Data;
using System.Windows.Interop;

namespace WslDock;

// Shared, mutable brushes update existing windows without rebuilding their edited controls.
public static class Theme
{
    private static readonly Dictionary<string, string> dark = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#F3F3F3"] = "#202020", ["#F5F5F5"] = "#252525", ["#EEEEEE"] = "#1C1C1C",
        ["#FBFBFB"] = "#2B2B2B", ["#FCFCFC"] = "#2C2C2C", ["#FDFDFD"] = "#343434", ["#FEFEFE"] = "#333333",
        ["#FFFFFF"] = "#383838", ["#E8E8E8"] = "#262626", ["#EAEAEA"] = "#393939", ["#E1E1E1"] = "#353535",
        ["#E6EFF7"] = "#243C50", ["#EAECEF"] = "#191919", ["#242424"] = "#F2F2F2", ["#1C1C1C"] = "#F2F2F2",
        ["#666666"] = "#BFBFBF", ["#656565"] = "#D0D0D0", ["#777777"] = "#B8B8B8", ["#757575"] = "#B8B8B8",
        ["#797979"] = "#B8B8B8", ["#7B7B7B"] = "#AEAEAE", ["#888888"] = "#ACACAC", ["#B6B6B6"] = "#777777",
        ["#D0D0D0"] = "#484848", ["#CFCFCF"] = "#4B4B4B", ["#E0E0E0"] = "#474747", ["#E2E2E2"] = "#3B3B3B",
        ["#D5D5D5"] = "#515151", ["#D6D6D6"] = "#565656", ["#DADADA"] = "#474747", ["#DDDDDD"] = "#494949",
        ["#0F7B0F"] = "#6CCB5F", ["#0067C0"] = "#60CDFF", ["#B3261E"] = "#FFB4AB"
    };
    private static readonly Dictionary<string, SolidColorBrush> brushes = new(StringComparer.OrdinalIgnoreCase);
    private sealed class ColorToken : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(Color), typeof(ColorToken));
        public Color Value { get => (Color)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    }
    private static readonly Dictionary<string, ColorToken> tokens = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsDark { get; private set; }
    public static SolidColorBrush Brush(string light)
    {
        if (!brushes.TryGetValue(light, out var brush))
        {
            var token = new ColorToken { Value = ColorOf(IsDark && dark.TryGetValue(light, out var value) ? value : light) };
            tokens[light] = token; brush = new SolidColorBrush();
            // A binding prevents WPF's resource/style optimizer from freezing this brush.
            BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(ColorToken.Value)) { Source = token });
            brushes[light] = brush;
        }
        return brush;
    }
    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
    public static void Apply(string mode)
    {
        bool systemDark = false;
        try { systemDark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) { Config.Log("Read system theme: " + ex.Message); }
        IsDark = mode == "dark" || mode == "system" && systemDark;
        foreach (var pair in tokens) pair.Value.Value = ColorOf(IsDark && dark.TryGetValue(pair.Key, out var value) ? value : pair.Key);
        foreach (var key in dark.Keys) App.Current.Resources["Color" + key[1..]] = Brush(key);
        App.Current.Resources["OnAccentBrush"] = IsDark ? Brushes.Black : Brushes.White;
        foreach (Window window in App.Current.Windows)
            if (new WindowInteropHelper(window).Handle != 0) Native.Style(window, window is DockWindow);
    }
}
