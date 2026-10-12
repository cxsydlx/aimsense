using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AimSense;

internal sealed record DisplayRecoverySnapshot(int ParentId, long ParentStartTicks, DisplayMode Original, DisplayMode Requested);

internal static class DisplayRecoveryProtocol
{
    internal const int HeartbeatTimeoutMs = 8000;
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    internal static bool IsValid(DisplayRecoverySnapshot snapshot) =>
        snapshot.ParentId > 0 && snapshot.ParentStartTicks > 0 &&
        snapshot.Original.DeviceName == snapshot.Requested.DeviceName &&
        snapshot.Original.DeviceId == snapshot.Requested.DeviceId &&
        snapshot.Original.Native.Size == System.Runtime.InteropServices.Marshal.SizeOf<DisplayNativeMode>() &&
        snapshot.Requested.Native.Size == snapshot.Original.Native.Size &&
        snapshot.Original.Width > 0 && snapshot.Original.Height > 0 &&
        snapshot.Requested.Width > 0 && snapshot.Requested.Height > 0;
}

/// <summary>A separate process restores the desktop if the rendering/UI thread stops responding.</summary>
internal sealed class DisplayRecovery
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process _watchdog;
    private readonly DisplayRecoverySnapshot _snapshot;
    private readonly object _gate = new();
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private bool _finished;

    private DisplayRecovery(NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer,
        Process watchdog, DisplayRecoverySnapshot snapshot)
    { _pipe = pipe; _reader = reader; _writer = writer; _watchdog = watchdog; _snapshot = snapshot; }

    internal static DisplayRecovery Start(DisplayMode original, DisplayMode requested)
    {
        string name = "AimSense-display-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? child = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位显示恢复程序。");
            var launch = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                launch.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("无法定位应用程序程序集。"));
            launch.ArgumentList.Add("--display-watchdog"); launch.ArgumentList.Add(name);
            child = Process.Start(launch) ?? throw new InvalidOperationException("显示恢复守护进程未能启动。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var parent = Process.GetCurrentProcess();
            var snapshot = new DisplayRecoverySnapshot(Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks, original, requested);
            writer.WriteLine(JsonSerializer.Serialize(snapshot, DisplayRecoveryProtocol.Json));
            string? ready = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (ready != "READY") throw new InvalidOperationException("显示恢复守护进程未确认原模式，已取消切换。");
            return new(pipe, reader, writer, child, snapshot);
        }
        catch (Exception ex)
        {
            SafeDispose(writer); SafeDispose(reader); SafeDispose(pipe); child?.Dispose();
            throw new InvalidOperationException("无法准备显示模式恢复，未切换显示器。" + ex.Message, ex);
        }
    }

    internal void Heartbeat()
    {
        lock (_gate)
        {
            if (_finished) return;
            if (_watchdog.HasExited) throw new InvalidOperationException("显示恢复守护已退出，测试必须停止。请确认显示模式已恢复。");
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastHeartbeat, now).TotalSeconds < 1) return;
            try { _writer.WriteLine("PING"); _lastHeartbeat = now; }
            catch (IOException ex) { throw new InvalidOperationException("显示恢复守护连接中断，测试已停止。", ex); }
        }
    }

    internal DisplayRestoreResult Restore()
    {
        lock (_gate)
        {
            if (_finished) return DisplayModeService.RestoreMode(_snapshot.Original, _snapshot.Requested);
            DisplayRestoreResult? result = null;
            try
            {
                _writer.WriteLine("RESTORE");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var line = _reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line is not null) result = JsonSerializer.Deserialize<DisplayRestoreResult>(line);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
            finally
            {
                _finished = true;
                SafeDispose(_writer); SafeDispose(_reader); SafeDispose(_pipe); _watchdog.Dispose();
            }
            return result ?? DisplayModeService.RestoreMode(_snapshot.Original, _snapshot.Requested);
        }
    }

    internal static async Task<int> RunWatchdogAsync(string pipeName)
    {
        if (!pipeName.StartsWith("AimSense-display-", StringComparison.Ordinal) || pipeName.Length != "AimSense-display-".Length + 32)
            return 2;
        DisplayRecoverySnapshot? snapshot = null;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        StreamWriter? writer = null;
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            {
                var init = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                snapshot = init is null ? null : JsonSerializer.Deserialize<DisplayRecoverySnapshot>(init, DisplayRecoveryProtocol.Json);
            }
            if (snapshot is null || !DisplayRecoveryProtocol.IsValid(snapshot)) return 2;
            using var parent = Process.GetProcessById(snapshot.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != snapshot.ParentStartTicks) return 2;
            await writer.WriteLineAsync("READY").ConfigureAwait(false);
            await WaitForRecoveryAsync(reader, parent.WaitForExitAsync(), DisplayRecoveryProtocol.HeartbeatTimeoutMs).ConfigureAwait(false);
            DisplayNative.ClipCursor(0);
            var restored = DisplayModeService.RestoreMode(snapshot.Original, snapshot.Requested);
            await writer.WriteLineAsync(JsonSerializer.Serialize(restored)).ConfigureAwait(false);
            return restored.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or ArgumentException or JsonException)
        {
            if (snapshot is not null && DisplayRecoveryProtocol.IsValid(snapshot))
            {
                DisplayNative.ClipCursor(0);
                return DisplayModeService.RestoreMode(snapshot.Original, snapshot.Requested).Success ? 0 : 1;
            }
            return 2;
        }
        finally { SafeDispose(writer); }
    }

    internal static async Task<string> WaitForRecoveryAsync(StreamReader reader, Task parentExit, int heartbeatTimeoutMs)
    {
        while (true)
        {
            var read = reader.ReadLineAsync();
            var first = await Task.WhenAny(read, parentExit, Task.Delay(heartbeatTimeoutMs)).ConfigureAwait(false);
            if (first == parentExit) return "parent-exited";
            if (first != read) return "heartbeat-timeout";
            string? command = await read.ConfigureAwait(false);
            if (command != "PING") return command == "RESTORE" ? "requested" : "disconnected";
        }
    }

    private static void SafeDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (IOException) { }
    }
}
