using System.Text.Json;

namespace WslDock;

public static class Diagnostics
{
    public static async Task RunAsync(string? output)
    {
        var report = new Dictionary<string, object?>();
        try
        {
            var wsl = new WslService();
            var distros = await wsl.DistrosAsync();
            report["distros"] = distros;
            var health = new Dictionary<string, DisplayHealth>();
            foreach (var distro in distros) health[distro] = await wsl.DisplayHealthAsync(distro);
            report["displayHealth"] = health;
            var discovered = new List<DockApp>();
            foreach (var distro in distros) discovered.AddRange(await wsl.DiscoverAsync(distro));
            report["apps"] = discovered.Select(a => new { a.Name, a.Distro, a.DesktopId, a.Command, a.ScaleProfile });
            foreach (var distro in distros)
            {
                var status = await wsl.RequestAsync(distro, new { action = "status", ids = Array.Empty<string>() });
                Native.UpdateWslgIdentities(distro, status.GetProperty("windowApps"));
            }
            var windows = Native.Windows();
            report["windows"] = windows.Select(w => new { handle = w.Handle.ToInt64(), w.Title, w.DisplayName, w.Distro, w.AppId, w.Minimized, w.ServerWindowId, w.LinuxAppId, w.IconPath,
                matches = discovered.Where(a => Native.Matches(a, w)).Select(a => a.DesktopId).ToArray() });
        }
        catch (Exception ex) { report["error"] = ex.ToString(); }
        var file = output ?? Path.Combine(Config.DirectoryPath, "diagnostics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, Config.Json));
    }
}
