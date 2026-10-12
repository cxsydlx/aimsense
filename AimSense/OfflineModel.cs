using System.IO;
using System.Text.Json;

namespace AimSense;

// 离线模型（影子模式）。模型文件由 ml/train.py 训练导出，C# 侧只做推理：
// 既不参与计分，也不改变任何推荐，只在报告末尾附一行"影子预测"用于对比和积累。
// 特征口径不符（FeatureVersion 不同）或文件损坏时一律拒绝加载，绝不猜。
internal sealed class OfflineModel
{
    public const string FeatureVersion = "candidate-features-1";

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimSense", "model");

    public static string FilePath => Path.Combine(DirectoryPath, "preference-v1.json");

    public string ModelVersion { get; private init; } = "";
    public string Target { get; private init; } = "";
    public string FeatureVersionTag { get; private init; } = "";
    public string ScoreVersion { get; private init; } = "";
    public string Game { get; private init; } = "";
    public int Samples { get; private init; }
    public int Groups { get; private init; }
    public double Auc { get; private init; }
    public string Notes { get; private init; } = "";
    public IReadOnlyList<string> Features { get; private init; } = [];
    private double[] _mean = [], _scale = [], _weights = [];
    private double _bias;

    public static OfflineModel? Load(string? path = null)
    {
        string file = path ?? FilePath;
        try
        {
            if (!File.Exists(file)) return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var features = new List<string>();
            if (root.TryGetProperty("Features", out JsonElement names) && names.ValueKind == JsonValueKind.Array)
                foreach (JsonElement name in names.EnumerateArray()) features.Add(name.GetString() ?? "");
            if (features.Count == 0) return null;
            var model = new OfflineModel
            {
                ModelVersion = Text(root, "ModelVersion"), Target = Text(root, "Target"),
                FeatureVersionTag = Text(root, "FeatureVersion"), ScoreVersion = Text(root, "ScoreVersion"),
                Game = Text(root, "Game"), Notes = Text(root, "Notes"),
                Samples = (int)Number(root, "Samples"), Groups = (int)Number(root, "Groups"), Auc = Number(root, "Auc"),
                Features = features
            };
            if (model.FeatureVersionTag != FeatureVersion) return null;
            model._mean = Read(root, "Mean", features.Count);
            model._scale = Read(root, "Scale", features.Count);
            model._weights = Read(root, "Weights", features.Count);
            model._bias = Number(root, "Bias");
            return model;
        }
        catch { return null; }
    }

    // 评分版本不同就不能预测：Score 的口径变了，模型学到的映射随之失效。
    public bool CanPredict(string scoreVersion) =>
        ScoreVersion.Length == 0 || string.Equals(ScoreVersion, scoreVersion, StringComparison.Ordinal);

    // 游戏不同也不预测；Game 缺省或 "ALL" 表示训练时未限定游戏（向后兼容）。
    public bool CanPredictGame(string game) =>
        Game.Length == 0 || string.Equals(Game, "ALL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Game, game, StringComparison.OrdinalIgnoreCase);

    public double Predict(IReadOnlyDictionary<string, double> features, out int missing)
    {
        missing = 0;
        double sum = _bias;
        for (int index = 0; index < Features.Count; index++)
        {
            if (!features.TryGetValue(Features[index], out double value) || !double.IsFinite(value)) { value = _mean[index]; missing++; }
            double scale = Math.Abs(_scale[index]) < 1e-12 ? 1 : _scale[index];
            sum += _weights[index] * (value - _mean[index]) / scale;
        }
        return 1 / (1 + Math.Exp(-Math.Clamp(sum, -30, 30)));
    }

    public string Describe() =>
        $"{ModelVersion}（目标 {Target}{(Game.Length > 0 ? " · 适用 " + Game : "")}）· 样本 {Samples} / 会话 {Groups} · 分组交叉验证 AUC {Auc:0.000}";

    public string Limits() => Notes.Length == 0 ? "" : Notes;

    private static double[] Read(JsonElement root, string property, int count)
    {
        var values = new double[count];
        if (!root.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return values;
        int index = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (index >= count) break;
            values[index++] = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
        }
        return values;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}
