using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimSense;

// 项目数据目录（与服务端无关，纯本机）。
internal static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimSense");
}

public sealed record UploadSession(string Game, double Dpi, double Fov, double DistanceMeters,
    IReadOnlyList<string> Modes, int DisplayWidth, int DisplayHeight, int DisplayRefreshHz,
    string ScoreVersion, string MotionVersion, string EvidenceVersion, double Low, double High, string StartedDate);

public sealed record UploadCandidate(double Cm360, IReadOnlyDictionary<string, double> Features,
    double? MeanScore, int ValidBlocks, bool FeaturesComplete, IReadOnlyDictionary<string, double>? BlockFeatures = null);

public sealed record UploadRequest(UploadSession? Session, IReadOnlyList<UploadCandidate> Candidates,
    IReadOnlyList<(string Kind, JsonElement Record)> Labels, SurveyLabel? Survey);

// 上传包的**本地构建器**：只负责把"将会发送什么"拼出来，供用户预览与导出。
//
// ⚠️ 本文件没有任何网络代码（无 HttpClient、无 Socket、无 WebRequest）。真正的"上传"尚未实现，
// 必须先由用户对**定稿的**告知书逐项同意后才做（见《数据字段清单与数据流》§5）。
//
// 设计要点（对应清单 §3）：
//  1. 每个分区都用**白名单**逐字段抄写，绝不整体序列化对象——这样"新增字段"不会静默进入上传包。
//  2. 构建完成后再跑一次**禁传键递归扫描**，发现即抛异常（宁可崩在本地，也不发出脏包）。
//  3. `Redactions` 显式列出被剥掉的字段，供服务端二次校验。
internal static class UploadPackage
{
    public const int SchemaVersion = 1;

    // 告知书尚未起草/定稿：这个常量必须保持"草稿"，UI 也要照原样显示。
    public const string ConsentVersionDraft = "draft-未定稿（未征得任何同意，禁止上传）";

    public const string InstallIdFileName = "install-id.txt";

    // ③禁传键：上传包任何层级出现这些键名即视为严重错误。
    // 覆盖逐帧轨迹、设备标识、屏幕几何、计时/显卡指纹，以及各类本地路径与自由文本。
    public static readonly string[] ForbiddenKeys =
    [
        "TracePath", "ResumedFrom", "SourcePath", "SessionId", "Path", "File", "Directory",
        "DeviceName", "DeviceId", "Handle", "MouseDevice", "DisplayName",
        "Client", "WindowDpi", "SurfaceWidth", "SurfaceHeight", "Bounds", "Native",
        "Samples", "Events", "Tick", "CameraYaw", "CameraPitch", "HeadX", "HeadY", "HeadZ",
        "RootX", "RootY", "RootZ", "FacingYaw", "GaitPhase", "Crouch", "DX", "DY",
        "Presents", "VSync", "Device", "P95Ms", "NativeFps", "CallbackFps", "FrameMs",
        "Note", "UserName", "MachineName", "Rank", "OSVersion",
    ];

    // 会被剥掉、并在包内 Redactions 里显式声明的字段（给服务端做二次校验用）。
    public static readonly string[] RedactionList =
    [
        "labels[*].TracePath", "labels[*].SessionId（改为不可逆的 SessionKey）", "labels[*].Note（自由文本，一律不上传）",
        "session.ResumedFrom", "session.Display.DeviceName", "session.Display.DeviceId", "session.Display.Bounds",
        "displayVerified.Client", "displayVerified.WindowDpi", "displayVerified.SurfaceWidth", "displayVerified.SurfaceHeight",
        "sampleBatch.Samples", "sampleBatch.Events", "frame.Device", "frame.Presents", "frame.VSync", "frame.P95Ms",
        "mouse.Handle", "mouse.DisplayName", "match.Rank（改为数值评分 Rating）", "survey.Note",
    ];

    public static string InstallIdPath => Path.Combine(OverrideDirectory ?? AppPaths.DataDirectory, InstallIdFileName);

