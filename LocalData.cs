using System.IO;

namespace AimSense;

internal sealed record LocalDataSummary(int TraceFiles, long TraceBytes, int HistoryFiles, long HistoryBytes,
    int LabelRecords, long LabelBytes, int PreviewFiles, long PreviewBytes);

// 本机数据清点与删除：把"撤回同意"落到实处。
// 服务端目前不存在，所以"删除"只作用于本机；上传功能真正上线后，撤回还必须配合服务端删除接口
// （见《告知书与用户协议-草稿》第 6 条与《数据字段清单与数据流》§5）。
// 所有方法都接受 root 参数，便于自检在临时目录里验证"只删勾选类别"。
internal static class LocalData
{
    public const string LabelFileName = "labels-v1.jsonl";

    public static LocalDataSummary Summarize(string root)
    {
        (int Count, long Bytes) Measure(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) return (0, 0);
            int count = 0; long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                count++;
                try { bytes += new FileInfo(file).Length; } catch { /* 统计失败不影响清点 */ }
            }
            return (count, bytes);
        }
        (int traces, long traceBytes) = Measure(Path.Combine(root, "traces"), "*.jsonl");
        (int history, long historyBytes) = Measure(Path.Combine(root, "history-v2"), "*.json");
        (int previews, long previewBytes) = Measure(root, "upload-preview-*.json");
        int labelRecords = 0; long labelBytes = 0;
        string labels = Path.Combine(root, "labels", LabelFileName);
        if (File.Exists(labels))
        {
            try
            {
                labelBytes = new FileInfo(labels).Length;
                labelRecords = File.ReadLines(labels).Count(line => line.Trim().Length > 0);
            }
            catch { /* 读不到就按 0 计 */ }
        }
        return new(traces, traceBytes, history, historyBytes, labelRecords, labelBytes, previews, previewBytes);
    }

    public static string Describe(LocalDataSummary s) =>
        $"轨迹 {s.TraceFiles} 个（{Size(s.TraceBytes)}）· 历史记录 {s.HistoryFiles} 个（{Size(s.HistoryBytes)}）· " +
        $"标签 {s.LabelRecords} 条（{Size(s.LabelBytes)}）· 上传预览导出 {s.PreviewFiles} 个（{Size(s.PreviewBytes)}）";

    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";

    // 只删调用方明确勾选的类别；逐项失败不抛出，改为写进明细，避免半途中断后状态不明。
    public static (int Files, List<string> Details) Delete(string root, bool labels, bool traces, bool history, bool previews, bool installId)
    {
        int files = 0; var details = new List<string>();
        void RemoveOne(string path, string label)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path); files++; details.Add(label + "：1 个文件");
            }
            catch (Exception ex) { details.Add($"{label}删除失败：{ex.Message}"); }
        }
        void RemoveMany(string directory, string pattern, string label)
        {
            if (!Directory.Exists(directory)) return;
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                try { File.Delete(file); count++; files++; }
                catch (Exception ex) { details.Add($"{label}删除失败：{ex.Message}"); }
            }
            if (count > 0) details.Add($"{label}：{count} 个文件");
        }
        if (labels) RemoveOne(Path.Combine(root, "labels", LabelFileName), "标签文件");
        if (traces) RemoveMany(Path.Combine(root, "traces"), "*.jsonl", "轨迹文件");
        if (history)
        {
            RemoveMany(Path.Combine(root, "history-v2"), "*.json", "历史记录");
            RemoveOne(Path.Combine(root, "history.json"), "旧版历史记录");
        }
        if (previews) RemoveMany(root, "upload-preview-*.json", "上传预览导出");
        if (installId) RemoveOne(Path.Combine(root, UploadPackage.InstallIdFileName), "匿名安装 ID");
        return (files, details);
    }
}
