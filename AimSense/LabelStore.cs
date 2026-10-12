using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimSense;

// 三类"公式之外"的标签，追加写入 %LOCALAPPDATA%\AimSense\labels\labels-v1.jsonl。
// 只追加、永不改写历史行，每行自带 SchemaVersion，便于离线导出训练集与事后审计。
// 读取一律容错：坏行跳过，不影响其它记录，也不影响测试流程。
internal static class LabelStore
{
    public const int SchemaVersion = 1;
    public const string FeelKind = "feel";
    public const string PreferenceKind = "preference";
    public const string MatchKind = "match";
    public const string AdoptionKind = "adoption";
    public const string RetestKind = "retest";
    public const string SessionKind = "session";
    public const string ProfileKind = "profile";

    public static string DirectoryPath => OverrideDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimSense", "labels");

    // 仅自检使用：把标签写到临时目录，确保自检绝不触碰真实标签文件。
    internal static string? OverrideDirectory;

    public static string FilePath => Path.Combine(DirectoryPath, "labels-v1.jsonl");

    private static readonly object Gate = new();

    public static string Append(string kind, object payload)
    {
        string id = Guid.NewGuid().ToString("N");
        JsonObject node = JsonSerializer.SerializeToNode(payload)?.AsObject()
            ?? throw new InvalidOperationException("标签无法序列化。");
        node["Id"] = id;
        node["Kind"] = kind;
        node["LabelSchemaVersion"] = SchemaVersion;
        node["Created"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(FilePath, node.ToJsonString() + Environment.NewLine);
        }
        return id;
    }

    public static IReadOnlyList<JsonElement> Read(string kind)
    {
        var list = new List<JsonElement>();
        if (!File.Exists(FilePath)) return list;
        try
        {
            foreach (string line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement.Clone();
                    if (root.ValueKind == JsonValueKind.Object && Str(root, "Kind") == kind) list.Add(root);
                }
                catch { /* 单行损坏不影响其它标签 */ }
            }
        }
        catch { /* 文件不可读时按"暂无标签"处理 */ }
        return list;
    }

    public static int Count(string kind) => Read(kind).Count;

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;

    public static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble()) : null;

    public static IReadOnlyDictionary<string, double> Modes(JsonElement element) => Numbers(element, "ModeScores");

    // 读取任意"对象 → 数值"字段（ModeScores / Features / BlockAggregates 都用它）。
    public static IReadOnlyDictionary<string, double> Numbers(JsonElement element, string name)
    {
        var result = new Dictionary<string, double>();
        if (element.ValueKind != JsonValueKind.Object) return result;
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object) return result;
        foreach (JsonProperty property in value.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Number) result[property.Name] = property.Value.GetDouble();
        return result;
    }
}

// 主观手感：某一档位的自评 0–10 分，以及与"开始测试时当前设置"的成对比较。
internal sealed record FeelLabel(string SessionId, string? TracePath, string Game, double Dpi, double Cm360,
    double Sensitivity, double EDpi, int Feel, string ComparedToCurrent, double BaselineCm360, string ScoreVersion, string? Note);

// 成对偏好：只写"更好/更差"这种有序对，专门给排序模型用。
internal sealed record PreferenceLabel(string SessionId, string? TracePath, string Game, double BetterCm360,
    double WorseCm360, string Basis, string? Note);

// 真实对局成绩：与软件内计分完全无关的外部标签。
internal sealed record MatchLabel(string Game, string Date, double? Cm360, double? Sensitivity, string? Rank,
    double? TrackingHitRate, double? HeadshotRate, int? Matches, double? Rating, string? Note, string? SessionId);

// 纵向留存：采用的档位 + 采用时的基线（按模式分解，便于日后同口径复测）。
internal sealed record AdoptionLabel(string SessionId, string? TracePath, string Game, double Dpi, double Cm360,
    double Sensitivity, double EDpi, string ScoreVersion, double BaselineScore, int BaselineBlocks,
    IReadOnlyDictionary<string, double> ModeScores, string? Note);

// 纵向复测：同游戏、同档位、同评分版本下的复测差值。
internal sealed record RetestLabel(string AdoptionId, string SessionId, string? TracePath, string Game, double Cm360,
    double BaselineScore, double RetestScore, double Delta, string ScoreVersion, int RetestBlocks, string Basis);

// 会话候选快照：让"手感评分 / 采用档位"在重启之后仍可补评（内存里的会话结果会随进程结束丢失）。
// Features / BlockAggregates 是后补的：**训练要吃的"jsonl 派生层"必须存在快照里**，
// 否则重启后生成的上传包会塌成只有 cm360/平均分的空壳（实测踩过）。
internal sealed record SessionCandidateLabel(double Cm360, double Sensitivity, double EDPi, double? MeanScore,
    int ValidBlocks, IReadOnlyDictionary<string, double> ModeScores,
    IReadOnlyDictionary<string, double>? Features = null, IReadOnlyDictionary<string, double>? BlockAggregates = null);

internal sealed record SessionSummaryLabel(string SessionId, string? TracePath, string Game, double Dpi,
    string ScoreVersion, double CenterCm, IReadOnlyList<SessionCandidateLabel> Candidates);

// 本地问卷（profile）：只写入本机标签文件，当前版本**没有任何上传路径**。
// 只采装备/习惯/经验/年龄区间这类分层变量，不采姓名、联系方式、精确生日、地理位置。
// 上传（含同意、撤回、删除）必须等用户明确同意后另做，见《数据字段清单与数据流》§2.F 与 §5。
public sealed record SurveyLabel(int SurveyVersion, string Game, double? Dpi, string? AimStyle, double? PadWidthCm,
    int? DisplayRefreshHz, string? MouseModel, double? MouseWeightGrams, string? PadType, string? Grip,
    double? FpsYears, double? AimTrainingMonths, double? WeeklyHours, string? AgeBracket, string? Note);
