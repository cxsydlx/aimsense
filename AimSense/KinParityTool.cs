using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AimSense;

// --kin-parity <trace.jsonl> <out.jsonl>：按 trace 里的 sampleBatch 重算 15 项 Kin*，
// 供与 ml/export_dataset.py 的同名输出**逐项对拍**（同一条 trace 两边必须吻合）。
// 独立于界面与计分，只读 trace、只写报告文件。
internal static class KinParityTool
{
    public static string Run(string tracePath, string outPath)
    {
        var text = new StringBuilder();
        int blocks = 0, withSamples = 0;
        using (var reader = new StreamReader(tracePath))
        using (var writer = new StreamWriter(outPath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("\"Kind\":\"sampleBatch\"", StringComparison.Ordinal)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); } catch { continue; }
                using (document)
                {
                    JsonElement root = document.RootElement;
                    blocks++;
                    string blockId = root.TryGetProperty("BlockId", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
                    if (!root.TryGetProperty("Samples", out JsonElement samples) || samples.ValueKind != JsonValueKind.Array)
                    { text.AppendLine($"{blockId}\t(无逐帧样本)"); continue; }
                    var points = new List<KinSample>();
                    foreach (JsonElement sample in samples.EnumerateArray())
                    {
                        double t = Number(sample, "Time"), dx = Number(sample, "DX"), dy = Number(sample, "DY");
                        string phase = sample.TryGetProperty("Phase", out JsonElement phaseElement) && phaseElement.ValueKind == JsonValueKind.String
                            ? phaseElement.GetString() ?? "" : "";
                        points.Add(new KinSample(t, dx, dy, phase, Number(sample, "CameraYaw"), Number(sample, "CameraPitch")));
                    }
                    if (points.Count > 0) withSamples++;
                    Dictionary<string, double> kinematics = KinematicsAnalysis.FromSamples(points);
                    var row = new StringBuilder();
                    row.Append('{').Append("\"BlockId\":").Append(JsonSerializer.Serialize(blockId));
                    foreach (string key in KinematicsAnalysis.Keys)
                        if (kinematics.TryGetValue(key, out double value) && double.IsFinite(value))
                            row.Append(',').Append(JsonSerializer.Serialize(key)).Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
                    row.Append('}');
                    writer.WriteLine(row.ToString());
                }
            }
        }
        return $"对拍数据已生成：{blocks} 个 sampleBatch（其中 {withSamples} 个含逐帧样本）→ {outPath}\n"
            + $"下一步：与 ml/out/blocks.jsonl 的同名 Kin* 逐项比对（计数类必须相等，均值类 1e-6 相对容差）。";
    }

    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return double.NaN;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => double.NaN,
        };
    }
}
