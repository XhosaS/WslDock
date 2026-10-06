using System.Text.Json;
using WslDock;

void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
var saved = JsonSerializer.Deserialize<DockApp>("""{"id":"saved","desktopId":"google-chrome.desktop","command":"old","name":"old","visible":true,"boundAppId":"old-window"}""", Config.Json)!;
Check(!saved.CustomConfiguration, "legacy configuration follows desktop on explicit rediscovery");
var discovered = new DockApp { DesktopId = saved.DesktopId, DesktopPath = "/home/user/.local/share/applications/google-chrome.desktop",
    SourceName = "Chrome", Name = "Chrome", Command = "chrome-wayland-ime %U", WorkingDirectory = "/home/user", WmClass = "chrome",
    IconName = "chrome-new", IconPng = "new-icon" };
saved.UpdateFromDesktop(discovered);
Check(saved.Command == discovered.Command && saved.Name == discovered.Name && saved.WorkingDirectory == discovered.WorkingDirectory,
    "desktop launch configuration updates");
Check(saved.DesktopPath == discovered.DesktopPath && saved.SourceName == discovered.SourceName && saved.WmClass == discovered.WmClass && saved.IconPng == discovered.IconPng,
    "desktop identity and icon update together");
Check(saved.Id == "saved" && saved.Visible && saved.BoundAppId == "", "visibility and ID survive while stale window binding clears");
saved.CustomConfiguration = true;
var before = JsonSerializer.Serialize(saved, Config.Json);
discovered.Command = "new-command"; discovered.IconPng = "another-icon"; discovered.Name = "new-name";
saved.UpdateFromDesktop(discovered);
Check(JsonSerializer.Serialize(saved, Config.Json) == before, "custom application is entirely unchanged during rediscovery");
var roundtrip = JsonSerializer.Deserialize<DockApp>(before, Config.Json)!;
Check(roundtrip.CustomConfiguration, "custom switch persists in settings");
roundtrip.CustomConfiguration = false; roundtrip.UpdateFromDesktop(discovered);
Check(roundtrip.Command == "new-command", "disabling custom resumes synchronization");
discovered.DesktopId = "Alacritty.desktop"; discovered.SourceName = "Alacritty";
roundtrip.UpdateFromDesktop(discovered);
Check(roundtrip.Name == "终端", "terminal display name remains consistent with initial discovery");

roundtrip.Name = "我的终端"; roundtrip.CustomName = true;
roundtrip.UpdateFromDesktop(discovered);
Check(roundtrip.Name == "我的终端" && roundtrip.Command == discovered.Command, "independent display name survives desktop command synchronization");
var renamed = JsonSerializer.Deserialize<DockApp>(JsonSerializer.Serialize(roundtrip, Config.Json), Config.Json)!;
Check(renamed.CustomName && renamed.Name == "我的终端", "independent name persists without custom command switch");
