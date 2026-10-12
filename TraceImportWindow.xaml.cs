using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace AimSense;
public partial class TraceImportWindow : Window
{
    private CancellationTokenSource? _cancel;
    private TraceImportResult? _result;
    public TraceImportWindow(Window owner, string? path = null)
    {
        InitializeComponent(); Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Closed += (_, _) => _cancel?.Cancel();
        if (path != null) Loaded += async (_, _) => await LoadFileAsync(path);
    }
    private async void OpenClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 AimSense JSONL 记录", Filter = "JSONL 记录|*.jsonl|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await LoadFileAsync(dialog.FileName);
    }
    private async Task LoadFileAsync(string path)
    {
        _cancel?.Cancel(); _cancel = new(); var token = _cancel.Token;
        OpenButton.IsEnabled = SaveButton.IsEnabled = ResumeButton.IsEnabled = false; _result = null; CandidatesGrid.ItemsSource = null;
        ReviewGrid.ItemsSource = null; ReviewSummaryText.Text = "正在重算18块复核明细…";
        RestartReviewButton.Visibility=Visibility.Collapsed;
        StatusText.Text = "正在解析：" + path; ReportText.Text = "大文件在后台逐行处理，请稍候…";
        try
        {
            var progress = new Progress<string>(text => { if (!token.IsCancellationRequested) StatusText.Text = text; });
            var result = await Task.Run(() => TraceImportService.Read(path, token, progress), token);
            if (token.IsCancellationRequested) return;
            _result = result; CandidatesGrid.ItemsSource = result.Candidates; ReportText.Text = result.Text;
            ReviewGrid.ItemsSource = result.ReviewRows; ReviewSummaryText.Text = result.ReviewSummary;
            ResumeButton.IsEnabled = result.ResumeState is {CanContinue:true};
            ResumeButton.Content = result.ResumeState?.Validation is {} validation && !validation.IsComplete
                ? $"继续剩余{18-validation.Results.Count}块" : "继续未完成测试";
            if(result.ResumeState is {CanContinue:true} state)
            {
                ReviewSummaryText.Text += $"\n可继续：{state.Game.Id} · {state.Dpi:0} DPI · FOV {state.Fov:0}° · {state.Display.Width}×{state.Display.Height} / {state.Display.RefreshHz:0.##}Hz。请保持实际鼠标DPI不变。点击继续后先检查输入，原文件保留、续测另存。";
                if(Owner is MainWindow main && main.ResumeDisplayError(state) is {} unavailable)
                {
                    ResumeButton.IsEnabled=false; ResumeButton.Content="原显示条件不可用";ReviewSummaryText.Text+="\n"+unavailable;
                    if(state.Modes.Length==3 &&state.Searches.Count==3 &&state.Searches.All(s=>s.HasEvidence&&s.IsComplete))
                    {
                        RestartReviewButton.Visibility=Visibility.Visible;
                        ReviewSummaryText.Text+="\n也可主动选择按主界面显示设置重做18块复核：原分项只作候选，旧复核成绩保留在历史、不混入新比较；不会只补剩余15块。";
                    }
                }
            }
            StatusText.Text = $"{result.Path}\n已重算 {result.VerifiedBlocks} 块 · 综合复核 {result.ValidationBlocks}/18 · {(result.CompositeConfirmed ? "综合候选已有完整证据" : "请查看分项候选与缺失原因")}";
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "解析未完成"; ReviewSummaryText.Text = "无法可靠解析此记录：" + ex.Message; ReportText.Text = ReviewSummaryText.Text; }
        finally { OpenButton.IsEnabled = true; }
    }
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dialog = new SaveFileDialog { Title = "导出解析报告", Filter = "文本报告|*.txt", FileName = Path.GetFileNameWithoutExtension(_result.Path) + "-解析报告.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, _result.Text); StatusText.Text = "已导出：" + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
    private async void ResumeClicked(object sender,RoutedEventArgs e)
    {
        if(_result==null || Owner is not MainWindow main) return;
        string path=_result.Path; ResumeButton.IsEnabled=OpenButton.IsEnabled=false;
        try
        {
            var token=_cancel?.Token??default;
            var fresh=await Task.Run(()=>TraceImportService.Read(path,token),token);
            if(fresh.ResumeState is not {CanContinue:true}) throw new InvalidOperationException("没有可以继续的未完成块；完整复核中无效块不会被假装成有效成绩。");
            Close(); await main.ContinueTraceAsync(fresh);
        }
        catch(OperationCanceledException) {}
        catch(Exception ex) { MessageBox.Show(main,ex.Message,"无法续测",MessageBoxButton.OK,MessageBoxImage.Information); }
        finally { if(IsVisible) {OpenButton.IsEnabled=true;ResumeButton.IsEnabled=_result?.ResumeState is {CanContinue:true};} }
    }
    private async void RestartReviewClicked(object sender,RoutedEventArgs e)
    {
        if(_result==null ||Owner is not MainWindow main) return;
        var path=_result.Path;RestartReviewButton.IsEnabled=OpenButton.IsEnabled=false;
        try
        {
            var token=_cancel?.Token??default;
            var fresh=await Task.Run(()=>TraceImportService.Read(path,token),token);
            Close();await main.RestartReviewWithCurrentDisplayAsync(fresh);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){MessageBox.Show(main,ex.Message,"无法重新复核",MessageBoxButton.OK,MessageBoxImage.Information);}
        finally{if(IsVisible){RestartReviewButton.IsEnabled=OpenButton.IsEnabled=true;}}
    }
}
