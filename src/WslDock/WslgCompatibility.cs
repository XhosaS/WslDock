using System.Runtime.InteropServices;
using System.Text.Json;

namespace WslDock;

public static partial class Native
{
    // WSLg's public WslgServerWindowId property combines the provider GUID Data1
    // and RAIL window ID. See microsoft/wslg WSLDVCPlugin/WSLDVCCallback.cpp.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private sealed record WslgIdentity(string Distro, string AppId);
    private static readonly Dictionary<ulong, WslgIdentity> wslgIdentities = new();
    private sealed record IconRepair(AppWindow Window, System.Drawing.Icon Icon, nint OldBig, nint OldSmall);
    private static readonly Dictionary<nint, IconRepair> repairedIcons = new();

    private static WslgIdentity? FindWslgIdentity(ulong id) => wslgIdentities.GetValueOrDefault(id);

    public static void RetainWslgDistros(IEnumerable<string> active)
    {
        var names = active.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in wslgIdentities.Where(p => !names.Contains(p.Value.Distro)).Select(p => p.Key).ToArray())
            wslgIdentities.Remove(id);
    }

    public static void UpdateWslgIdentities(string distro, JsonElement windows)
    {
        foreach (var id in wslgIdentities.Where(p => p.Value.Distro == distro).Select(p => p.Key).ToArray())
            wslgIdentities.Remove(id);
        foreach (var window in windows.EnumerateObject())
            if (ulong.TryParse(window.Name, out var id) && (id >> 32) != 0 && (uint)id != 0
                && window.Value.GetString() is { Length: > 0 } appId)
            {
                if (wslgIdentities.TryGetValue(id, out var other) && other.Distro != distro) continue;
                wslgIdentities[id] = new(distro, appId);
            }
    }

    private static nint WindowIcon(nint hwnd, int size)
    {
        SendMessageTimeout(hwnd, 0x007F, size, 0, 2, 100, out var icon); // WM_GETICON
        return icon;
    }
    internal static bool HasRepairedIcon(AppWindow window) => Validate(window) && repairedIcons.TryGetValue(window.Handle, out var repair) && WindowIcon(window.Handle, 1) == repair.Icon.Handle && WindowIcon(window.Handle, 0) == repair.Icon.Handle;

    public static void RepairWslgIcons(List<AppWindow> windows, List<DockApp> apps)
    {
        foreach (var pair in repairedIcons.ToArray())
        {
            if (windows.Any(w => w.Handle == pair.Key && w.ProcessId == pair.Value.Window.ProcessId && w.AppId == pair.Value.Window.AppId && w.ServerWindowId == pair.Value.Window.ServerWindowId)) continue;
            RestoreIcon(pair.Value);
            pair.Value.Icon.Dispose(); repairedIcons.Remove(pair.Key);
        }
        foreach (var window in windows)
        {
            if (window.IconPath.Length != 0 || window.LinuxAppId.Length == 0) continue;
            var matches = apps.Where(a => Matches(a, window)).ToArray();
            if (matches.Length != 1 || !Validate(window)) continue;
            if (!repairedIcons.TryGetValue(window.Handle, out var repair))
            {
                try
                {
                    var app = matches[0];
                    if (app.IconPng.Length == 0 || app.IconSource != "linux") continue;
                    System.Drawing.Icon icon;
                    using (var bytes = new MemoryStream(Convert.FromBase64String(app.IconPng)))
                    using (var bitmap = new System.Drawing.Bitmap(bytes))
                    {
                        var handle = bitmap.GetHicon();
                        try { using var borrowed = System.Drawing.Icon.FromHandle(handle); icon = (System.Drawing.Icon)borrowed.Clone(); }
                        finally { DestroyIcon(handle); }
                    }
                    repair = new(window, icon, WindowIcon(window.Handle, 1), WindowIcon(window.Handle, 0));
                    repairedIcons[window.Handle] = repair;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException or FormatException)
                { Config.Log("WSLg icon: " + ex.Message); continue; }
            }
            if (!Validate(window)) continue;
            foreach (var size in new[] { 1, 0 })
                if (WindowIcon(window.Handle, size) != repair.Icon.Handle)
                    SendMessageTimeout(window.Handle, 0x0080, size, repair.Icon.Handle, 2, 100, out _); // WM_SETICON
        }
    }

    private static void RestoreIcon(IconRepair repair)
    {
        if (!Validate(repair.Window)) return;
        if (WindowIcon(repair.Window.Handle, 1) == repair.Icon.Handle)
            SendMessageTimeout(repair.Window.Handle, 0x0080, 1, repair.OldBig, 2, 100, out _);
        if (WindowIcon(repair.Window.Handle, 0) == repair.Icon.Handle)
            SendMessageTimeout(repair.Window.Handle, 0x0080, 0, repair.OldSmall, 2, 100, out _);
    }
    public static void RestoreWslgIcons()
    {
        foreach (var repair in repairedIcons.Values) { RestoreIcon(repair); repair.Icon.Dispose(); }
        repairedIcons.Clear();
    }
}