    // 仅自检使用：把安装 ID 写到临时目录，确保自检不触碰真实数据目录。
    internal static string? OverrideDirectory;

    // 随机安装 ID：可重置、可销毁；不采集任何硬件指纹，因此它不与"这台机器"绑定。
    public static string InstallId()
    {
        try
        {
            if (File.Exists(InstallIdPath))
            {
                string existing = File.ReadAllText(InstallIdPath).Trim();
                if (existing.Length == 32) return existing;
            }
            string created = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(InstallIdPath)!);
            File.WriteAllText(InstallIdPath, created);
            return created;
        }
        catch { return "00000000000000000000000000000000"; }
    }

    // 重置 = 换一个新 ID，从此与历史数据断开（旧数据需要另行请求删除）。
    public static string ResetInstallId()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstallIdPath)!);
            string created = Guid.NewGuid().ToString("N");
            File.WriteAllText(InstallIdPath, created);
            return created;
        }
        catch { return "00000000000000000000000000000000"; }
    }

    // 会话键：由会话标识做不可逆散列，既能在服务端把同一会话的记录归并，又不泄漏本地路径。
    public static string SessionKey(string sessionId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId ?? ""));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static JsonObject Build(UploadRequest request)
    {
        // 宁可不发，也不发空壳：实测出现过"只有一个 cm/360、0 个有效块、6 项特征"的包被静默生成。
        // 这类包对训练毫无价值，却会浪费带宽、污染数据集统计，所以在这里硬拒绝。
        UploadCandidate[] usable = request.Candidates.Where(x => x.ValidBlocks > 0 && x.FeaturesComplete).ToArray();
        if (request.Candidates.Count > 0 && usable.Length == 0)
            throw new InvalidOperationException("本次没有可用于训练的测量结果（有效块为 0 或特征不完整），已拒绝生成上传内容。");
        if (request.Candidates.Count == 0 && request.Labels.Count == 0 && request.Survey == null)
            throw new InvalidOperationException("没有任何可上传的内容（没有会话结果、也没有标签或问卷）。");

        string installId = InstallId();
        var package = new JsonObject
        {
            ["SchemaVersion"] = SchemaVersion,
            ["ConsentVersion"] = ConsentVersionDraft,
            ["ConsentScope"] = new JsonArray(Scopes(request).Select(x => (JsonNode)x).ToArray()),
            ["InstallId"] = installId,
            ["AppVersion"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ["LabelSchemaVersion"] = LabelStore.SchemaVersion,
            ["GeneratedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        };

        if (request.Session is { } session)
        {
            package["Session"] = new JsonObject
            {
                ["Game"] = session.Game,
                ["Dpi"] = session.Dpi,
                ["Fov"] = session.Fov,
                ["DistanceMeters"] = session.DistanceMeters,
                ["Modes"] = new JsonArray(session.Modes.Select(x => (JsonNode)x).ToArray()),
                ["StartedDate"] = session.StartedDate,
                ["ScoreVersion"] = session.ScoreVersion,
                ["MotionVersion"] = session.MotionVersion,
                ["EvidenceVersion"] = session.EvidenceVersion,
                // SearchRange 以前写的是**声明区间**，但块间自适应步进不受它约束，实测档位会跑到区间外
                // （实测见过声明 28.8–45 而实际测了 26.5–36）。所以拆成两个字段，各自名副其实。
                ["InitialSearchRange"] = new JsonObject { ["Low"] = session.Low, ["High"] = session.High },
                ["SearchRange"] = new JsonObject
                {
                    ["Low"] = usable.Length > 0 ? usable.Min(x => x.Cm360) : session.Low,
                    ["High"] = usable.Length > 0 ? usable.Max(x => x.Cm360) : session.High,
                },
                // 只保留显示条件里影响瞄准的三项，设备标识与屏幕几何一律不带
                ["Display"] = new JsonObject
                {
                    ["Width"] = session.DisplayWidth,
                    ["Height"] = session.DisplayHeight,
                    ["RefreshHz"] = session.DisplayRefreshHz,
                },
            };
        }

        if (usable.Length > 0)   // 与上面同一把关：没有任何可用候选时不产出候选段
        {
            var array = new JsonArray();
            foreach (UploadCandidate candidate in usable)   // 只发有有效块且特征完整的候选：0 块候选会污染数据集
            {
                var item = new JsonObject
                {
                    ["cm360"] = candidate.Cm360,
                    ["MeanScore"] = candidate.MeanScore, ["ValidBlocks"] = candidate.ValidBlocks,
                    ["FeaturesComplete"] = candidate.FeaturesComplete,
                };
                // 契约特征：白名单来自 OfflineModel 的 FeatureVersion 约定，训练/推理两侧同名同义
                var features = new JsonObject();
                foreach ((string key, double value) in candidate.Features.OrderBy(x => x.Key, StringComparer.Ordinal))
                    features[key] = value;
                item["Features"] = features;
                // 块级派生层（含逐帧运动学 Kin*）：与 26 项契约特征分开，契约保持冻结。
                if (candidate.BlockFeatures is { Count: > 0 })
                {
                    var block = new JsonObject();
                    foreach ((string key, double value) in candidate.BlockFeatures.OrderBy(x => x.Key, StringComparer.Ordinal))
                        block[key] = value;
                    item["BlockFeatures"] = block;
                }
                array.Add(item);
            }
            package["Candidates"] = array;
        }

        if (request.Labels.Count > 0)
        {
            var labels = new JsonObject();
            foreach (IGrouping<string, (string Kind, JsonElement Record)> group in request.Labels.GroupBy(x => x.Kind))
            {
                var array = new JsonArray();
                foreach ((string _, JsonElement record) in group) array.Add(MapLabel(group.Key, record));
                labels[group.Key] = array;
            }
            package["Labels"] = labels;
        }

        if (request.Survey is { } survey)
        {
            package["Survey"] = new JsonObject
            {
                ["SurveyVersion"] = survey.SurveyVersion,
                ["Game"] = survey.Game,
                ["Dpi"] = survey.Dpi,
                ["AimStyle"] = survey.AimStyle,
                ["PadWidthCm"] = survey.PadWidthCm,
                ["DisplayRefreshHz"] = survey.DisplayRefreshHz,
                ["MouseModel"] = survey.MouseModel,
                ["MouseWeightGrams"] = survey.MouseWeightGrams,
                ["PadType"] = survey.PadType,
                ["Grip"] = survey.Grip,
                ["FpsYears"] = survey.FpsYears,
                ["AimTrainingMonths"] = survey.AimTrainingMonths,
                ["WeeklyHours"] = survey.WeeklyHours,
                ["AgeBracket"] = survey.AgeBracket,
            };
        }

        package["Redactions"] = new JsonArray(RedactionList.Select(x => (JsonNode)x).ToArray());

        if (FirstForbiddenKey(package) is { } offending)
            throw new InvalidOperationException($"上传包出现禁传字段，已拒绝生成：{offending}");
        return package;
    }

    private static IEnumerable<string> Scopes(UploadRequest request)
    {
        if (request.Session != null || request.Candidates.Count > 0) yield return "sessionAggregate";
        if (request.Labels.Count > 0) yield return "labels";
        if (request.Survey != null) yield return "survey";
    }

    // 逐 Kind 白名单抄写。新增标签字段不会自动进入上传包——必须在这里显式加一行。
    private static JsonObject MapLabel(string kind, JsonElement record)
    {
        var item = new JsonObject();
        string? sessionId = LabelStore.Str(record, "SessionId");
        if (sessionId != null) item["SessionKey"] = SessionKey(sessionId);
        item["Game"] = LabelStore.Str(record, "Game");
        switch (kind)
        {
            case LabelStore.FeelKind:
                item["Dpi"] = LabelStore.Num(record, "Dpi");
                item["Cm360"] = LabelStore.Num(record, "Cm360");
                item["Sensitivity"] = LabelStore.Num(record, "Sensitivity");
                item["EDpi"] = LabelStore.Num(record, "EDpi");
                item["Feel"] = LabelStore.Int(record, "Feel");
                item["ComparedToCurrent"] = LabelStore.Str(record, "ComparedToCurrent");
                item["BaselineCm360"] = LabelStore.Num(record, "BaselineCm360");
                item["ScoreVersion"] = LabelStore.Str(record, "ScoreVersion");
                break;
            case LabelStore.PreferenceKind:
                item["BetterCm360"] = LabelStore.Num(record, "BetterCm360");
                item["WorseCm360"] = LabelStore.Num(record, "WorseCm360");
                item["Basis"] = LabelStore.Str(record, "Basis");
                break;
            case LabelStore.MatchKind:
                item["Date"] = LabelStore.Str(record, "Date");
                item["Cm360"] = LabelStore.Num(record, "Cm360");
                item["Sensitivity"] = LabelStore.Num(record, "Sensitivity");
                item["TrackingHitRate"] = LabelStore.Num(record, "TrackingHitRate");
                item["HeadshotRate"] = LabelStore.Num(record, "HeadshotRate");
                item["Matches"] = LabelStore.Int(record, "Matches");
                item["Rating"] = LabelStore.Num(record, "Rating");
                break;
            case LabelStore.AdoptionKind:
                item["Cm360"] = LabelStore.Num(record, "Cm360");
                item["Sensitivity"] = LabelStore.Num(record, "Sensitivity");
                item["EDpi"] = LabelStore.Num(record, "EDpi");
                item["ScoreVersion"] = LabelStore.Str(record, "ScoreVersion");
                item["BaselineScore"] = LabelStore.Num(record, "BaselineScore");
                item["BaselineBlocks"] = LabelStore.Int(record, "BaselineBlocks");
                item["ModeScores"] = JsonSerializer.SerializeToNode(LabelStore.Modes(record));
                break;
            case LabelStore.RetestKind:
                item["AdoptionId"] = LabelStore.Str(record, "AdoptionId");
                item["Cm360"] = LabelStore.Num(record, "Cm360");
                item["BaselineScore"] = LabelStore.Num(record, "BaselineScore");
                item["RetestScore"] = LabelStore.Num(record, "RetestScore");
                item["Delta"] = LabelStore.Num(record, "Delta");
                item["ScoreVersion"] = LabelStore.Str(record, "ScoreVersion");
                item["RetestBlocks"] = LabelStore.Int(record, "RetestBlocks");
                item["Basis"] = LabelStore.Str(record, "Basis");
                break;
            case LabelStore.SessionKind:
                item["Dpi"] = LabelStore.Num(record, "Dpi");
                item["ScoreVersion"] = LabelStore.Str(record, "ScoreVersion");
                item["CenterCm"] = LabelStore.Num(record, "CenterCm");
                var summary = new JsonArray();
                if (record.TryGetProperty("Candidates", out JsonElement candidates) && candidates.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement candidate in candidates.EnumerateArray())
                        summary.Add(new JsonObject
                        {
                            ["Cm360"] = LabelStore.Num(candidate, "Cm360"),
                            ["MeanScore"] = LabelStore.Num(candidate, "MeanScore"),
                            ["ValidBlocks"] = LabelStore.Int(candidate, "ValidBlocks"),
                        });
                item["Candidates"] = summary;
                break;
        }
        return item;
    }

    // 递归扫描禁传键；返回第一个命中的路径（用于自检与构建后校验）。
    public static string? FirstForbiddenKey(JsonNode? node)
    {
        var forbidden = new HashSet<string>(ForbiddenKeys, StringComparer.OrdinalIgnoreCase);
        return Walk(node, "$");
        string? Walk(JsonNode? current, string path) => current switch
        {
            JsonObject obj => obj.Select(pair => forbidden.Contains(pair.Key)
                    ? $"{path}.{pair.Key}"
                    : Walk(pair.Value, $"{path}.{pair.Key}")).FirstOrDefault(x => x != null),
            JsonArray array => array.Select((child, index) => Walk(child, $"{path}[{index}]")).FirstOrDefault(x => x != null),
            _ => null
        };
    }
}
