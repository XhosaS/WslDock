using System.Reflection;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WslDock;

public static class Branding
{
    public static ImageSource AppIcon { get; } = LoadAppIcon();
    private static ImageSource LoadAppIcon()
    {
        var image = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WslDock.png"));
        image.Freeze(); return image;
    }
    public static bool IsTaskbarLight
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0; }
            catch { return false; }
        }
    }
    public static System.Drawing.Icon TrayIcon(bool lightTaskbar, int size)
    {
        var name = lightTaskbar ? "TrayLight.ico" : "TrayWhite.ico";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WslDock.Assets." + name)
            ?? throw new InvalidOperationException("Missing icon: " + name);
        using var source = new System.Drawing.Icon(stream, size, size);
        // NotifyIcon keeps the handle alive; clone before releasing the resource stream.
        return (System.Drawing.Icon)source.Clone();
    }
}
