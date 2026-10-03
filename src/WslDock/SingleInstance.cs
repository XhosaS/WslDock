using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WslDock;

// One interactive instance per Windows session, independent of the executable's folder.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private Task? listener;
    public bool IsPrimary { get; }

    public SingleInstance(string name = "WslDock.Desktop")
    {
        mutex = new Mutex(false, @"Local\" + name);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        pipeName = name + "." + WindowsIdentity.GetCurrent().User!.Value + "." + Process.GetCurrentProcess().SessionId;
    }

    public void Listen(Action<bool> activate)
    {
        if (!IsPrimary || listener != null) throw new InvalidOperationException("Only the primary instance can listen once.");
        listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Action<bool> activate)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await pipe.ReadAsync(command, timeout.Token).ConfigureAwait(false) == 1 && command[0] <= 1)
                    activate(command[0] == 1);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100).ConfigureAwait(false); }
        }
    }

    public async Task<bool> ActivateAsync(bool settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            // Forward the user's foreground permission to the existing process.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) AllowSetForegroundWindow(pid);
            await pipe.WriteAsync(new byte[] { settings ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener?.GetAwaiter().GetResult();
        if (IsPrimary) mutex.ReleaseMutex(); // Dispose on the acquiring (UI) thread.
        mutex.Dispose();
        stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
