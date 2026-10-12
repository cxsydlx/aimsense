using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AimSense;

// 纵向留存的判定规则集中在这里，保证"复测"与"采用"始终同口径：
// 同游戏 + 同档位（0.5% 容差）+ 同评分版本 + 至少一个共同测试项目，且只用有效块。
internal static class LabelAnalysis
{
    public const double CmTolerance = .005;
    public static bool SameCm(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(.01, Math.Max(Math.Abs(a), Math.Abs(b)) * CmTolerance);

    public static IReadOnlyDictionary<string, double> ModeScores(IEnumerable<BlockResult> results, double cm) =>
        results.Where(x => x.Valid && SameCm(x.Request.Cm360, cm))
            .GroupBy(x => x.Request.Mode.ToString())
            .ToDictionary(x => x.Key, x => x.Average(y => y.Score));

    public static double Mean(IReadOnlyDictionary<string, double> modes, IEnumerable<string> keys) =>
        keys.Where(modes.ContainsKey).Select(k => modes[k]).DefaultIfEmpty(double.NaN).Average();

    internal sealed record RetestView(double RetestScore, double Delta, string Basis, string Date, int Blocks);

    internal sealed record AdoptionView(string Id, string Date, string Game, double Cm360, double Sensitivity,
        double EDpi, string ScoreVersion, double BaselineScore, IReadOnlyDictionary<string, double> ModeScores,
        string? TracePath, string? SessionId, RetestView? Retest)
    {
        public bool Pending => Retest == null;
    }

    public static IReadOnlyList<AdoptionView> Adoptions(string? game = null)
    {
        var retests = LabelStore.Read(LabelStore.RetestKind)
            .GroupBy(x => LabelStore.Str(x, "AdoptionId") ?? "")
            .ToDictionary(x => x.Key, x => x.First());
        var list = new List<AdoptionView>();
        foreach (JsonElement element in LabelStore.Read(LabelStore.AdoptionKind))
        {
            string adoptionGame = LabelStore.Str(element, "Game") ?? "";
            if (game != null && !string.Equals(adoptionGame, game, StringComparison.OrdinalIgnoreCase)) continue;
            string id = LabelStore.Str(element, "Id") ?? "";
            RetestView? retest = retests.TryGetValue(id, out JsonElement value)
                ? new RetestView(LabelStore.Num(value, "RetestScore") ?? double.NaN, LabelStore.Num(value, "Delta") ?? double.NaN,
                    LabelStore.Str(value, "Basis") ?? "", LabelStore.Str(value, "Created") ?? "", LabelStore.Int(value, "RetestBlocks") ?? 0)
                : null;
            list.Add(new AdoptionView(id, LabelStore.Str(element, "Created") ?? "", adoptionGame,
                LabelStore.Num(element, "Cm360") ?? double.NaN, LabelStore.Num(element, "Sensitivity") ?? double.NaN,
                LabelStore.Num(element, "EDpi") ?? double.NaN, LabelStore.Str(element, "ScoreVersion") ?? "",
                LabelStore.Num(element, "BaselineScore") ?? double.NaN, LabelStore.Modes(element),
                LabelStore.Str(element, "TracePath"), LabelStore.Str(element, "SessionId"), retest));
        }
        return list.OrderBy(x => x.Date, StringComparer.Ordinal).ToArray();
    }

    // 最近一次会话的候选快照：用于重启后补评手感 / 补登记采用档位。
    public static SessionSummaryLabel? LastSession()
    {
        IReadOnlyList<JsonElement> records = LabelStore.Read(LabelStore.SessionKind);
        if (records.Count == 0) return null;
        JsonElement element = records[^1];
        var candidates = new List<SessionCandidateLabel>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Candidates", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in array.EnumerateArray())
                candidates.Add(new SessionCandidateLabel(LabelStore.Num(item, "Cm360") ?? 0, LabelStore.Num(item, "Sensitivity") ?? 0,
                    LabelStore.Num(item, "EDPi") ?? 0, LabelStore.Num(item, "MeanScore"), LabelStore.Int(item, "ValidBlocks") ?? 0,
                    LabelStore.Numbers(item, "ModeScores"), LabelStore.Numbers(item, "Features"), LabelStore.Numbers(item, "BlockAggregates")));
        return candidates.Count == 0 ? null : new SessionSummaryLabel(LabelStore.Str(element, "SessionId") ?? "",
            LabelStore.Str(element, "TracePath"), LabelStore.Str(element, "Game") ?? "", LabelStore.Num(element, "Dpi") ?? 0,
            LabelStore.Str(element, "ScoreVersion") ?? "", LabelStore.Num(element, "CenterCm") ?? 0, candidates);
    }

    // 最近一次填写的本地问卷（用于回填表单；写入是追加式，最后一条即最新）。
    public static SurveyLabel? LastSurvey()
    {
        IReadOnlyList<JsonElement> records = LabelStore.Read(LabelStore.ProfileKind);
        if (records.Count == 0) return null;
        JsonElement element = records[^1];
        return new SurveyLabel(LabelStore.Int(element, "SurveyVersion") ?? 0, LabelStore.Str(element, "Game") ?? "",
            LabelStore.Num(element, "Dpi"), LabelStore.Str(element, "AimStyle"), LabelStore.Num(element, "PadWidthCm"),
            LabelStore.Int(element, "DisplayRefreshHz"), LabelStore.Str(element, "MouseModel"),
            LabelStore.Num(element, "MouseWeightGrams"), LabelStore.Str(element, "PadType"), LabelStore.Str(element, "Grip"),
            LabelStore.Num(element, "FpsYears"), LabelStore.Num(element, "AimTrainingMonths"),
            LabelStore.Num(element, "WeeklyHours"), LabelStore.Str(element, "AgeBracket"), LabelStore.Str(element, "Note"));
    }

    // 本地采集规模摘要：只数文件与标签条数，不解析 trace 内容（避免 300MB 级读盘）。
    public static string LocalScaleSummary(string traceDirectory, string historyDirectory)
    {
        int traces = Directory.Exists(traceDirectory) ? Directory.EnumerateFiles(traceDirectory, "*.jsonl").Count() : 0;
        int sessions = Directory.Exists(historyDirectory) ? Directory.EnumerateFiles(historyDirectory, "*.json").Count() : 0;
        return $"本机 trace 文件 {traces} 个 · 已完成会话 {sessions} 个 · " +
            $"标签：手感 {LabelStore.Count(LabelStore.FeelKind)}、成对偏好 {LabelStore.Count(LabelStore.PreferenceKind)}、" +
            $"对局成绩 {LabelStore.Count(LabelStore.MatchKind)}、采用 {LabelStore.Count(LabelStore.AdoptionKind)}、" +
            $"复测 {LabelStore.Count(LabelStore.RetestKind)}、会话快照 {LabelStore.Count(LabelStore.SessionKind)}、" +
            $"问卷 {LabelStore.Count(LabelStore.ProfileKind)}";
    }

    // 本次会话是否复测了某个已采用档位；成立才写 retest 标签，否则只提示缺什么。
    public static (RetestLabel? Label, string? Reason) EvaluateRetest(IEnumerable<BlockResult> results, string game,
        string scoreVersion, string sessionId, string? tracePath)
    {
        var blocks = results.ToArray();
        if (blocks.Length == 0) return (null, "本次没有可用于复测的计分块。");
        foreach (AdoptionView adoption in Adoptions(game).Where(x => x.Pending).OrderByDescending(x => x.Date))
        {
            if (!string.Equals(adoption.ScoreVersion, scoreVersion, StringComparison.Ordinal))
                return (null, $"已采用档位 {F(adoption.Cm360)} cm/360 的评分版本为 {adoption.ScoreVersion}，与当前 {scoreVersion} 不同，不做跨版本比较。");
            IReadOnlyDictionary<string, double> current = ModeScores(blocks, adoption.Cm360);
            if (current.Count == 0) continue;
            string[] common = current.Keys.Where(adoption.ModeScores.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (common.Length == 0) return (null, $"复测命中 {F(adoption.Cm360)} cm/360，但没有任何共同测试项目可与基线比较。");
            double baseline = Mean(adoption.ModeScores, common), retest = Mean(current, common);
            int count = blocks.Count(x => x.Valid && SameCm(x.Request.Cm360, adoption.Cm360));
            return (new RetestLabel(adoption.Id, sessionId, tracePath, game, adoption.Cm360, baseline, retest,
                retest - baseline, scoreVersion, count, string.Join("+", common)), null);
        }
        return (null, null);
    }

    public static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);    public static string Signed(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    public static string ShortDate(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : iso;

    public static string DescribeModes(IReadOnlyDictionary<string, double> modes) =>
        modes.Count == 0 ? "无" : string.Join("、", modes.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{(x.Key == "MovingClick" ? "移动点击" : x.Key == "Click" ? "点击" : "跟随")} {F(x.Value)}"));
}
