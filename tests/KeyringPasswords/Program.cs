using System.Security.Cryptography;
using System.Text.Json;
using WslDock;

void Check(bool passed, string label) { if (!passed) throw new Exception(label); Console.WriteLine("PASS " + label); }
const string password = "fixture-secret 中文 \" $(ignored) ;\n";
var encrypted = KeyringPassword.Protect("Fixture Ubuntu", password);
var prefs = new Preferences(); prefs.KeyringPasswords["Fixture Ubuntu"] = encrypted;
Check(KeyringPassword.Read(prefs, "Fixture Ubuntu") == password, "current user password round trip");
Check(!JsonSerializer.Serialize(prefs, Config.Json).Contains("fixture-secret"), "settings contain no plaintext password");
Check(KeyringPassword.Read(prefs, "Missing") == null, "missing credential leaves automatic unlock disabled");
prefs.KeyringPasswords["Other distro"] = encrypted;
try { KeyringPassword.Read(prefs, "Other distro"); throw new Exception("distro binding missing"); }
catch (InvalidOperationException ex) { Check(!ex.Message.Contains(password), "credential is bound to distro and error is sanitized"); }
prefs.KeyringPasswords["Fixture Ubuntu"] = "invalid ciphertext";
try { KeyringPassword.Read(prefs, "Fixture Ubuntu"); throw new Exception("corrupt credential accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS corrupt credential requests reconfiguration"); }

var oldJson = """
{"version":1,"themeMode":"dark","appScalingEnabled":false,"apps":[{"id":"saved","desktopId":"code-wsl.desktop","command":"code-wsl --ozone-platform=wayland %F","visible":true,"scaleProfile":"chromium","scalePercent":200}],"keyringPasswords":{"Ubuntu":"ciphertext"}}
""";
var migrated = JsonSerializer.Deserialize<Preferences>(oldJson, Config.Json)!;
Check(migrated.Apps[0].Command == "code-wsl --ozone-platform=wayland %F" && migrated.Apps[0].Visible, "legacy settings preserve application command and visibility");
Check(migrated.KeyringPasswords["Ubuntu"] == "ciphertext", "legacy settings preserve encrypted passwords");
var newJson = JsonSerializer.Serialize(migrated, Config.Json);
Check(!newJson.Contains("themeMode") && !newJson.Contains("appScalingEnabled") && !newJson.Contains("scaleProfile") && !newJson.Contains("scalePercent"), "removed display fields disappear when preferences are saved");
