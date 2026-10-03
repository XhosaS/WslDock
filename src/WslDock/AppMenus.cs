using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace WslDock;

// Dock and tray share the same flyout template and live theme resources.
public static class AppMenus
{
    internal static ContextMenu? Current { get; private set; }
    private static Window? focusHost;

    private static Window ActivateHost()
    {
        if (focusHost == null)
        {
            // A desktop child cannot own keyboard focus for a top-level flyout.
            // This transparent 1 pixel owner is visible only while a menu is open.
            focusHost = new Window { Title = "WslDock menu host", Width = 1, Height = 1,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0 };
            focusHost.Closed += (_, _) => focusHost = null;
        }
        focusHost.Show();
        var point = System.Windows.Forms.Cursor.Position;
        Native.PositionMenuHost(new WindowInteropHelper(focusHost).Handle, point.X, point.Y);
        focusHost.Activate();
        return focusHost;
    }

    private static ContextMenu Create(string name)
    {
        var menu = new ContextMenu { StaysOpen = false };
        System.Windows.Automation.AutomationProperties.SetName(menu, name);
        menu.Opened += (_, _) =>
        {
            if (Current != menu) Current?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            Current = menu;
        };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { menu.IsOpen = false; e.Handled = true; }
        };
        menu.Closed += (_, _) =>
        {
            if (Current == menu) { Current = null; focusHost?.Hide(); }
        };
        return menu;
    }

    private static MenuItem Command(string text, string glyph, Action action, double iconSize = 14)
    {
        var item = new MenuItem { Header = text, Icon = Ui.Glyph(glyph, iconSize) };
        item.Click += (_, _) => action();
        return item;
    }

    public static ContextMenu Dock()
    {
        var menu = Create("WslDock 菜单");
        menu.Items.Add(Command("设置", "\uE713", App.Current.OpenSettings));
        menu.Items.Add(Command("退出 WslDock", "\uE8BB", () => App.Current.Shutdown(), 11));
        return menu;
    }

    public static ContextMenu Tray()
    {
        var menu = Create("WslDock 托盘菜单");
        menu.Items.Add(Command("显示 WslDock", "\uE737", () => { App.Current.Dock.Show(); App.Current.Dock.EnsureDesktop(); }));
        menu.Items.Add(Command("设置", "\uE713", App.Current.OpenSettings));
        var shutdown = Command("关闭 WSL", "\uE7E8", async () => await App.Current.ShutdownWslAsync());
        menu.Items.Add(shutdown);
        menu.Opened += (_, _) => shutdown.IsEnabled = !App.Current.Wsl.IsShuttingDown;
        menu.Items.Add(new Separator { Style = (Style)App.Current.FindResource("MenuSeparator") });
        menu.Items.Add(Command("退出 WslDock", "\uE8BB", () => App.Current.Shutdown(), 11));
        return menu;
    }

    public static void ShowAtPointer(ContextMenu menu)
    {
        Close();
        menu.PlacementTarget = null;
        menu.Placement = PlacementMode.MousePoint;
        Open(menu);
    }

    public static void Open(ContextMenu menu)
    {
        menu.PlacementRectangle = Rect.Empty;
        menu.PlacementTarget = ActivateHost();
        menu.IsOpen = true;
        menu.Focus();
    }

    public static void Close() { if (Current != null) Current.IsOpen = false; }
}
