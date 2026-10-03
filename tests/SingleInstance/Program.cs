using System.Diagnostics;
using WslDock;

// Run as separate processes to exercise the real Windows mutex and IPC.
if (args.Length > 0)
{
    using var child = new SingleInstance(args[1]);
    if (args[0] == "owner") { Console.WriteLine(child.IsPrimary ? "ready" : "failed"); Thread.Sleep(Timeout.Infinite); }
    if (child.IsPrimary) return 10;
    return child.ActivateAsync(args[0] == "settings").GetAwaiter().GetResult() ? 0 : 11;
}

var name = "WslDock.Tests." + Guid.NewGuid().ToString("N");
int dock = 0, settings = 0;
Process Child(string command)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    info.ArgumentList.Add(command); info.ArgumentList.Add(name);
    return Process.Start(info)!;
}
void Check(bool passed, string label) { if (!passed) throw new Exception(label); Console.WriteLine("PASS " + label); }
using (var primary = new SingleInstance(name))
{
    Check(primary.IsPrimary, "first launch owns mutex");
    // A client launched before listener startup must wait for the primary to become ready.
    using var early = Child("settings");
    Thread.Sleep(250);
    primary.Listen(showSettings => { if (showSettings) Interlocked.Increment(ref settings); else Interlocked.Increment(ref dock); });
    Check(early.WaitForExit(10000) && early.ExitCode == 0, "activation during startup");
    for (int i = 0; i < 8; i++)
    {
        using var client = Child(i % 2 == 0 ? "dock" : "settings");
        Check(client.WaitForExit(10000) && client.ExitCode == 0, "secondary exits after forwarding " + i);
    }
    Check(SpinWait.SpinUntil(() => dock == 4 && settings == 5, 3000), "all activation requests delivered");
    var burst = Enumerable.Range(0, 12).Select(i => Child(i % 2 == 0 ? "dock" : "settings")).ToArray();
    foreach (var client in burst)
    {
        using (client) Check(client.WaitForExit(10000) && client.ExitCode == 0, "concurrent secondary forwards activation");
    }
    Check(SpinWait.SpinUntil(() => dock == 10 && settings == 11, 3000), "concurrent requests all delivered");
}
using (var restarted = new SingleInstance(name)) Check(restarted.IsPrimary, "normal exit releases mutex");
using (var crashed = Child("owner"))
{
    Check(crashed.StandardOutput.ReadLine() == "ready", "crash fixture owns mutex");
    crashed.Kill(); crashed.WaitForExit();
}
using (var recovered = new SingleInstance(name)) Check(recovered.IsPrimary, "restart after abrupt termination");
return 0;
