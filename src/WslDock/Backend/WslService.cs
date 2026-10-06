using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace WslDock;

public sealed class WslService
{
    private readonly string script;
    private readonly WslRequestGate requests = new();
    public bool IsShuttingDown => requests.IsShuttingDown;
    public bool BackgroundPaused => requests.BackgroundPaused;
    public Task ShutdownAsync() => requests.ShutdownAsync(async () =>
        await RunAsync(new[] { "--shutdown" }, null, Encoding.Unicode));

    public WslService()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WslDock.Backend.wsl_bridge.py")!;
        using var reader = new StreamReader(stream);
        script = reader.ReadToEnd();
    }

    public async Task<List<string>> DistrosAsync(bool runningOnly = false, bool includeSystem = false)
    {
        var args = new List<string> { "--list", "--quiet" };
        if (runningOnly) args.Add("--running");
        var output = await RunAsync(args, null, Encoding.Unicode);
        return output.Replace("\0", "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0 && (includeSystem || !s.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public Task<JsonElement> RequestAsync(string distro, object request, bool background = false) => requests.RunAsync(async () =>
    {
        var text = await RunAsync(new[] { "-d", distro, "--", "python3", "-c", script }, JsonSerializer.Serialize(request, Config.Json), Encoding.UTF8);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        return doc.RootElement.Clone();
    }, background);

    // Refresh only for a distro already running, or during an explicit launch.
    public async Task<bool> RefreshIconsAsync(string distro, IEnumerable<DockApp> apps, bool background = true)
    {
        var pending = apps.Where(a => a.IconSource != "linux").ToArray();
        if (pending.Length == 0) return false;
        var result = await RequestAsync(distro, new { action = "icons", apps = pending }, background);
        foreach (var item in result.GetProperty("icons").EnumerateArray())
        {
            var app = pending.Single(a => a.Id == item.GetProperty("id").GetString());
            app.IconPng = item.GetProperty("iconPng").GetString() ?? "";
            app.IconSource = "linux";
        }
        return true;
    }

    public async Task<List<DockApp>> DiscoverAsync(string distro)
    {
        var result = await RequestAsync(distro, new { action = "discover" });
        var apps = result.GetProperty("apps").Deserialize<List<DockApp>>(Config.Json) ?? new();
        foreach (var a in apps)
        {
            a.Distro = distro;
            a.IconSource = "linux";
            if (a.IconPng.Length == 0) continue;
            try
            {
                using var bytes = new MemoryStream(Convert.FromBase64String(a.IconPng));
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 128; image.StreamSource = bytes; image.EndInit(); image.Freeze();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var compressed = new MemoryStream(); encoder.Save(compressed); a.IconPng = Convert.ToBase64String(compressed.ToArray());
            }
            catch { a.IconPng = ""; }
        }
        return apps;
    }

    public Task<JsonElement> LaunchAsync(DockApp app) => RequestAsync(app.Distro,
        new { action = "launch", app, keyringPassword = KeyringPassword.Read(App.Current.Prefs, app.Distro) });

    public Task<JsonElement> UnlockKeyringAsync(string distro, string password) =>
        RequestAsync(distro, new { action = "unlock_keyring", password });

    public async Task CloseWindowAsync(DockApp app, AppWindow window)
    {
        if (!Native.Matches(app, window) || !Native.Close(window)) throw new InvalidOperationException("窗口身份已改变，请重新打开菜单。");
        // RAIL destroys its host HWND asynchronously after the Linux client has exited.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(150);
            if (!Native.IsWindow(window.Handle)) return;
        }
        if (!Native.Windows().Any(w => w.Handle == window.Handle && w.ProcessId == window.ProcessId && w.AppId == window.AppId && w.Title == window.Title))
            throw new InvalidOperationException("窗口已改变，请重新打开菜单。");
        // RAIL does not forward SC_CLOSE reliably in every WSLg build. X11's normal
        // close protocol still lets applications display their unsaved-work prompt.
        try { await RequestAsync(app.Distro, new { action = "close_window", app, title = window.Title }); }
        catch (InvalidOperationException)
        {
            // A missing X11 target can mean the original normal-close request succeeded.
            // Do not hide a real identification error if the same host window survives.
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(150);
                if (!Native.IsWindow(window.Handle)) return;
            }
            throw;
        }
    }

    public static async Task<string> RunAsync(IEnumerable<string> args, string? input, Encoding encoding)
    {
        var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
            StandardOutputEncoding = encoding, StandardErrorEncoding = Encoding.UTF8 };
        if (input != null) info.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 WSL");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            if (input != null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("WSL 响应超时。请确认发行版可以启动，且已安装 python3。");
        }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException((stdout + "\n" + stderr).Trim());
        return stdout;
    }
}
