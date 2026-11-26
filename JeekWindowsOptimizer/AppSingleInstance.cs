using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using JeekTools;
using JeekWindowsOptimizer.Mcp;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
/// Enforces one running instance per installation directory. Launches from the same folder
/// forward an activation signal to the primary instance and exit; parallel worktree or
/// separate-directory installations keep independent mutex and pipe names.
/// </summary>
internal sealed class AppSingleInstance : IDisposable
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AppSingleInstance));
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string ActivateMessage = "ACTIVATE";
    private const uint AsfwAny = unchecked((uint)-1);

    private readonly Mutex? _mutex;
    private readonly int _ownerThreadId;
    private readonly string _pipeName;
    private CancellationTokenSource? _listenerCts;
    private bool _disposed;

    private AppSingleInstance(Mutex? mutex, int ownerThreadId, string pipeName)
    {
        _mutex = mutex;
        _ownerThreadId = ownerThreadId;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _mutex is not null;

    public string PipeName => _pipeName;

    public static AppSingleInstance Acquire(string appDirectory)
    {
        var instanceId = McpPipeNames.InstanceId(appDirectory);
        var mutexName = $"Local\\JeekWindowsOptimizer.Instance.{instanceId}";
        var pipeName = $"JeekWindowsOptimizer.Instance.{instanceId}";

        var mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        if (createdNew)
            return new AppSingleInstance(mutex, Environment.CurrentManagedThreadId, pipeName);

        mutex.Dispose();
        return new AppSingleInstance(null, 0, pipeName);
    }

    public void StartActivationListener(Action onActivate)
    {
        if (!IsPrimary || _listenerCts is not null || _disposed)
            return;

        var cts = new CancellationTokenSource();
        _listenerCts = cts;
        _ = Task.Run(() => ListenLoopAsync(onActivate, cts.Token));
    }

    public void StopListener()
    {
        var cts = Interlocked.Exchange(ref _listenerCts, null);
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        cts.Dispose();
    }

    public bool NotifyPrimaryInstance(int timeoutMs = 1500)
    {
        try
        {
            AllowSetForegroundWindow(AsfwAny);
        }
        catch
        {
            // Best effort.
        }

        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 100);
        while (true)
        {
            var remaining = (int)Math.Max(50, deadline - Environment.TickCount64);
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.None
                );
                client.Connect(remaining);
                using var writer = new StreamWriter(client, Utf8, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(ActivateMessage);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    Log.ZLogWarning($"Could not signal existing instance on {_pipeName}: {ex.Message}");
                    return false;
                }

                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Log.ZLogWarning($"Could not signal existing instance on {_pipeName}: {ex.Message}");
                return false;
            }
        }
    }

    private async Task ListenLoopAsync(Action onActivate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server,
                    Utf8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true
                );
                var message = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (string.Equals(message?.Trim(), ActivateMessage, StringComparison.OrdinalIgnoreCase))
                    onActivate();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client disconnected before sending; keep listening.
            }
            catch (Exception ex)
            {
                Log.ZLogWarning($"Single-instance listener on {_pipeName} failed: {ex.Message}");
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                    await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();
        if (WindowsIdentity.GetCurrent().User is { } user)
        {
            security.AddAccessRule(
                new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow)
            );
        }

        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow
            )
        );

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        StopListener();

        if (_mutex is not null)
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex not owned on this thread.
                }
            }

            _mutex.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
