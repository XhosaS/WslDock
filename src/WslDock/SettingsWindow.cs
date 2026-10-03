using System.Diagnostics;
using System.Windows.Interop;

namespace WslDock;

public sealed class SettingsWindow : Window
{
    private readonly StartupRegistration startup;
    private CheckBox? startupToggle;
    private bool updatingStartup;
    private readonly StackPanel body = new();
    private readonly TextBlock notice = Ui.Text("", 12, "#666666");
    private readonly Button appsNav;
    private readonly Button scaleNav;
    private bool scalePage;
    private TextBlock? displayTitle;
    private TextBlock? displayScale;
    private string selectedDistro = "";
    private List<string> distros = new();

    public SettingsWindow() : this(new StartupRegistration()) { }
    internal SettingsWindow(StartupRegistration startup)
    {
        this.startup = startup;
        Style = (Style)App.Current.FindResource(typeof(Window));
        Icon = Branding.AppIcon;
        Title = "WslDock 设置"; Width = 1040; Height = 760; MinWidth = 820; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(12, 24, 12, 16) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 26) };
        brand.Children.Add(new Image { Source = Branding.AppIcon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        var brandName = Ui.Text("WslDock", 19); brandName.FontWeight = FontWeights.SemiBold; brand.Children.Add(brandName);
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var foot = Ui.Text("WSL 应用 · v" + typeof(App).Assembly.GetName().Version!.ToString(3), 12, "#7B7B7B"); foot.Margin = new Thickness(14, 0, 0, 0); DockPanel.SetDock(foot, Dock.Bottom); sidebar.Children.Add(foot);
        var nav = new StackPanel();
        appsNav = Nav("\uE80A", "配置应用", false); scaleNav = Nav("\uE7F4", "显示设置", true); nav.Children.Add(appsNav); nav.Children.Add(scaleNav); sidebar.Children.Add(nav);
        grid.Children.Add(new Border { Background = Ui.Brush("#EEEEEE"), Child = sidebar });
        var right = new DockPanel { Margin = new Thickness(32, 24, 32, 24) }; Grid.SetColumn(right, 1);
        notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 14, 0, 0); DockPanel.SetDock(notice, Dock.Bottom); right.Children.Add(notice);
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); grid.Children.Add(right); Content = grid;
        SourceInitialized += (_, _) => Native.Style(this);
        DpiChanged += (_, _) => UpdateDisplayInfo();
        Activated += (_, _) => RefreshStartupToggle();
        Loaded += async (_, _) => { Render(); try { distros = await App.Current.Wsl.DistrosAsync(); selectedDistro = distros.FirstOrDefault() ?? ""; Render(); } catch (Exception ex) { SetNotice(ex.Message, true); } };
    }
    private Button Nav(string glyph, string text, bool scale)
    {
        var b = Ui.Button("", () => { scalePage = scale; Render(); }, true); b.Height = 42; b.Margin = new Thickness(0, 0, 0, 4); b.HorizontalContentAlignment = HorizontalAlignment.Left;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Width = 156 }; var icon = Ui.Glyph(glyph, 17); icon.Margin = new Thickness(0, 0, 14, 0); row.Children.Add(icon); row.Children.Add(Ui.Text(text)); b.Content = row; return b;
    }
    private void SetNotice(string message, bool error = false) { notice.Text = message; notice.Foreground = Ui.Brush(error ? "#B3261E" : "#666666"); }
    private void Render()
    {
        body.Children.Clear(); startupToggle = null;
        appsNav.Background = Ui.Brush(scalePage ? "#EEEEEE" : "#E1E1E1"); scaleNav.Background = Ui.Brush(scalePage ? "#E1E1E1" : "#EEEEEE");
        var heading = Ui.Text(scalePage ? "显示设置" : "配置应用", 28); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(heading);
        var intro = Ui.Text(scalePage ? "调整界面外观、应用大小与显示状态。" : "选择在桌面 Dock 中显示的 WSL 应用。", 13, "#666666"); intro.Margin = new Thickness(0, 0, 0, 26); body.Children.Add(intro);
        if (scalePage) RenderScale(); else RenderApps();
    }
    private void RenderApps()
    {
        RenderStartup();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var refresh = Ui.Button("重新发现应用", () => _ = RefreshApps()); DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh);
        var select = new ComboBox { ItemsSource = distros, SelectedItem = selectedDistro, Width = 190, HorizontalAlignment = HorizontalAlignment.Left };
        select.SelectionChanged += (_, _) => selectedDistro = select.SelectedItem as string ?? ""; toolbar.Children.Add(select); body.Children.Add(toolbar);
        Label("我的应用");
        foreach (var app in App.Current.Prefs.Apps)
        {
            var stack = new StackPanel();
            var row = AppRow(app, out var details);
            var toggle = new CheckBox { Style = (Style)App.Current.FindResource("Toggle"), IsChecked = app.Visible, VerticalAlignment = VerticalAlignment.Center, ToolTip = "在 Dock 中显示" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "在 Dock 中显示 " + app.Name);
            toggle.Checked += (_, _) => { app.Visible = true; Save(); }; toggle.Unchecked += (_, _) => { app.Visible = false; Save(); };
            var edit = new StackPanel { Margin = new Thickness(50, 18, 0, 0), Visibility = Visibility.Collapsed };
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rowActions.Children.Add(toggle);
            var expand = Ui.Button("", () => { edit.Visibility = edit.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed; }, true);
            expand.Content = Ui.Glyph("\uE70D", 11); expand.Width = 26; expand.Height = 30; expand.Margin = new Thickness(12, 0, 0, 0); expand.ToolTip = "启动配置";
            rowActions.Children.Add(expand); Grid.SetColumn(rowActions, 2); row.Children.Add(rowActions); stack.Children.Add(row);
            var name = Field(edit, "显示名称", app.Name); var command = Field(edit, "启动命令", app.Command);
            var source = Ui.Text(app.Distro + " · " + app.DesktopId, 11, "#888888"); source.TextWrapping = TextWrapping.Wrap; source.Margin = new Thickness(0, 0, 0, 10); edit.Children.Add(source);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Ui.Button("保存", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(command.Text)) { SetNotice("名称与启动命令不能为空。", true); return; }
                if (app.Command != command.Text.Trim()) { app.ScaleProfile = DetectProfile(command.Text); app.BoundAppId = ""; }
                app.Name = name.Text.Trim(); app.Command = command.Text.Trim(); Save(); Render();
            }));
            var log = Ui.Button("查看启动日志", () => _ = ReadLog(app), true); log.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(log); edit.Children.Add(actions);
            stack.Children.Add(edit); body.Children.Add(Ui.Card(stack));
        }
        if (App.Current.Prefs.Apps.Count == 0) body.Children.Add(Ui.Card(Ui.Text("选择 Ubuntu，然后点击“重新发现应用”。", 13, "#666666")));
        Note("隐藏图标不会关闭应用。启动命令按参数解析；管道、重定向和 shell 脚本请放在你自己的可执行包装器中。");
    }
    private void RenderStartup()
    {
        var row = new DockPanel();
        startupToggle = new CheckBox { Style = (Style)App.Current.FindResource("Toggle"), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(startupToggle, "开机自启动");
        DockPanel.SetDock(startupToggle, Dock.Right); row.Children.Add(startupToggle);
        var labels = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        labels.Children.Add(Ui.Text("开机自启动"));
        var caption = Ui.Text("登录 Windows 时自动启动 WslDock", 12, "#777777"); caption.Margin = new Thickness(0, 5, 0, 0); labels.Children.Add(caption);
        row.Children.Add(labels);
        var card = Ui.Card(row); card.Margin = new Thickness(0, 0, 0, 20); body.Children.Add(card);
        RefreshStartupToggle();
        startupToggle.Checked += (_, _) => ChangeStartup(true);
        startupToggle.Unchecked += (_, _) => ChangeStartup(false);
    }
    private void RefreshStartupToggle()
    {
        if (startupToggle == null) return;
        updatingStartup = true;
        try { startupToggle.IsChecked = startup.IsEnabled(); startupToggle.IsEnabled = true; }
        catch (Exception ex) { startupToggle.IsEnabled = false; SetNotice("无法读取自启动设置：" + ex.Message, true); }
        finally { updatingStartup = false; }
    }
    private void ChangeStartup(bool enabled)
    {
        if (updatingStartup) return;
        try { startup.SetEnabled(enabled); SetNotice(enabled ? "已开启开机自启动" : "已关闭开机自启动"); }
        catch (Exception ex) { RefreshStartupToggle(); SetNotice("无法修改自启动设置：" + ex.Message, true); Config.Log(ex.ToString()); }
    }
    private static string DetectProfile(string command)
    {
        var c = command.ToLowerInvariant();
        if (c.Contains("alacritty")) return "alacritty";
        if (new[] { "google-chrome", "chromium", "codex-pinyin", "chatgpt", "electron" }.Any(c.Contains)) return "chromium";
        return "none";
    }
    private async Task RefreshApps()
    {
        if (selectedDistro.Length == 0) { SetNotice("请选择一个 WSL 发行版。", true); return; }
        SetNotice("正在读取 " + selectedDistro + " 中的应用…");
        try { await App.Current.Discover(selectedDistro); Render(); SetNotice("应用列表已更新。你的显示选择和启动配置已保留。"); }
        catch (Exception ex) { SetNotice(ex.Message, true); Config.Log(ex.ToString()); }
    }
    public void ShowDisplayPage() { scalePage = true; Render(); }
    private void RenderScale()
    {
        Label("外观");
        var themeRow = new DockPanel();
        var choice = new ComboBox { Width = 168, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, VerticalAlignment = VerticalAlignment.Center };
        var modes = new[] { "system", "light", "dark" }; choice.SelectedIndex = Math.Max(0, Array.IndexOf(modes, App.Current.Prefs.ThemeMode));
        System.Windows.Automation.AutomationProperties.SetName(choice, "夜间模式");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            App.Current.Prefs.ThemeMode = modes[choice.SelectedIndex]; Theme.Apply(App.Current.Prefs.ThemeMode);
            Save(); SetNotice("外观已保存，Dock、菜单和设置同步生效。");
        };
        DockPanel.SetDock(choice, Dock.Right); themeRow.Children.Add(choice);
        var themeText = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        themeText.Children.Add(Ui.Text("夜间模式"));
        var caption = Ui.Text("选择 WslDock 的浅色或深色外观", 12, "#777777"); caption.Margin = new Thickness(0, 5, 0, 0); themeText.Children.Add(caption);
        themeRow.Children.Add(themeText); body.Children.Add(Ui.Card(themeRow));
        Label("显示与缩放");
        var monitor = new StackPanel(); displayTitle = Ui.Text("", 14); monitor.Children.Add(displayTitle);
        displayScale = Ui.Text("", 12, "#666666"); displayScale.Margin = new Thickness(0, 5, 0, 0); monitor.Children.Add(displayScale); body.Children.Add(Ui.Card(monitor));
        UpdateDisplayInfo();
        Label("按应用调整");
        foreach (var app in App.Current.Prefs.Apps.Where(a => a.Visible))
        {
            var row = AppRow(app, out var info);
            info.Text = app.CanScale ? (app.ScaleProfile == "alacritty" ? "Alacritty · 原生 X11 渲染" : "Chromium / Electron · 原生界面缩放") : "尚未适配 · 保留应用自身的缩放";
            info.TextWrapping = TextWrapping.Wrap;
            var values = new[] { 0, 100, 125, 150, 175, 200, 250, 300 };
            var select = new ComboBox { Width = 168, IsEnabled = app.CanScale, VerticalAlignment = VerticalAlignment.Center };
            foreach (var value in values) select.Items.Add(value == 0 ? "跟随 Dock 显示器" : value + "%");
            select.SelectedIndex = Array.IndexOf(values, app.ScalePercent); if (select.SelectedIndex < 0) select.SelectedIndex = 0;
            select.SelectionChanged += (_, _) => { app.ScalePercent = values[select.SelectedIndex]; Save(); SetNotice(app.Name + " 的缩放已保存，下次完整启动时生效。现有窗口不会被关闭。"); };
            System.Windows.Automation.AutomationProperties.SetName(select, app.Name + " 缩放");
            Grid.SetColumn(select, 2); row.Children.Add(select); body.Children.Add(Ui.Card(row));
        }
        Note("缩放只作用于从 WslDock 新启动的应用，不修改 Ubuntu 或 WSLg 的全局缩放。选择“跟随”时，读取启动瞬间 Dock 所在显示器的比例。");
        Note("已运行的 Chrome / Codex 可能复用原进程。请先在应用中保存工作并完整退出，再从 Dock 打开以应用新的比例。跨屏移动后不会强制重启应用。");
        Label("WSLg 显示状态");
        var healthPanel = new StackPanel();
        var summary = Ui.Text("正在检查显示状态…", 13); summary.TextWrapping = TextWrapping.Wrap;
        var detail = Ui.Text("", 12, "#777777"); detail.TextWrapping = TextWrapping.Wrap; detail.LineHeight = 20; detail.Margin = new Thickness(0, 8, 0, 10);
        var retry = Ui.Button("重新检查", () => { }); retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Click += async (_, _) => await CheckDisplay(summary, detail, retry);
        healthPanel.Children.Add(summary); healthPanel.Children.Add(detail); healthPanel.Children.Add(retry); body.Children.Add(Ui.Card(healthPanel));
        _ = CheckDisplay(summary, detail, retry);
    }
    private async Task CheckDisplay(TextBlock summary, TextBlock detail, Button retry)
    {
        retry.IsEnabled = false; summary.Text = "正在检查 WSLg…";
        try
        {
            var active = await App.Current.Wsl.DistrosAsync(true);
            var names = App.Current.Prefs.Apps.Where(a => a.Visible).Select(a => a.Distro).Distinct().Where(active.Contains).ToArray();
            if (names.Length == 0) { summary.Text = "WSL 尚未运行"; detail.Text = "打开应用后可检查显示状态。"; return; }
            var states = new List<string>(); bool broken = false;
            foreach (var distro in names)
            {
                var health = await App.Current.Wsl.DisplayHealthAsync(distro, background: true);
                states.Add(distro + " · " + health.Message); broken |= health.Broken;
            }
            summary.Text = string.Join("\n", states); summary.Foreground = Ui.Brush(broken ? "#B3261E" : "#242424");
            detail.Text = broken ? DisplayHealth.Recovery : "此检查识别已知日志故障。窗口是否可见、能否响应操作仍需实际确认。";
        }
        catch (Exception ex) { summary.Text = "显示检查未完成"; detail.Text = ex.Message; }
        finally { retry.IsEnabled = true; }
    }
    private void UpdateDisplayInfo()
    {
        if (displayTitle == null || displayScale == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd); var dpi = Native.GetDpiForWindow(hwnd);
        displayTitle.Text = $"当前显示器 · {screen.Bounds.Width} × {screen.Bounds.Height}";
        displayScale.Text = $"Windows 缩放 {Math.Round(dpi / 96.0 * 100)}% · WslDock 随显示器自动适配";
    }
    private static Grid AppRow(DockApp app, out TextBlock details)
    {
        var row = new Grid { MinHeight = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Icon(app); icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) }; labels.Children.Add(Ui.Text(app.Name));
        details = Ui.Text(app.Distro, 12, "#777777"); details.Margin = new Thickness(0, 4, 0, 0); labels.Children.Add(details); Grid.SetColumn(labels, 1); row.Children.Add(labels); return row;
    }
    private static TextBox Field(StackPanel panel, string label, string value)
    {
        var caption = Ui.Text(label, 12, "#666666"); caption.Margin = new Thickness(0, 0, 0, 5); panel.Children.Add(caption);
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(input); return input;
    }
    private void Label(string text) { var label = Ui.Text(text, 13); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 19, 0, 12); body.Children.Add(label); }
    private void Note(string text) { var note = Ui.Text(text, 12, "#777777"); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 14, 0, 0); note.LineHeight = 20; body.Children.Add(note); }
    private void Save() { try { App.Current.Save(); SetNotice("已保存"); } catch (Exception ex) { SetNotice("无法保存设置：" + ex.Message, true); } }
    private async Task ReadLog(DockApp app)
    {
        try
        {
            var result = await App.Current.Wsl.RequestAsync(app.Distro, new { action = "log", id = app.Id });
            var text = new TextBox { Text = result.GetProperty("text").GetString(), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
            new Window { Title = app.Name + " · 启动日志", Width = 760, Height = 460, Owner = this, Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
        }
        catch (Exception ex) { SetNotice(ex.Message, true); }
    }
}
