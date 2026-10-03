using Microsoft.Win32;

namespace WslDock;

public partial class App : System.Windows.Application
{
    private SingleInstance? instance;
    private System.Windows.Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private (bool Light, int Size)? trayAppearance;
    public static new App Current => (App)System.Windows.Application.Current;
    public Preferences Prefs { get; internal set; } = new();
    public WslService Wsl { get; } = new();
    public DockWindow Dock { get; internal set; } = null!;
    private SettingsWindow? settings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply("system");
        if (e.Args.Length > 0 && e.Args[0] == "--diagnose")
        {
            await Diagnostics.RunAsync(e.Args.Skip(1).FirstOrDefault());
            Shutdown(); return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--smoke-test")
        {
            var passed = await SmokeTest.RunAsync(e.Args.Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "WslDock-smoke"));
            Shutdown(passed ? 0 : 1); return;
        }
        instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            if (!e.Args.Contains("--startup") && !await instance.ActivateAsync(e.Args.Contains("--settings")))
                Config.Log("Existing WslDock instance did not respond to activation.");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Config.Log(args.Exception.ToString());
            MessageBox.Show(args.Exception.Message, "WslDock", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        Prefs = Config.Load();
        Theme.Apply(Prefs.ThemeMode);
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        SystemEvents.DisplaySettingsChanged += SystemDisplayChanged;
        Dock = new DockWindow();
        MainWindow = Dock;
        Dock.Show();
        instance.Listen(showSettings => Dispatcher.BeginInvoke(new Action(() =>
        {
            Dock.Show(); Dock.EnsureDesktop();
            if (showSettings || settings != null) OpenSettings();
        })));
        if (e.Args.Contains("--settings")) OpenSettings();
        var menu = AppMenus.Tray();
        tray = new System.Windows.Forms.NotifyIcon { Text = "WslDock", Visible = true };
        UpdateTrayIcon();
        tray.MouseUp += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Right)
                Dispatcher.Invoke(() => AppMenus.ShowAtPointer(menu));
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Dock.Show(); Dock.EnsureDesktop(); });
        if (Config.LoadWarning != null) Dock.ShowStatus(Config.LoadWarning);
        if (Prefs.Apps.Count == 0 && !e.Args.Contains("--startup"))
        {
            Dock.ShowStatus("正在查找 WSL 应用…");
            try
            {
                var distros = await Wsl.DistrosAsync();
                if (distros.Count == 0) throw new InvalidOperationException("未找到 WSL 发行版，请先安装带 WSLg 的 WSL 2。");
                await Discover(distros[0]);
                Dock.ShowStatus("已找到应用。点击图标打开应用。");
            }
            catch (Exception ex) { Dock.ShowStatus("发现应用失败，请打开设置重试。" ); Config.Log(ex.ToString()); OpenSettings(); }
        }
    }

    public async Task Discover(string distro)
    {
        var found = await Wsl.DiscoverAsync(distro);
        foreach (var app in found)
        {
            var existing = Prefs.Apps.FirstOrDefault(a => a.Distro == distro && a.DesktopId == app.DesktopId);
            if (existing != null) { existing.IconPng = app.IconPng; continue; }
            app.Visible = app.Name == "Codex" || app.Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || app.ScaleProfile == "alacritty";
            if (app.ScaleProfile == "alacritty") app.Name = "终端";
            Prefs.Apps.Add(app);
        }
        Prefs.Apps = Prefs.Apps.OrderBy(a => a.Name == "Codex" ? 0 : a.Name.Contains("Chrome") ? 1 : a.ScaleProfile == "alacritty" ? 2 : 3).ToList();
        Save();
    }
    public void Save() { Config.Save(Prefs); Dock?.RenderApps(); }
    public void OpenSettings()
    {
        if (settings == null) { settings = new SettingsWindow(); settings.Closed += (_, _) => settings = null; }
        settings.Show(); if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal; settings.Activate();
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (Prefs.ThemeMode == "system") Theme.Apply("system"); UpdateTrayIcon(); });
    }
    private void SystemDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateTrayIcon);
    private void UpdateTrayIcon()
    {
        if (tray == null) return;
        // Taskbar color is a Windows shell preference, independent of the Dock's theme.
        var appearance = (Branding.IsTaskbarLight, Math.Clamp(System.Windows.Forms.SystemInformation.SmallIconSize.Width, 16, 64));
        if (trayAppearance == appearance) return;
        var next = Branding.TrayIcon(appearance.Item1, appearance.Item2);
        var previous = trayIcon;
        tray.Icon = next; trayIcon = next; trayAppearance = appearance;
        previous?.Dispose();
    }
    public void OpenDisplaySettings() { OpenSettings(); settings!.ShowDisplayPage(); }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        SystemEvents.DisplaySettingsChanged -= SystemDisplayChanged;
        AppMenus.Close(); tray?.Dispose(); trayIcon?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
