using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WslDock;

public sealed class DockWindow : Window
{
    internal WslStatusIndicator StatusLight { get; } = new();
    private readonly StackPanel icons = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<string, Ellipse> dots = new();
    private readonly Dictionary<string, DateTime> launching = new();
    private readonly HashSet<string> running = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private List<AppWindow> windows = new();
    private DateTime lastPoll = DateTime.MinValue;
    private bool polling;
    private readonly DesktopHost desktop;
    private bool dragging;
    private bool attachmentWarning;
    public bool DesktopAttached => desktop.Attached;
    public bool HasCompositedSurface => desktop.HasCompositedSurface;
    private double DockScale => Native.GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;

    public DockWindow()
    {
        desktop = new DesktopHost(this);
        Style = (Style)App.Current.FindResource(typeof(Window));
        Title = "WslDock"; Height = 66; SizeToContent = SizeToContent.Width;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false;
        // A normal child has no composited surface under Win11's no-redirection Progman.
        // WPF's per-pixel layered surface remains visible there and retains hit testing.
        AllowsTransparency = true; Background = Brushes.Transparent;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        var grip = new Border { Width = 12, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "拖动 WslDock" };
        var gripContents = new Grid { Height = 50 };
        StatusLight.VerticalAlignment = VerticalAlignment.Top;
        StatusLight.Margin = new Thickness(0, 6, 0, 0);
        gripContents.Children.Add(StatusLight);
        gripContents.Children.Add(new Border { Width = 3, Height = 18, Background = Ui.Brush("#B6B6B6"), CornerRadius = new CornerRadius(2) });
        grip.Child = gripContents;
        grip.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1 && desktop.Attached) { AppMenus.Close(); dragging = true; desktop.BeginDrag(); grip.CaptureMouse(); e.Handled = true; } };
        grip.MouseMove += (_, _) => { if (dragging) desktop.Drag(); };
        grip.MouseLeftButtonUp += (_, _) => { if (dragging) { dragging = false; grip.ReleaseMouseCapture(); SavePosition(); } };
        grip.LostMouseCapture += (_, _) => { dragging = false; };
        panel.Children.Add(grip); panel.Children.Add(icons);
        panel.Children.Add(new Border { Width = 1, Height = 26, Margin = new Thickness(7, 0, 7, 0), Background = Ui.Brush("#D0D0D0") });
        var settings = Ui.Button("", App.Current.OpenSettings, true); settings.Content = Ui.Glyph("\uE713", 19); settings.Width = 36; settings.Height = 40; settings.ToolTip = "设置";
        System.Windows.Automation.AutomationProperties.SetName(settings, "设置");
        panel.Children.Add(settings);
        Content = new Border { Background = Ui.Brush("#F3F3F3"), BorderBrush = Ui.Brush("#E0E0E0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = panel };
        var dockMenu = AppMenus.Dock();
        grip.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; AppMenus.ShowAtPointer(dockMenu); };
        SourceInitialized += (_, _) => Native.Style(this, true);
        // Let WPF finish resizing the HWND before clamping its final physical bounds.
        // The per-pixel surface already supplies rounded corners; no Win32 region is needed.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => desktop.ClampToWorkArea()));
        Loaded += async (_, _) => { RenderApps(); Place(); EnsureDesktop(); timer.Start(); await PollProcesses(); };
        timer.Tick += async (_, _) => { EnsureDesktop(); RefreshWindows(); await PollProcesses(); };
        Closed += (_, _) => { timer.Stop(); AppMenus.Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) AppMenus.Close(); };
    }
    public void EnsureDesktop()
    {
        if (desktop.Attached) return;
        try { desktop.Attach(); Opacity = 1; attachmentWarning = false; }
        catch (Exception ex)
        {
            Opacity = 0;
            if (!attachmentWarning) { Config.Log("Desktop attachment: " + ex.Message); attachmentWarning = true; }
        }
    }
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = App.Current.Prefs.Left ?? area.Left + (area.Width - ActualWidth) / 2;
        Top = App.Current.Prefs.Top ?? area.Bottom - Height - 24;
        ClampPosition();
    }
    private void ClampPosition()
    {
        // Work areas from Forms are physical pixels; convert using this window's current DPI.
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var scale = Math.Max(1, DockScale); var area = screen.WorkingArea;
        Left = Math.Clamp(Left, area.Left / scale, Math.Max(area.Left / scale, area.Right / scale - ActualWidth));
        Top = Math.Clamp(Top, area.Top / scale, Math.Max(area.Top / scale, area.Bottom / scale - ActualHeight));
    }
    private void SavePosition()
    {
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
        var scale = Math.Max(1, DockScale);
        App.Current.Prefs.Left = rect.Left / scale; App.Current.Prefs.Top = rect.Top / scale; Config.Save(App.Current.Prefs);
    }
    public void RenderApps()
    {
        AppMenus.Close();
        icons.Children.Clear(); dots.Clear();
        foreach (var app in App.Current.Prefs.Apps.Where(a => a.Visible))
        {
            var grid = new Grid { Width = 46, Height = 50 };
            var icon = Ui.Icon(app); icon.Margin = new Thickness(0, 0, 0, 6); grid.Children.Add(icon);
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Ui.Brush("#656565"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Hidden };
            grid.Children.Add(dot); dots[app.Id] = dot;
            var button = Ui.Button("", () => _ = OpenApp(app), true); button.Content = grid; button.Padding = new Thickness(0); button.ToolTip = app.Name + " · " + app.Distro;
            System.Windows.Automation.AutomationProperties.SetName(button, app.Name);
            // Application icons only activate on primary click; do not open a context menu.
            ContextMenuService.SetIsEnabled(button, false);
            button.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
            icons.Children.Add(button);
        }
        if (icons.Children.Count == 0) icons.Children.Add(Ui.Button("添加应用", App.Current.OpenSettings, true));
        RefreshWindows();
    }
    public List<AppWindow> ForApp(DockApp app) => windows.Where(w => Native.Matches(app, w) && App.Current.Prefs.Apps.Count(a => Native.Matches(a, w)) == 1).ToList();
    private void RefreshWindows()
    {
        windows = Native.Windows();
        Native.RepairWslgIcons(windows, App.Current.Prefs.Apps);
        foreach (var app in App.Current.Prefs.Apps)
        {
            var matched = ForApp(app);
            if (matched.Count > 0) launching.Remove(app.Id);
            if (dots.TryGetValue(app.Id, out var dot))
            {
                dot.Visibility = matched.Count > 0 || running.Contains(app.Id) ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
    private async Task PollProcesses()
    {
        if (App.Current.Wsl.IsShuttingDown || polling || (DateTime.UtcNow - lastPoll).TotalSeconds < 5) return;
        polling = true; lastPoll = DateTime.UtcNow;
        try
        {
            List<string> active;
            try
            {
                // Listing running distributions does not start any stopped distribution.
                active = await App.Current.Wsl.DistrosAsync(runningOnly: true, includeSystem: true);
                StatusLight.SetRunning(active.Count > 0);
            }
            catch
            {
                StatusLight.SetRunning(null);
                throw;
            }
            Native.RetainWslgDistros(active);
            var next = new HashSet<string>();
            if (App.Current.Wsl.BackgroundPaused) { running.Clear(); return; }
            foreach (var group in App.Current.Prefs.Apps.GroupBy(a => a.Distro).Where(g => active.Contains(g.Key)))
            {
                var value = await App.Current.Wsl.RequestAsync(group.Key, new { action = "status", ids = group.Select(a => a.Id).ToArray() }, background: true);
                Native.UpdateWslgIdentities(group.Key, value.GetProperty("windowApps"));
                if (await App.Current.Wsl.RefreshIconsAsync(group.Key, group)) App.Current.Save();
                foreach (var item in value.GetProperty("running").EnumerateArray()) next.Add(item.GetString()!);
            }
            running.Clear(); running.UnionWith(next);
        }
        catch (Exception ex) { Config.Log("Process status: " + ex.Message); }
        finally { polling = false; }
    }
    public async Task OpenApp(DockApp app)
    {
        if (App.Current.Wsl.IsShuttingDown) { ShowStatus("正在关闭 WSL，请稍后重试。"); return; }
        AppMenus.Close(); RefreshWindows();
        var targets = ForApp(app);
        if (targets.Count > 0) { if (!Native.Restore(targets[0])) ShowStatus("Windows 暂未允许切换焦点，请再点击一次。"); return; }
        if (launching.TryGetValue(app.Id, out var started) && (DateTime.UtcNow - started).TotalSeconds < 20) { ShowStatus(app.Name + " 正在启动…"); return; }
        launching[app.Id] = DateTime.UtcNow;
        try
        {
            ShowStatus("正在打开 " + app.Name + "…");
            await App.Current.Wsl.LaunchAsync(app);
            // A successful launcher exit isn't proof of a visible GUI. Allow WSLg time to register it.
            await Task.Delay(1500); RefreshWindows();
            if (ForApp(app).Count == 0) ShowStatus("启动请求已发送；若窗口未出现，请查看设置中的应用日志。");
        }
        catch (Exception ex) { launching.Remove(app.Id); ShowStatus(app.Name + " 启动失败"); MessageBox.Show(ex.Message, "WslDock · 启动失败", MessageBoxButton.OK, MessageBoxImage.Warning); Config.Log(ex.ToString()); }
    }
    public void ClearWslState()
    {
        running.Clear(); launching.Clear(); Native.RetainWslgDistros(Array.Empty<string>());
        lastPoll = DateTime.MinValue; StatusLight.SetRunning(false); RefreshWindows();
    }
    public void ShowStatus(string message)
    {
        // A short tooltip leaves the dock itself compact.
        var tip = new ToolTip { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }, PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, IsOpen = true, StaysOpen = true };
        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        close.Tick += (_, _) => { tip.IsOpen = false; close.Stop(); }; close.Start();
    }
}
