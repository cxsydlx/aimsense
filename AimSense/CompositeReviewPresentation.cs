namespace AimSense;

internal sealed record ReviewProgressRow(string Mode, string Gear, double Cm360, double Sensitivity, double EDpi,
    string FirstPass, string SecondPass, string Progress);

internal static class CompositeReviewPresentation
{
    public const string Plan = "综合复核仍是3项测试：点击、跟随、移动点击。\n每项使用A、B、C三个档位，每档做第1遍和第2遍（不同动作脚本）。\n每项3档 × 2遍 = 6块；三项合计18块，约5分钟。\n每块2秒预告 + 2秒适应 + 12秒计分。档位顺序交错；完整有效复核后才比较统一设置。";
    public static IReadOnlyList<ReviewProgressRow> Rows(CompositeValidation review, double dpi) =>
        Enum.GetValues<TrialMode>().SelectMany(mode => review.Candidates.Select((cm, index) =>
        {
            string Status(int script)
            {
                var block = review.Results.LastOrDefault(r => r.Request.Mode == mode && r.Request.ScriptId == script && PairedEvidence.Same(r.Request.Cm360, cm));
                return block == null ? "待测试" : block.Valid ? $"有效 · {block.Score:0.0}分" : "无效 · 缺有效证据";
            }
            int valid = review.Results.Count(r => r.Valid && r.Request.Mode == mode && PairedEvidence.Same(r.Request.Cm360, cm));
            return new ReviewProgressRow(RecommendationReport.Name(mode), ((char)('A' + index)).ToString(), cm,
                review.Game.Sensitivity(dpi, cm), review.Game.Edpi(dpi, cm), Status(6), Status(7), $"{valid}/2 有效");
        })).ToArray();

    public static string Progress(CompositeValidation review) =>
        string.Join(" · ", Enum.GetValues<TrialMode>().Select(m => $"{RecommendationReport.Name(m)} {review.Results.Count(r => r.Valid && r.Request.Mode == m)}/6有效"));

    public static string Summary(CompositeValidation review)
    {
        int valid = review.Results.Count(r => r.Valid);
        return $"综合复核：完成 {review.Results.Count}/18块，有效 {valid}/18块，还缺 {18-valid} 个有效块。\n{Progress(review)}\n" +
            (review.HasCompleteEvidence ? "三项复核证据齐全；候选是否可区分请看比较结果。" : "缺少完整证据，只提供分项候选，不能确认统一eDPI。");
    }

    public static string Current(CompositeValidation review, BlockRequest request)
    {
        int index = review.Candidates.ToList().FindIndex(cm => PairedEvidence.Same(cm, request.Cm360));
        return $"综合复核 第{review.Results.Count+1}/18块 · {RecommendationReport.Name(request.Mode)} · 档位{(char)('A'+index)}/3 · 第{request.ScriptId-5}/2遍";
    }

    public static string Details(CompositeValidation review, double dpi) => Plan + "\n\n" + Summary(review) + "\n" +
        string.Join("\n", Rows(review, dpi).Select(r => $"{r.Mode} · 档位{r.Gear} · {r.Cm360:0.00} cm/360 · 灵敏度{r.Sensitivity:0.0000} · eDPI {r.EDpi:0.0}\n    第1遍：{r.FirstPass}；第2遍：{r.SecondPass}"));

    public static string Consent(CompositeValidation review, double dpi) =>
        "第1阶段：三项分项搜索已结束。\n第2阶段：综合复核尚未开始，必须由你主动选择继续。\n\n" +
        string.Join("\n", review.Candidates.Select((cm,i) => $"档位{(char)('A'+i)}：{cm:0.00} cm/360 · 灵敏度 {review.Game.Sensitivity(dpi,cm):0.0000} · eDPI {review.Game.Edpi(dpi,cm):0.0}")) +
        "\n\n点击：A/B/C档各2遍，共6块。\n跟随：A/B/C档各2遍，共6块。\n移动点击：A/B/C档各2遍，共6块。\n合计18块，约5分钟；两遍使用不同新脚本。\n每块2秒预告 + 2秒适应 + 12秒计分。\n\n结束可查看已有分项结果；只有完整有效复核后，才有统一设置的比较依据。";
}
