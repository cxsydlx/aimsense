using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace AimSense;

public sealed record MouseDevice(nint Handle, string DisplayName)
{
    // 自定义 ComboBox 模板在同一处显示选中项，ToString 兜底保证设备名始终可读。
    public override string ToString() => DisplayName;
}

public sealed class RawMouseInput : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const int RimTypeMouse = 0;
    private readonly HwndSource _source;
    private nint _memory;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RawInputHeader>();
    public event Action<nint, int, int, long>? Moved;
    public event Action<nint, long>? PrimaryPressed;
    public event Action? DevicesChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList { public nint Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nint WParam; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        private ushort Padding;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? data, ref uint size);

    public RawMouseInput(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
        var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x00002000, Target = source.Handle } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _source.RemoveHook(WndProc);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        _memory = Marshal.AllocHGlobal(4096);
    }

    public static IReadOnlyList<MouseDevice> Enumerate()
    {
        var devices = new List<MouseDevice> { new(0, "自动识别 · 当前移动的鼠标") };
        uint count = 0;
        uint stride = (uint)Marshal.SizeOf<RawInputDeviceList>();
        GetRawInputDeviceList(null, ref count, stride);
        if (count == 0 || count > 1024) return devices;
        var list = new RawInputDeviceList[count];
        uint actual = GetRawInputDeviceList(list, ref count, stride);
        if (actual == uint.MaxValue) return devices;
        for (int i = 0; i < actual; i++)
        {
            if (list[i].Type != RimTypeMouse) continue;
            uint chars = 0;
            GetRawInputDeviceInfo(list[i].Device, RidiDeviceName, null, ref chars);
            if (chars is 0 or > 4096) continue;
            var buffer = new StringBuilder((int)chars);
            if (GetRawInputDeviceInfo(list[i].Device, RidiDeviceName, buffer, ref chars) == uint.MaxValue) continue;
            string path = buffer.ToString();
            var match = System.Text.RegularExpressions.Regex.Match(path, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string name = match.Success ? $"鼠标 · VID {match.Groups[1].Value.ToUpperInvariant()} / PID {match.Groups[2].Value.ToUpperInvariant()}" : $"鼠标设备 {devices.Count}";
            devices.Add(new MouseDevice(list[i].Device, name));
        }
        return devices;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmInputDeviceChange) { DevicesChanged?.Invoke(); return 0; }
        if (msg != WmInput) return 0;
        uint size = 4096;
        uint read = GetRawInputData(lParam, RidInput, _memory, ref size, HeaderSize);
        if (read == uint.MaxValue || read < HeaderSize + Marshal.SizeOf<RawMouse>()) return 0;
            var header = Marshal.PtrToStructure<RawInputHeader>(_memory);
            if (header.Type != RimTypeMouse) return 0;
            var mouse = Marshal.PtrToStructure<RawMouse>(_memory + (int)HeaderSize);
            if ((mouse.Flags & 1) != 0) return 0; // Absolute coordinates are not relative mouse counts.
            long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mouse.LastX != 0 || mouse.LastY != 0)
                Moved?.Invoke(header.Device, mouse.LastX, mouse.LastY, timestamp);
            if ((mouse.Buttons & 1) != 0) PrimaryPressed?.Invoke(header.Device, timestamp);
        return 0; // Let WPF and DefWindowProc finish normal WM_INPUT processing.
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        if (_memory != 0) { Marshal.FreeHGlobal(_memory); _memory = 0; }
    }
}
