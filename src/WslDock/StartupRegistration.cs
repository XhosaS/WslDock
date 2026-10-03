using Microsoft.Win32;

namespace WslDock;

public sealed class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WslDock";
    private readonly string executable;
    private readonly string keyPath;
    internal string Command => "\"" + executable + "\" --startup";

    public StartupRegistration() : this(Path.Combine(AppContext.BaseDirectory, "WslDock.exe"), RunKey) { }
    internal StartupRegistration(string executable, string keyPath)
    {
        this.executable = Path.GetFullPath(executable);
        this.keyPath = keyPath;
    }
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到 WslDock.exe，请从完整的程序目录运行。", executable);
            if (Command.Length > 260) throw new InvalidOperationException("程序路径过长，请将 WslDock 移到较短的目录后重试。");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }
}
