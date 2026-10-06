using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslDock;

public sealed class DockApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Distro { get; set; } = "";
    public string DesktopId { get; set; } = "";
    public string DesktopPath { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string IconName { get; set; } = "";
    public string IconPng { get; set; } = "";
    public string IconSource { get; set; } = "";
    public string WmClass { get; set; } = "";
    public bool Visible { get; set; }
    public string BoundAppId { get; set; } = "";
    [JsonIgnore] public bool IsTerminal => string.Equals(SourceName, "Alacritty", StringComparison.OrdinalIgnoreCase)
        || string.Equals(DesktopId, "Alacritty.desktop", StringComparison.OrdinalIgnoreCase);
}

public sealed class Preferences
{
    public int Version { get; set; } = 1;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public List<DockApp> Apps { get; set; } = new();
    public Dictionary<string, string> KeyringPasswords { get; set; } = new();
}

public static class Config
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslDock");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    public static string? LoadWarning { get; private set; }
    public static Preferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var config = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), Json) ?? throw new JsonException("设置为空");
            if (config.Version != 1 || config.Apps == null || config.Apps.Any(a => a == null)) throw new JsonException("设置版本或应用列表无效");
            config.KeyringPasswords ??= new();
            return config;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Preserve the user's invalid file before writing any fresh preferences.
            File.Copy(FilePath, FilePath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".backup", true);
            LoadWarning = "设置文件无法读取，已保留备份：" + ex.Message;
            return new();
        }
    }
    public static void Save(Preferences prefs)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(prefs, Json));
        File.Move(temp, FilePath, true);
    }
    public static void Log(string text)
    {
        try { Directory.CreateDirectory(DirectoryPath); File.AppendAllText(Path.Combine(DirectoryPath, "wsldock.log"), DateTime.Now.ToString("O") + " " + text + Environment.NewLine); } catch { }
    }
}
