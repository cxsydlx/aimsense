using System.Globalization;
using System.Text;

namespace AimSense;

internal static class RecommendationReport
{
    public static string Build(GameProfile game, double dpi, IEnumerable<ModeSearch> searches,
        CompositeValidation? validation, string? tracePath = null, bool legacyDiagnosticsText = false)
    {
        string DescribeResult(BlockResult result) => Describe(result,legacyDiagnosticsText);
        var text = new StringBuilder();
        text.AppendLine("表现分0–100，越高越好；表现分不等于命中率。以下只使用有效计分块，预告、适应、休息不计分。");
        text.AppendLine("点击：100 − 75×失败比例 − 25×完成时间比例；错点/超时的时间比例记1。");
        text.AppendLine("跟随：100 − 75×离靶时间比例 − 25×min(平均角度误差÷头部角半径÷3, 1)。");
        foreach (var search in searches)
        {
            text.AppendLine(); text.AppendLine($"{Name(search.Mode)} · {search.StopReason}");
            int valid = search.Results.Count(r => r.Valid);
            text.AppendLine(search.Mode == TrialMode.Track ? $"有效块 {valid}/{search.Results.Count}；有效跟随计分 {search.Results.Where(r => r.Valid).Sum(r => r.Block.DurationSeconds):0} 秒。"
                : $"有效块 {valid}/{search.Results.Count}；有效命中/点击 {search.Results.Where(r => r.Valid).Sum(r => r.Hits)}/{search.Results.Where(r => r.Valid).Sum(r => r.Hits + r.Misses + r.Timeouts)}。");
            if (search.HasEvidence) text.AppendLine($"分项候选：{Convert(game, dpi, search.BestCm360)}");
            else text.AppendLine("当前证据不足，不能给出该项灵敏度转换建议。");
            foreach (var block in search.Results)
                text.AppendLine($"  逐块记录 {block.Request.Id} · 脚本{block.Request.ScriptId} · {F(block.Request.Cm360)} cm/360：{DescribeResult(block)}");
            foreach (var comparison in search.Comparisons)
            {
                text.AppendLine($"{comparison.Stage}：{F(comparison.BaselineCm360)} → {F(comparison.CandidateCm360)} cm/360（{Direction(comparison.BaselineCm360, comparison.CandidateCm360)}）。{comparison.Reason}。");
                if (comparison.Complete)
                    text.AppendLine($"  同脚本表现分变化：{string.Join("、", comparison.PairedImprovements.Select(Signed))}；均值 {Signed(comparison.MeanImprovement)} 分；{(comparison.Accepted ? "接受此调整" : "未接受此调整")}。");
                foreach (int script in comparison.ScriptIds)
                {
                    var a = search.Results.LastOrDefault(r => r.Request.ScriptId == script && PairedEvidence.Same(r.Request.Cm360, comparison.BaselineCm360));
                    var b = search.Results.LastOrDefault(r => r.Request.ScriptId == script && PairedEvidence.Same(r.Request.Cm360, comparison.CandidateCm360));
                    if (a != null && b != null) text.AppendLine($"  脚本 {script}：{DescribeResult(a)} → {DescribeResult(b)}");
                }
            }
            foreach (var invalid in search.Results.Where(r => !r.Valid))
                text.AppendLine($"排除块 {invalid.Request.Id}：{string.Join("；", invalid.InvalidReasons)}。其错点/超时不被当作输入故障。");
        }
        text.AppendLine();
        if (validation == null || !validation.HasCompleteEvidence)
            text.AppendLine("综合建议：缺少三项完整、有效的独立复核证据，仅提供上述分项候选。");
        else
        {
            double best = validation.BestCm360!.Value;
            text.AppendLine($"综合复核：点击 {Percent(game.Weight(TrialMode.Click))}、移动点击 {Percent(game.Weight(TrialMode.MovingClick))}、跟随 {Percent(game.Weight(TrialMode.Track))}；全新脚本6/7，共18块。");
            foreach (double cm in validation.Candidates)
            {
                text.AppendLine($"{F(cm)} cm/360 · 加权表现 {F(validation.WeightedScore(cm))} 分");
                foreach (var mode in Enum.GetValues<TrialMode>())
                    foreach (var result in validation.Results.Where(r => r.Request.Mode == mode && PairedEvidence.Same(r.Request.Cm360, cm)))
                        text.AppendLine($"  {Name(mode)} 脚本{result.Request.ScriptId}：{DescribeResult(result)}");
            }
            if (validation.Ambiguous)
                text.AppendLine($"综合候选差距未达到可重复改善门槛，保留 {F(validation.Candidates.Min())}–{F(validation.Candidates.Max())} cm/360，暂不确认统一档位。");
            else text.AppendLine($"预算内综合较好候选：{Convert(game, dpi, best)}。两套独立复核加权分均改善，平均至少提高3分。");
            foreach (var conflict in validation.Conflicts)
                text.AppendLine($"取舍提示：综合较高分档 {F(best)} 在{ Name(conflict.Mode)}上，比 {F(conflict.AlternativeCm360)} cm/360 分别低 {F(conflict.Deficits[0])}/{F(conflict.Deficits[1])} 分，两脚本均超过8分；不能称为三项统一最优。");
        }
        text.AppendLine($"换算依据：灵敏度 = 360×2.54÷(DPI×cm/360×{game.YawDegreesPerCount.ToString(CultureInfo.InvariantCulture)})；eDPI = DPI×灵敏度。cm/360越大，灵敏度越低。");
        text.AppendLine($"评分版本 {CalibrationScoring.Version}；游戏腰射常数 {game.SourceVersion}，来源 {game.Source}。换向响应表示动作跟随反应，不是硬件输入延迟。");
        if (!string.IsNullOrEmpty(tracePath)) text.AppendLine($"可重算轨迹与逐块依据：{tracePath}");
        return text.ToString();
    }

