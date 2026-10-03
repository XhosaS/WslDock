using WslDock;

static void Assert(bool result, string label)
{
    if (!result) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}
static async Task Rejected(Task task)
{
    try { await task; throw new Exception("Request was allowed"); }
    catch (InvalidOperationException) { }
}

var gate = new WslRequestGate();
var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var active = gate.RunAsync(async () => { entered.SetResult(); return await release.Task; }, background: true);
await entered.Task;
bool queuedRan = false, shutdownRan = false;
var queued = gate.RunAsync(() => { queuedRan = true; return Task.FromResult(0); });
var shutdownRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var shutdown = gate.ShutdownAsync(async () => { shutdownRan = true; await shutdownRelease.Task; });
Assert(gate.IsShuttingDown && !shutdownRan, "shutdown waits for active request");
await Rejected(gate.RunAsync(() => Task.FromResult(0)));
release.SetResult(1);
await active;
await Rejected(queued);
Assert(!queuedRan, "queued request is invalidated by shutdown");
while (!shutdownRan) await Task.Delay(1);
await gate.ShutdownAsync(() => throw new Exception("Duplicate shutdown"));
shutdownRelease.SetResult();
await shutdown;
Assert(!gate.IsShuttingDown && gate.BackgroundPaused, "background remains paused after shutdown");
await Rejected(gate.RunAsync(() => Task.FromResult(0), background: true));
Assert(await gate.RunAsync(() => Task.FromResult(42)) == 42, "explicit user action resumes requests");
Assert(await gate.RunAsync(() => Task.FromResult(7), background: true) == 7, "background resumes after user action");
try { await gate.ShutdownAsync(() => throw new IOException("fake shutdown failure")); }
catch (IOException) { }
Assert(!gate.IsShuttingDown && gate.BackgroundPaused, "failed shutdown releases gate without restarting WSL");
Assert(await gate.RunAsync(() => Task.FromResult(9)) == 9, "user can retry after failure");
