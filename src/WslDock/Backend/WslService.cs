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

    public async Task<List<DockApp>> DiscoverAsync(string distro)
    {
        var result = await RequestAsync(distro, new { action = "discover" });
        var apps = result.GetProperty("apps").Deserialize<List<DockApp>>(Config.Json) ?? new();
        foreach (var a in apps)
        {
            a.Distro = distro;
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

    public async Task<DisplayHealth> DisplayHealthAsync(string distro, bool background = false)
    {
        var result = await RequestAsync(distro, new { action = "display_health" }, background);
        var health = result.Deserialize<DisplayHealth>(Config.Json) ?? new();
        if (Native.Windows().Any(w => w.Distro == distro && w.Title.Contains("[WARN:COPY MODE]")))
            return new DisplayHealth { State = "broken", Message = "WSLg 已进入 COPY MODE，窗口画面可能不可用。", Detail = health.Detail };
        return health;
    }

    public Task<JsonElement> LaunchAsync(DockApp app, double monitorScale) => RequestAsync(app.Distro,
        new { action = "launch", app, scale = app.ScalePercent == 0 ? Math.Clamp(monitorScale, 1, 3) : app.ScalePercent / 100.0 });

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

public sealed class DisplayHealth
{
    public string State { get; set; } = "unknown";
    public string Message { get; set; } = "无法确认显示状态";
    public string Detail { get; set; } = "";
    public bool Broken => State == "broken";
    public const string Recovery = "请保存 WSL 中的工作后，更新 WSL 并重启会话。更新可使用 wsl --update，重启使用 wsl --shutdown。重启会停止所有 WSL 进程；WslDock 不会自动执行。";
}
