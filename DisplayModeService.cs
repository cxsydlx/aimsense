using System.Runtime.InteropServices;

namespace AimSense;

public sealed record DisplayDevice(string DeviceName, string Label, string DeviceId, bool IsPrimary)
{
    public override string ToString() => Label;
}
public readonly record struct PhysicalDisplayBounds(int Left, int Top, int Width, int Height);
public sealed record DisplayMode(string DeviceName, string Label, string DeviceId, int Width, int Height,
    double RefreshHz, bool RefreshIsApproximate, PhysicalDisplayBounds Bounds, DisplayNativeMode Native)
{
    public override string ToString() => $"{Width} × {Height} · {(RefreshIsApproximate ? "约 " : "")}{RefreshHz:0.##} Hz";
}
public sealed record DisplayRestoreResult(bool Success, string? Error = null);

/// <summary>Changes only a selected, connected display using temporary Win32 modes.</summary>
public sealed class DisplayModeService
{
    public PhysicalDisplayBounds ReadMonitorBounds(string deviceName) => DisplayNative.ReadMonitorBounds(deviceName);
    public IReadOnlyList<DisplayDevice> EnumerateDisplays()
    {
        var result = new List<DisplayDevice>();
        for (uint i = 0; ; i++)
        {
            var device = DisplayNative.Device.Create();
            if (!DisplayNative.EnumDisplayDevices(null, i, ref device, 0)) break;
            if ((device.StateFlags & 1) == 0 || (device.StateFlags & 8) != 0) continue;
            var monitor = DisplayNative.Device.Create();
            bool found = DisplayNative.EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0);
            string name = found && !string.IsNullOrWhiteSpace(monitor.DeviceString) ? monitor.DeviceString : device.DeviceString;
            result.Add(new(device.DeviceName, $"{device.DeviceName} · {name}", found ? monitor.DeviceId : device.DeviceId,
                (device.StateFlags & 4) != 0));
        }
        return result;
    }

    public IReadOnlyList<DisplayMode> EnumerateModes(DisplayDevice display)
    {
        var current = ReadCurrent(display);
        var result = new List<DisplayMode>();
        for (int i = 0; ; i++)
        {
            var mode = DisplayNativeMode.Create();
            // Flags 0 excludes modes unsupported by the connected monitor and other orientations.
            if (!DisplayNative.EnumDisplaySettingsEx(display.DeviceName, i, ref mode, 0)) break;
            if (mode.BitsPerPel != 32 || (mode.DisplayFlags & 2) != 0 || mode.DisplayFrequency <= 1) continue;
            if (mode.PelsWidth < 640 || mode.PelsHeight < 480) continue;
            // Position belongs to the active desktop arrangement, not the enumerated timing.
            mode.PositionX = current.Native.PositionX; mode.PositionY = current.Native.PositionY;
            result.Add(CreateMode(display, mode, mode.DisplayFrequency, true));
        }
        if (!result.Any(x => SameTiming(x.Native, current.Native))) result.Add(current);
        return result.DistinctBy(m => (m.Width, m.Height, m.Native.DisplayFrequency, m.Native.DisplayOrientation, m.Native.DisplayFlags))
            .OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.RefreshHz).ToArray();
    }

    public DisplayMode ReadCurrent(DisplayDevice display)
    {
        var mode = DisplayNativeMode.Create();
        if (!DisplayNative.EnumDisplaySettingsEx(display.DeviceName, -1, ref mode, 0))
            throw new InvalidOperationException($"无法读取显示器 {display.Label} 的当前模式。");
        double? precise = DisplayNative.TryReadRefresh(display.DeviceName);
        return CreateMode(display, mode, precise ?? mode.DisplayFrequency, precise is null);
    }

    public DisplayModeLease ApplyTemporary(DisplayMode requested)
    {
        var display = EnumerateDisplays().FirstOrDefault(d => d.DeviceName == requested.DeviceName && d.DeviceId == requested.DeviceId)
            ?? throw new InvalidOperationException("所选显示器已断开，请刷新显示器列表。");
        var original = ReadCurrent(display);
        if (SameTiming(original.Native, requested.Native)) return new(original);
        if (DisplayNative.GetSystemMetrics(0x1000) != 0)
            throw new InvalidOperationException("远程桌面会话不支持切换测试显示模式，请在本机桌面使用当前模式。");
        if (DisplayNative.IsCloned(display.DeviceName))
            throw new InvalidOperationException("当前显示器使用复制显示，无法独立切换。请使用当前模式或先切换为扩展显示。");
        var supported = EnumerateModes(display).FirstOrDefault(m => SameTiming(m.Native, requested.Native))
            ?? throw new InvalidOperationException("显示器已不再支持所选分辨率和刷新率。");
        var native = supported.Native;
        int test = DisplayNative.ChangeDisplaySettingsEx(display.DeviceName, ref native, 0, 2, 0);
        if (test != 0) throw new InvalidOperationException($"显示模式验证失败：{DisplayNative.Describe(test)}");
        var recovery = DisplayRecovery.Start(original, supported);
        try
        {
            int result = DisplayNative.ChangeDisplaySettingsEx(display.DeviceName, ref native, 0, 4, 0);
            if (result != 0) throw new InvalidOperationException($"显示模式切换失败：{DisplayNative.Describe(result)}");
            var actual = ReadCurrent(display);
            if (!SameTiming(actual.Native, native) || (!supported.RefreshIsApproximate && Math.Abs(actual.RefreshHz - supported.RefreshHz) > .1))
                throw new InvalidOperationException($"显示器回读为 {actual}，与所选模式不一致。");
            return new(actual, recovery);
        }
        catch (Exception ex)
        {
            var restore = recovery.Restore();
            throw new InvalidOperationException(ex.Message + (restore.Success ? " 已恢复原显示模式。" : $" 恢复失败：{restore.Error}"), ex);
        }
    }

    internal static bool SameTiming(DisplayNativeMode a, DisplayNativeMode b) =>
        a.PelsWidth == b.PelsWidth && a.PelsHeight == b.PelsHeight && a.DisplayFrequency == b.DisplayFrequency &&
        a.BitsPerPel == b.BitsPerPel && a.DisplayOrientation == b.DisplayOrientation && a.DisplayFlags == b.DisplayFlags;

    internal static DisplayRestoreResult RestoreMode(DisplayMode original, DisplayMode requested)
    {
        try
        {
            var service = new DisplayModeService();
            var display = service.EnumerateDisplays().FirstOrDefault(x => x.DeviceName == original.DeviceName && x.DeviceId == original.DeviceId);
            if (display is null) return new(false, "原显示器已断开，未修改其他显示器。");
            var current = service.ReadCurrent(display);
            if (SameTiming(current.Native, original.Native)) return new(true);
            // Do not overwrite a display configuration changed by the user or a different program.
            if (!SameTiming(current.Native, requested.Native)) return new(false, "显示模式已被外部修改，已保留当前设置。");
            var restore = original.Native;
            int result = DisplayNative.ChangeDisplaySettingsEx(original.DeviceName, ref restore, 0, 4, 0);
            if (result == 0 && SameTiming(service.ReadCurrent(display).Native, original.Native)) return new(true);
            int fallback = DisplayNative.RestoreRegistryMode(original.DeviceName, 0, 0, 0, 0);
            return new(false, fallback == 0 ? "原模式恢复失败，已恢复该显示器的系统默认模式。" :
                $"无法恢复显示模式：{DisplayNative.Describe(result)}；默认模式：{DisplayNative.Describe(fallback)}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    private static DisplayMode CreateMode(DisplayDevice device, DisplayNativeMode native, double hz, bool approximate) =>
        new(device.DeviceName, device.Label, device.DeviceId, (int)native.PelsWidth, (int)native.PelsHeight, hz, approximate,
            new(native.PositionX, native.PositionY, (int)native.PelsWidth, (int)native.PelsHeight), native);
}

public sealed class DisplayModeLease : IDisposable
{
    private DisplayRecovery? _recovery;
    public bool IsChanged { get; }
    public DisplayMode AppliedMode { get; }
    internal DisplayModeLease(DisplayMode current, DisplayRecovery? recovery = null)
    { AppliedMode = current; _recovery = recovery; IsChanged = recovery is not null; }
    public void Heartbeat() => _recovery?.Heartbeat();
    public DisplayRestoreResult Restore()
    {
        var recovery = _recovery;
        if (recovery is null) return new(true);
        var result = recovery.Restore();
        if (result.Success) _recovery = null;
        return result;
    }
    public void Dispose() => Restore();
}

// DEVMODEW display union. Keep driver-supplied values when applying or restoring a mode.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DisplayNativeMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public ushort SpecVersion, DriverVersion, Size, DriverExtra;
    public uint Fields;
    public int PositionX, PositionY;
    public uint DisplayOrientation, DisplayFixedOutput;
    public short Color, Duplex, YResolution, TTOption, Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
    public ushort LogPixels;
    public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
    public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    internal static DisplayNativeMode Create() => new() { DeviceName = "", FormName = "", Size = (ushort)Marshal.SizeOf<DisplayNativeMode>() };
}

internal static class DisplayNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    internal static PhysicalDisplayBounds ReadMonitorBounds(string device)
    {
        PhysicalDisplayBounds? result = null;
        EnumDisplayMonitors(0, 0, (nint handle, nint dc, ref Rect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info) && string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase))
                result = new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right-info.Monitor.Left, info.Monitor.Bottom-info.Monitor.Top);
            return true;
        }, 0);
        return result ?? throw new InvalidOperationException("无法读取所选显示器的桌面边界，已取消测试。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Device
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        internal static Device Create() => new() { Size = (uint)Marshal.SizeOf<Device>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
    }
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayDevices(string? device, uint number, ref Device info, uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplaySettingsEx(string device, int number, ref DisplayNativeMode mode, uint flags);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettingsEx(string device, ref DisplayNativeMode mode, nint hwnd, uint flags, nint parameter);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    internal static extern int RestoreRegistryMode(string device, nint mode, nint hwnd, uint flags, nint parameter);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool ClipCursor(nint rectangle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct Source { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct Target
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLine;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct Path { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct Mode
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(32)] public uint VSyncNumerator;
        [FieldOffset(36)] public uint VSyncDenominator;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)] internal struct SourceName
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] Path[] paths,
        ref uint modeCount, [Out] Mode[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

    private static (Path[] Paths, Mode[] Modes)? ReadPaths()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0) return null;
            var paths = new Path[pathCount]; var modes = new Mode[modeCount];
            int result = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == 122) continue;
            if (result != 0) return null;
            return (paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        return null;
    }
    private static string? GetName(Source source)
    {
        var request = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = source.Adapter, Id = source.Id, Name = "" };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.Name : null;
    }
    internal static double? TryReadRefresh(string device)
    {
        var config = ReadPaths();
        if (config is null) return null;
        foreach (var path in config.Value.Paths.Where(p => GetName(p.Source) == device))
        {
            if (path.Target.ModeIndex < config.Value.Modes.Length)
            {
                var mode = config.Value.Modes[path.Target.ModeIndex];
                if (mode.Type == 2 && mode.VSyncDenominator > 0 && mode.VSyncNumerator > 0)
                    return mode.VSyncNumerator / (double)mode.VSyncDenominator;
            }
        }
        return null;
    }
    internal static bool IsCloned(string device)
    {
        var config = ReadPaths() ?? throw new InvalidOperationException("无法确认显示器连接拓扑，暂不切换显示模式。");
        return config.Paths.Count(p => GetName(p.Source) == device) != 1;
    }
    internal static string Describe(int code) => code switch
    {
        0 => "成功", 1 => "需要重启（不应用）", -1 => "驱动拒绝", -2 => "模式不支持", -3 => "无法保存设置",
        -4 => "标志无效", -5 => "参数无效", -6 => "显示拓扑不支持", _ => $"错误 {code}"
    };
}
