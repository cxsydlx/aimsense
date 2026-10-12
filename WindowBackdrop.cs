using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AimSense;

// WinUI 风格的窗口外框：圆角 + 浅色标题栏。
// 刻意不使用 DWM 的 Mica 系统背景材质：Mica 取样的是桌面壁纸，遇到深色/纯黑壁纸时
// 客户区会变成深色，而本应用是浅色 Fluent 配色，深色文字会直接看不见。
// 因此这里保持 XAML 里的实色底（#F3F3F3），在任意壁纸下都稳定可读。
internal static class WindowBackdrop
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int CornerRound = 2;

    public static void ApplyFrame(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int light = 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref light, sizeof(int));
        int corner = CornerRound;
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));
    }

    // 全屏测试会改写 WindowStyle，退出后重新贴一次外框属性。
    public static void Reapply(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int light = 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref light, sizeof(int));
        int corner = CornerRound;
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