    public static string Name(TrialMode mode) => mode switch { TrialMode.Click => "点击", TrialMode.Track => "跟随", _ => "移动点击" };
    private static string Direction(double a, double b) => b > a ? "降低灵敏度" : "提高灵敏度";
    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Signed(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    private static string Percent(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    private static string Convert(GameProfile game, double dpi, double cm) =>
        FormattableString.Invariant($"{F(cm)} cm/360 · {game.Id} · {dpi:0} DPI · 游戏灵敏度 {game.Sensitivity(dpi, cm):0.0000} · eDPI {game.Edpi(dpi, cm):0.0}");
    private static string Describe(BlockResult result,bool legacyDiagnosticsText)
    {
        if (!result.Valid) return $"采样无效（{string.Join("；", result.InvalidReasons)}）";
        var d = result.Diagnostics;
        string performance = result.Request.Mode == TrialMode.Track ? $"贴靶 {Percent(result.TrackingShare)}，角误差 {F(result.MeanAngularErrorRadii)} 个头部半径"
            : $"命中 {result.Hits}/{result.Hits + result.Misses + result.Timeouts}，错点 {result.Misses}，超时 {result.Timeouts}，命中耗时 {(result.Hits > 0 ? F(result.MeanShotSeconds) + "秒" : "无有效命中")}";
        var loss = result.Breakdown;
        string deduction = result.Request.Mode == TrialMode.Track ? $"离靶扣 {F(loss.OffTargetLoss)}、误差扣 {F(loss.AngularErrorLoss)}" : $"失败扣 {F(loss.FailureLoss)}、耗时扣 {F(loss.TimeLoss)}";
        string correction=result.Request.Mode==TrialMode.Track && !legacyDiagnosticsText ? "回拉未统计" : $"回拉 {d.Corrections}";
        return $"{F(result.Score)}分（{deduction}），{performance}，过冲 {d.Overshoots}，{correction}，恢复 {Time(d.MeanRecoverySeconds, d.RecoverySamples)}，换向响应 {Time(d.MeanTurnResponseSeconds, d.TurnSamples)}";
    }
    private static string Time(double? seconds, int count) => seconds.HasValue ? FormattableString.Invariant($"{seconds.Value * 1000:0}ms/{count}次") : "无可测事件";
}
