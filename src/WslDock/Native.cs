using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace WslDock;

public sealed record AppWindow(nint Handle, uint ProcessId, string Title, string AppId, string DisplayName, string IconPath, string Relaunch, bool Minimized, ulong ServerWindowId = 0, string LinuxAppId = "", string DistroName = "")
{
    public string Distro
    {
        get
        {
            if (DistroName.Length > 0) return DistroName;
            const string marker = "\\WSLDVCPlugin\\";
            var index = IconPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index < 0 ? "" : IconPath[(index + marker.Length)..].Split('\\')[0];
        }
    }
}

public static partial class Native
{
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder value, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    internal static void PositionMenuHost(nint hwnd, int x, int y) => SetWindowPos(hwnd, 0, x, y, 1, 1, 0x0004 | 0x0010);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] public struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Key { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out Key key);
        [PreserveSig] int GetValue(ref Key key, out Variant value);
        [PreserveSig] int SetValue(ref Key key, ref Variant value);
        [PreserveSig] int Commit();
    }

    private static string ReadProperty(IPropertyStore store, uint id)
    {
        var key = new Key { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        if (store.GetValue(ref key, out var value) != 0) return "";
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
        finally { PropVariantClear(ref value); }
    }

    public static List<AppWindow> Windows()
    {
        var result = new List<AppWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "RAIL_WINDOW") return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName is not ("msrdc" or "mstsc")) return true;
            }
            catch { return true; }
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return true;
            try
            {
                var icon = ReadProperty(store, 3);
                var relaunch = ReadProperty(store, 2);
                // Ordinary Remote Desktop windows must never be managed by this dock.
                var serverId = unchecked((ulong)GetProp(hwnd, "WslgServerWindowId").ToInt64());
                var identity = FindWslgIdentity(serverId);
                if ((!icon.Contains("\\WSLDVCPlugin\\", StringComparison.OrdinalIgnoreCase) || !relaunch.Contains("wslg.exe", StringComparison.OrdinalIgnoreCase)) && identity == null) return true;
                var title = new StringBuilder(2048);
                GetWindowText(hwnd, title, title.Capacity);
                result.Add(new(hwnd, pid, title.ToString(), ReadProperty(store, 5), ReadProperty(store, 4), icon, relaunch, IsIconic(hwnd), serverId, identity?.AppId ?? "", identity?.Distro ?? ""));
            }
            finally { Marshal.ReleaseComObject(store); }
            return true;
        }, 0);
        return result;
    }

    public static bool Matches(DockApp app, AppWindow window)
    {
        if (!string.Equals(app.Distro, window.Distro, StringComparison.OrdinalIgnoreCase)) return false;
        if (app.BoundAppId.Length > 0) return app.BoundAppId == window.AppId;
        var iconId = Path.GetFileNameWithoutExtension(window.IconPath);
        var desktopId = Path.GetFileNameWithoutExtension(app.DesktopId);
        return (window.LinuxAppId.Length > 0 && string.Equals(window.LinuxAppId, desktopId, StringComparison.Ordinal))
            || string.Equals(iconId, desktopId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(window.DisplayName, app.SourceName + " (" + app.Distro + ")", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Validate(AppWindow window)
    {
        if (!IsWindow(window.Handle)) return false;
        GetWindowThreadProcessId(window.Handle, out var pid);
        if (pid != window.ProcessId) return false;
        if (window.ServerWindowId != 0 && unchecked((ulong)GetProp(window.Handle, "WslgServerWindowId").ToInt64()) != window.ServerWindowId) return false;
        // HWNDs can be reused. Re-read identity immediately before any action.
        // Visibility is deliberately not required: RAIL briefly hides a window while restoring it.
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(window.Handle, ref iid, out var store) != 0) return false;
        try { return ReadProperty(store, 5) == window.AppId && ReadProperty(store, 3) == window.IconPath; }
        finally { Marshal.ReleaseComObject(store); }
    }
    public static bool Restore(AppWindow window)
    {
        if (!Validate(window)) return false;
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
        return SetForegroundWindow(window.Handle);
    }
    public static bool Minimize(AppWindow window) => Validate(window) && ShowWindowAsync(window.Handle, 6);
    // Use the system-menu action, which WSLg forwards to the Linux application's close handler.
    public static bool Close(AppWindow window) => Validate(window) && PostMessage(window.Handle, 0x0112, 0xF060, 0);

    public static void Style(Window window, bool small = false)
    {
        // Layered desktop widgets supply their own pixels; DWM backdrops belong to top-level windows.
        if (window.AllowsTransparency) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        var corner = small ? 3 : 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
        var isDark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref isDark, 4);
        var caption = Theme.IsDark ? 0x00202020 : 0x00F3F3F3;
        var captionText = Theme.IsDark ? 0x00F2F2F2 : 0x00242424;
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
        DwmSetWindowAttribute(hwnd, 36, ref captionText, 4);
        var border = Theme.IsDark ? 0x00474747 : 0x00DADADA;
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);
        var backdrop = small ? 3 : 2;
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
    }
}
