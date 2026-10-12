using System.Configuration;
using System.Data;
using System.Windows;

namespace AimSense;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--analyze-trace")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Length != 3) { Shutdown(2); return; }
            try { var result = await Task.Run(() => TraceImportService.Read(e.Args[1])); await System.IO.File.WriteAllTextAsync(e.Args[2], result.Text); Shutdown(0); }
            catch (Exception ex) { await System.IO.File.WriteAllTextAsync(e.Args[2], "解析失败：" + ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--verify-trace")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Length != 2) { Shutdown(2); return; }
            string outputPath = e.Args[1] + ".verification.txt";
            try { await System.IO.File.WriteAllTextAsync(outputPath, TraceReplay.Verify(e.Args[1])); Shutdown(0); }
            catch (Exception ex) { await System.IO.File.WriteAllTextAsync(outputPath, "Verification failed: " + ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--display-watchdog")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = e.Args.Length == 2 ? await DisplayRecovery.RunWatchdogAsync(e.Args[1]) : 2;
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--kin-parity")
        {
            // 逐帧运动学对拍：按 trace 重算 Kin*，与 ml/export_dataset.py 的输出逐项比对。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await System.IO.File.WriteAllTextAsync(e.Args[2] + ".summary.txt", KinParityTool.Run(e.Args[1], e.Args[2]));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await System.IO.File.WriteAllTextAsync(e.Args[2] + ".summary.txt", "对拍失败：" + ex);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--label-selftest")
        {
            // 标签链路自检：写入 → 读回 → 复测判定，全程在临时目录，不碰真实标签与 trace。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await System.IO.File.WriteAllTextAsync(e.Args[1], LabelSelfTest.Run(e.Args[1]));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await System.IO.File.WriteAllTextAsync(e.Args[1], ex is InvalidOperationException ? ex.Message : "自检异常：" + ex);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--ui-preview")
        {
            // 外观自检：把主界面渲染成 PNG 后退出，不写任何标签与 trace。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var preview = new MainWindow();
                preview.Show();
                await preview.RenderPreviewAsync(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await System.IO.File.WriteAllTextAsync(e.Args[1] + ".error.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--render-bench")
        {
            // 渲染基准：量 FPS/P95 + 导出当前画面 PNG（不写 trace、不计分）。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var window = new AimSense.MainWindow();
                window.Show();
                int seconds = e.Args.Length > 2 && int.TryParse(e.Args[2], out int parsed) ? parsed : 6;
                double distance = e.Args.Length > 3 && double.TryParse(e.Args[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedDistance) ? parsedDistance : 10;
                string report = await window.RunRenderBenchAsync(e.Args[1], seconds, distance);
                await System.IO.File.WriteAllTextAsync(e.Args[1] + ".txt", report);
                window.RequestClose();
                Shutdown(0);            }
            catch (Exception ex)
            {
                await System.IO.File.WriteAllTextAsync(e.Args[1] + ".error.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--ui-shot")
        {
            // 真实启动态截图：不加示例数据，用于核对实际底色与滚动位置。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var window = new MainWindow();
                window.Show();
                await window.RenderLiveAsync(e.Args[1], e.Args.Length > 2 && int.TryParse(e.Args[2], out int delay) ? delay : 2500);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await System.IO.File.WriteAllTextAsync(e.Args[1] + ".error.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--import-trace") ((MainWindow)MainWindow).OpenTraceImport(e.Args[1]);
    }
}

