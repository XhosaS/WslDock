using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WslDock;

// Only our own HWND is reparented. Never create, hide or rearrange Explorer's windows.
public sealed class DesktopHost
{
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] public static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetStyle(nint hwnd, int index, nint style);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref PointI point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI point);
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    private readonly Window window;
    private nint parent;
    private PointI dragCursor;
    private Native.RectI dragRect;
    public bool HasCompositedSurface => (GetStyle(Handle, -20).ToInt64() & 0x00080000) != 0;
    public bool Attached => parent != 0 && Native.IsWindow(parent) && GetParent(Handle) == parent;
    private nint Handle => new WindowInteropHelper(window).Handle;
    public DesktopHost(Window window) => this.window = window;
    public void Attach()
    {
        if (Attached) return;
        nint shell = 0;
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(128); GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is not ("Progman" or "WorkerW") || !IsWindowVisible(hwnd)) return true;
            var view = FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null);
            if (view == 0 || !IsWindowVisible(view)) return true;
            // The icon view has a real composited surface. Its Progman parent can
            // be WS_EX_NOREDIRECTIONBITMAP on Windows 11, dropping WPF child pixels.
            shell = view; return false;
        }, 0);
        if (shell == 0) throw new InvalidOperationException("桌面尚未就绪，请在 Explorer 启动后重试。");
        var hwnd = Handle;
        Native.GetWindowRect(hwnd, out var rect);
        var style = GetStyle(hwnd, -16);
        // SetParent does not adjust WS_CHILD / WS_POPUP itself (Win32 contract).
        SetStyle(hwnd, -16, (nint)((style.ToInt64() | 0x40000000L) & ~0x80000000L));
        Marshal.SetLastPInvokeError(0);
        SetParent(hwnd, shell);
        var error = Marshal.GetLastPInvokeError();
        if (GetParent(hwnd) != shell)
        {
            SetStyle(hwnd, -16, style);
            throw new Win32Exception(error, "无法挂载到桌面层");
        }
        parent = shell;
        MoveTo(rect.Left, rect.Top);
    }
    public void ClampToWorkArea()
    {
        if (!Attached || !Native.GetWindowRect(Handle, out var rect)) return;
        var area = System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        if (x != rect.Left || y != rect.Top) MoveTo(x, y);
    }
    public void BeginDrag() { GetCursorPos(out dragCursor); Native.GetWindowRect(Handle, out dragRect); }
    public void Drag()
    {
        GetCursorPos(out var cursor);
        var x = dragRect.Left + cursor.X - dragCursor.X; var y = dragRect.Top + cursor.Y - dragCursor.Y;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - (dragRect.Right - dragRect.Left)));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - (dragRect.Bottom - dragRect.Top)));
        MoveTo(x, y);
    }
    public void MoveTo(int x, int y)
    {
        if (!Attached) return;
        var point = new PointI { X = x, Y = y }; ScreenToClient(parent, ref point);
        // HWND_TOP here is only the top of the desktop's child windows, never topmost.
        SetWindowPos(Handle, 0, point.X, point.Y, 0, 0, 0x0001 | 0x0010 | 0x0020 | 0x0040);
    }
}
