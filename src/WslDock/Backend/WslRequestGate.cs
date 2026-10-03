namespace WslDock;

// A shutdown drains active requests and invalidates queued ones. Background
// requests remain paused until a subsequent explicit user request resumes them.
public sealed class WslRequestGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int generation;
    public bool IsShuttingDown { get; private set; }
    public bool BackgroundPaused { get; private set; }

    public async Task<T> RunAsync<T>(Func<Task<T>> action, bool background = false)
    {
        var ticket = generation;
        Check(ticket, background);
        await gate.WaitAsync();
        try
        {
            Check(ticket, background);
            if (!background) BackgroundPaused = false;
            return await action();
        }
        finally { gate.Release(); }
    }

    private void Check(int ticket, bool background)
    {
        if (IsShuttingDown || ticket != generation || (background && BackgroundPaused))
            throw new InvalidOperationException("WSL 已暂停后台查询，请手动启动应用后重试。");
    }

    public async Task ShutdownAsync(Func<Task> action)
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        BackgroundPaused = true;
        generation++;
        await gate.WaitAsync();
        try { await action(); }
        finally { gate.Release(); IsShuttingDown = false; }
    }
}
