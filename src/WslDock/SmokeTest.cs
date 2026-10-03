using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace WslDock;

// Explicit opt-in integration test. Controls only a newly created, uniquely titled test terminal.
public static class SmokeTest
{
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Native.RectI rect);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    private static bool DockIsNotClipped()
    {
        var dock = App.Current.Dock; var hwnd = new WindowInteropHelper(dock).Handle;
        Native.GetWindowRect(hwnd, out var rect); var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (Math.Abs(rect.Right - rect.Left - dock.ActualWidth * scale) > 1) return false;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(hwnd, region) == 0) return true;
            GetRgnBox(region, out var clip);
            return clip.Left <= 0 && clip.Top <= 0 && clip.Right >= rect.Right - rect.Left && clip.Bottom >= rect.Bottom - rect.Top;
        }
        finally { DeleteObject(region); }
    }
    private static async Task<bool> Until(Func<bool> test, int milliseconds = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { if (test()) return true; await Task.Delay(200); } while (DateTime.UtcNow < until);
        return false;
    }
    private static void Capture(FrameworkElement window, string file)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); png.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    public static async Task<bool> RunAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var checks = new Dictionary<string, object?>();
        AppWindow? owned = null; SettingsWindow? settings = null;
        var startupKey = @"Software\WslDock\Tests\" + Guid.NewGuid().ToString("N");
        var fixtureDir = Path.Combine(folder, "startup fixture " + Guid.NewGuid().ToString("N"));
        var fixtureExe = Path.Combine(fixtureDir, "WslDock.exe");
        var originalStartup = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + StartupRegistration.RunKey, "WslDock", null);
        try
        {
            checks["applicationIconLoaded"] = Branding.AppIcon is BitmapSource { PixelWidth: 512, PixelHeight: 512 };
            foreach (var lightTaskbar in new[] { false, true })
                foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64 })
                {
                    using var icon = Branding.TrayIcon(lightTaskbar, size);
                    checks[$"trayIcon-{lightTaskbar}-{size}"] = icon.Width == size && icon.Height == size;
                }
            var wsl = new WslService(); var distros = await wsl.DistrosAsync(); checks["distros"] = distros;
            var apps = await wsl.DiscoverAsync(distros.First());
            foreach (var a in apps) a.Visible = a.Name == "Codex" || a.Name.Contains("Chrome") || a.ScaleProfile == "alacritty";
            App.Current.Prefs = new Preferences { Apps = apps };
            App.Current.Dock = new DockWindow(); App.Current.Dock.Show();
            await Task.Delay(500); Capture(App.Current.Dock, Path.Combine(folder, "dock.png"));
            Directory.CreateDirectory(fixtureDir); File.WriteAllText(fixtureExe, "Isolated startup registration fixture; never executed.");
            var startup = new StartupRegistration(fixtureExe, startupKey);
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupKey)) key.SetValue("Unrelated", "preserved");
            settings = new SettingsWindow(startup); settings.Show(); await Task.Delay(800);
            var startupSwitch = Descendants<CheckBox>(settings).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "开机自启动");
            checks["startupDefaultsOff"] = startupSwitch.IsChecked == false;
            startupSwitch.IsChecked = true;
            checks["startupEnabledBySwitch"] = startup.IsEnabled() && new StartupRegistration(fixtureExe, startupKey).IsEnabled();
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupKey))
                checks["startupQuotesSpacedPath"] = (string?)key?.GetValue("WslDock") == "\"" + fixtureExe + "\" --startup";
            foreach (var mode in new[] { "light", "dark" })
            {
                Theme.Apply(mode); await Task.Delay(100); Capture(settings, Path.Combine(folder, "startup-" + mode + ".png"));
            }
            startupSwitch.IsChecked = false;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupKey))
                checks["startupDisableOnlyRemovesOwnValue"] = key?.GetValue("WslDock") == null && (string?)key?.GetValue("Unrelated") == "preserved";
            File.Delete(fixtureExe); startupSwitch.IsChecked = true;
            checks["startupFailureRollsBackSwitch"] = startupSwitch.IsChecked == false && !startup.IsEnabled()
                && Descendants<TextBlock>(settings).Any(t => t.Text.StartsWith("无法修改自启动设置"));
            checks["realStartupUnchanged"] = Equals(originalStartup, Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + StartupRegistration.RunKey, "WslDock", null));
            Capture(settings, Path.Combine(folder, "settings.png"));
            var scaleNav = Descendants<Button>(settings).First(b => Descendants<TextBlock>(b).Any(t => t.Text == "显示设置"));
            scaleNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(500); settings.UpdateLayout(); checks["scalePageLaidOut"] = Descendants<TextBlock>(settings).Any(t => t.Text == "显示设置" && t.FontSize == 28 && t.ActualWidth > 0); Capture(settings, Path.Combine(folder, "scaling.png"));
            checks["desktopAttached"] = App.Current.Dock.DesktopAttached;
            checks["desktopHasLayeredSurface"] = App.Current.Dock.HasCompositedSurface;
            checks["dockNotClippedAtFullWidth"] = DockIsNotClipped();
            var resizedApp = apps.First(a => a.Visible && a.Name.Contains("Chrome"));
            resizedApp.Visible = false; App.Current.Dock.RenderApps(); await Task.Delay(250);
            checks["dockNotClippedAfterRemovingIcon"] = DockIsNotClipped();
            resizedApp.Visible = true; App.Current.Dock.RenderApps(); await Task.Delay(250);
            checks["dockNotClippedAfterAddingIcon"] = DockIsNotClipped();
            Native.GetWindowRect(new WindowInteropHelper(App.Current.Dock).Handle, out var resetRect);
            var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            checks["desktopInsidePrimaryWorkArea"] = resetRect.Left >= primary.Left && resetRect.Right <= primary.Right
                && resetRect.Top >= primary.Top && resetRect.Bottom <= primary.Bottom;
            checks["desktopParent"] = DesktopHost.GetParent(new WindowInteropHelper(App.Current.Dock).Handle).ToInt64();
            Theme.Apply("dark"); await Task.Delay(200);
            Capture(settings, Path.Combine(folder, "display-dark.png")); Capture(App.Current.Dock, Path.Combine(folder, "dock-dark.png"));
            checks["darkTheme"] = Theme.IsDark && Ui.Brush("#242424").Color.R > 200;
            Theme.Apply("light"); await Task.Delay(200); Capture(settings, Path.Combine(folder, "display-light.png"));
            checks["lightTheme"] = !Theme.IsDark && Ui.Brush("#242424").Color.R < 100;
            settings.Close(); settings = null;
            var active = await wsl.DistrosAsync(runningOnly: true, includeSystem: true);
            checks["wslStatusMatchesRunningDistros"] = await Until(() => App.Current.Dock.StatusLight.IsRunning == (active.Count > 0));
            var status = App.Current.Dock.StatusLight;
            var actualState = status.IsRunning;
            var lamp = (System.Windows.Shapes.Ellipse)status.Child;
            Color? lightRing = null;
            foreach (var mode in new[] { "light", "dark" })
            {
                Theme.Apply(mode);
                status.SetRunning(false);
                checks["wslStoppedIsHollow-" + mode] = lamp.Fill is SolidColorBrush off && off.Color.A == 0
                    && lamp.Stroke is SolidColorBrush edge && edge.Color.A == 255;
                var ringColor = ((SolidColorBrush)lamp.Stroke).Color;
                if (mode == "light") lightRing = ringColor;
                else checks["wslRingTracksTheme"] = lightRing != ringColor;
                Capture(App.Current.Dock, Path.Combine(folder, "dock-wsl-stopped-" + mode + ".png"));
                status.SetRunning(true);
                checks["wslRunningIsGreen-" + mode] = lamp.Fill is SolidColorBrush on && on.Color.A == 255
                    && on.Color.G > on.Color.R && on.Color.G > on.Color.B;
                Capture(App.Current.Dock, Path.Combine(folder, "dock-wsl-running-" + mode + ".png"));
            }
            status.SetRunning(null);
            checks["wslUnknownNotReportedAsStopped"] = ((SolidColorBrush)lamp.Fill).Color.A == 0
                && System.Windows.Automation.AutomationProperties.GetName(status).Contains("暂不可用");
            status.SetRunning(actualState);
            var health = await wsl.DisplayHealthAsync(distros.First()); checks["displayHealth"] = health;
            checks["noKnownDisplayFailure"] = health.State == "ready";
            if (health.State != "ready") throw new InvalidOperationException("Cannot validate GUI while display is degraded: " + health.Message);
            checks["defaultApps"] = apps.Where(a => a.Visible).Select(a => a.Name).ToArray();
            var terminal = apps.First(a => a.ScaleProfile == "alacritty");
            var probe = JsonSerializer.Deserialize<DockApp>(JsonSerializer.Serialize(terminal, Config.Json), Config.Json)!;
            probe.Id = "wsldock-integration-" + Guid.NewGuid().ToString("N");
            var unique = "WslDock-test-" + Guid.NewGuid().ToString("N")[..8];
            probe.Command = terminal.Command + " --title " + unique; probe.ScalePercent = 200;
            var launch = await wsl.LaunchAsync(probe, 2); checks["launch"] = launch;
            checks["windowAssociated"] = await Until(() => { owned = Native.Windows().FirstOrDefault(w => w.Title.Contains(unique) && Native.Matches(terminal, w)); return owned != null; });
            if (owned == null) throw new InvalidOperationException("No test terminal appeared");
            await Task.Delay(1000);
            var terminalButton = Descendants<Button>(App.Current.Dock).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == terminal.Name);
            void RightClick() => terminalButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            RightClick(); await Task.Delay(250);
            checks["appRightClickDoesNothing"] = AppMenus.Current == null;
            terminalButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(terminalButton), Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await Task.Delay(150);
            checks["appMenuKeyDoesNothing"] = AppMenus.Current == null;
            checks["noPreviewWindow"] = !App.Current.Windows.Cast<Window>().Any(w => w.Title.Contains("预览"));
            foreach (var mode in new[] { "light", "dark" })
            {
                Theme.Apply(mode);
                var trayMenu = AppMenus.Tray(); AppMenus.ShowAtPointer(trayMenu); await Task.Delay(200);
                checks["trayCommands-" + mode] = trayMenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(new[] { "显示 WslDock", "设置", "退出 WslDock" });
                checks["trayTheme-" + mode] = trayMenu.Background is SolidColorBrush brush && (mode == "dark" ? brush.Color.R < 80 : brush.Color.R > 240);
                Capture(trayMenu, Path.Combine(folder, "tray-menu-" + mode + ".png"));
                var dockMenu = AppMenus.Dock(); AppMenus.ShowAtPointer(dockMenu); await Task.Delay(150);
                checks["oneMenuAtATime-" + mode] = !trayMenu.IsOpen && dockMenu.IsOpen;
                Capture(dockMenu, Path.Combine(folder, "dock-menu-" + mode + ".png"));
                // Existing Dock and tray menus continue to track changes while open.
                Theme.Apply(mode == "dark" ? "light" : "dark"); await Task.Delay(100);
                checks["openMenuTracksTheme-" + mode] = dockMenu.Background is SolidColorBrush live && (mode == "dark" ? live.Color.R > 240 : live.Color.R < 80);
                AppMenus.Close();
            }
            Theme.Apply("system");
            var expectedDark = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
            checks["systemTheme"] = Theme.IsDark == expectedDark;
            checks["unrelatedAppDoesNotMatch"] = !Native.Matches(apps.First(a => a.Name == "Codex"), owned);
            var wrongDistro = new DockApp { Distro = "unrelated", DesktopId = terminal.DesktopId, SourceName = terminal.SourceName };
            checks["crossDistroDoesNotMatch"] = !Native.Matches(wrongDistro, owned);
            Native.Minimize(owned); checks["minimized"] = await Until(() => Native.IsIconic(owned.Handle), 3000);
            Native.Restore(owned); checks["restored"] = await Until(() => !Native.IsIconic(owned.Handle), 3000);
            await Task.Delay(500);
            // Clean up only this test's uniquely identified terminal via the backend.
            await wsl.CloseWindowAsync(terminal, owned);
            checks["closed"] = await Until(() => !Native.IsWindow(owned.Handle), 5000);
            var process = await wsl.RequestAsync(probe.Distro, new { action = "status", ids = new[] { probe.Id } });
            checks["processMarkerCleared"] = process.GetProperty("running").GetArrayLength() == 0;
        }
        catch (Exception ex) { checks["error"] = ex.ToString(); }
        finally
        {
            AppMenus.Close();
            if (owned != null && Native.IsWindow(owned.Handle))
            {
                var terminal = App.Current.Prefs.Apps.FirstOrDefault(a => a.ScaleProfile == "alacritty");
                if (terminal != null)
                    try { await App.Current.Wsl.CloseWindowAsync(terminal, owned); }
                    catch (Exception ex) { checks["cleanupError"] = ex.Message; }
            }
            settings?.Close(); App.Current.Dock?.Close();
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupKey, false);
            if (File.Exists(fixtureExe)) File.Delete(fixtureExe);
            if (Directory.Exists(fixtureDir)) Directory.Delete(fixtureDir);
            checks["passed"] = !checks.ContainsKey("error") && !checks.Values.OfType<bool>().Any(value => !value);
            await File.WriteAllTextAsync(Path.Combine(folder, "smoke.json"), JsonSerializer.Serialize(checks, Config.Json));
        }
        return checks["passed"] is true;
    }
}
