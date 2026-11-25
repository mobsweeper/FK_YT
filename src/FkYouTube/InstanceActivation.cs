using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FkYouTube;

// This endpoint can only request window restoration. It accepts no URL, file,
// command line or account data; the existing mutex/file lock still owns startup.
internal sealed class InstanceActivation : IDisposable
{
    private const byte Restore = 1;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _listener;
    private int _disposed;

    internal InstanceActivation(RuntimePaths paths, Action requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        _pipe = new NamedPipeServerStream(PipeNameFor(paths.Data), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        _listener = ListenAsync(requested);
    }

    internal static string PipeNameFor(string data)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var owner = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var scope = RuntimePaths.NormalizeRoot(data).ToUpperInvariant() + "\n" + owner + "\n" + process.SessionId;
        return "FKYT-activate-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    internal static async Task<bool> TryNotifyAsync(RuntimePaths paths, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeNameFor(paths.Data), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // Startup may still be between ownership acquisition and listener creation.
            await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) || session != process.SessionId ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
            // A user-launched secondary process can pass its foreground permission
            // to this exact server, without changing Topmost or granting all apps.
            _ = AllowSetForegroundWindow(pid);
            await pipe.WriteAsync(new byte[] { Restore }, timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            return await pipe.ReadAsync(reply, timeout.Token).ConfigureAwait(false) == 1 && reply[0] == Restore;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        { return false; }
    }

    private async Task ListenAsync(Action requested)
    {
        while (!_stop.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                connected = true;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(1));
                using var process = Process.GetCurrentProcess();
                if (!GetNamedPipeClientSessionId(_pipe.SafePipeHandle, out var session) || session != process.SessionId) continue;
                var command = new byte[1];
                if (await _pipe.ReadAsync(command, request.Token).ConfigureAwait(false) != 1 || command[0] != Restore) continue;
                requested();
                await _pipe.WriteAsync(new byte[] { Restore }, request.Token).ConfigureAwait(false);
                // DisconnectNamedPipe discards unread output. Wait for the client
                // to consume its receipt and close, bounded even for idle clients.
                await _pipe.ReadAsync(command, request.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) break;
                if (!connected)
                {
                    try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                // An EOF/broken connection reports IsConnected=false but still
                // needs Disconnect before WaitForConnectionAsync can accept again.
                try { if (connected) _pipe.Disconnect(); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _pipe.Dispose();
        _ = _listener.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
