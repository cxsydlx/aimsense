using System.IO;
using System.Text;

namespace AimSense;

// 标签链路自检：写入 → 读回 → 纵向复测判定 → 去重。用 --label-selftest <报告路径> 触发。
// 全程写在临时目录（LabelStore.OverrideDirectory），不读写任何真实标签或 trace。
internal static class LabelSelfTest
{
    private const string Game = "CS2";

    public static string Run(string? reportPath = null)
    {
        // 临时目录放在**报告文件旁边**（默认工作区内），不要用 %TEMP%：
        // 受限文件策略下工作区外不可写，自检会以"权限被拒"这种与业务无关的方式失败。
        string baseDirectory = reportPath is { Length: > 0 }
            ? Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? "."
            : Path.GetTempPath();
        string directory = Path.Combine(baseDirectory, "selftest-scratch-" + Guid.NewGuid().ToString("N")[..8]);
        LabelStore.OverrideDirectory = directory;
        var text = new StringBuilder();
        int failures = 0;
        void Check(string name, bool passed, string detail)
        {
            if (!passed) failures++;
            text.AppendLine($"{(passed ? "PASS" : "FAIL")}  {name}：{detail}");
        }

        try
        {
            text.AppendLine("标签文件：" + LabelStore.FilePath);

            // 1. 主观手感 + 成对偏好
            LabelStore.Append(LabelStore.FeelKind, new FeelLabel("selftest-a", null, Game, 800, 33.12, 1.2345, 987.6, 8, "更好", 36.0, CalibrationScoring.Version, "自检"));
            LabelStore.Append(LabelStore.PreferenceKind, new PreferenceLabel("selftest-a", null, Game, 33.12, 36.0, "Overall", "自检"));
            var feels = LabelStore.Read(LabelStore.FeelKind);
            Check("手感写入/读回", feels.Count == 1 && LabelStore.Int(feels[0], "Feel") == 8 && LabelStore.Str(feels[0], "ComparedToCurrent") == "更好",
                $"条数 {feels.Count}，Feel={LabelStore.Int(feels[0], "Feel")}，比较={LabelStore.Str(feels[0], "ComparedToCurrent")}，基准={LabelStore.Num(feels[0], "BaselineCm360")}");
            Check("成对偏好写入/读回", LabelStore.Count(LabelStore.PreferenceKind) == 1,
                $"更好 {LabelStore.Num(LabelStore.Read(LabelStore.PreferenceKind)[0], "BetterCm360")} → 更差 {LabelStore.Num(LabelStore.Read(LabelStore.PreferenceKind)[0], "WorseCm360")}");

            // 2. 真实对局成绩
            LabelStore.Append(LabelStore.MatchKind, new MatchLabel(Game, "2026-10-12", 33.12, 1.2345, "黄金3", 42, 21, 18, null, "自检", "selftest-a"));
            var matches = LabelStore.Read(LabelStore.MatchKind);
            Check("对局成绩写入/读回", matches.Count == 1 && LabelStore.Num(matches[0], "TrackingHitRate") == 42 && LabelStore.Int(matches[0], "Matches") == 18,
                $"{LabelStore.Str(matches[0], "Date")} · 段位 {LabelStore.Str(matches[0], "Rank")} · 跟枪 {LabelStore.Num(matches[0], "TrackingHitRate")}% · 爆头 {LabelStore.Num(matches[0], "HeadshotRate")}%");

            // 3. 纵向留存：先登记采用，再用下一次会话复测同一档位
            var baseline = new Dictionary<string, double> { ["Click"] = 70, ["Track"] = 60 };
            LabelStore.Append(LabelStore.AdoptionKind, new AdoptionLabel("selftest-a", null, Game, 800, 33.12, 1.2345, 987.6,
                CalibrationScoring.Version, baseline.Values.Average(), 8, baseline, "自检"));
            var adoptions = LabelAnalysis.Adoptions();
            Check("采用登记为待复测", adoptions.Count == 1 && adoptions[0].Pending, $"条数 {adoptions.Count}，待复测 {adoptions.Count(x => x.Pending)}");

            BlockResult[] blocks = [Block(33.12, TrialMode.Click, 80), Block(33.12, TrialMode.Track, 70), Block(36.00, TrialMode.Click, 60)];
            var (label, reason) = LabelAnalysis.EvaluateRetest(blocks, Game, CalibrationScoring.Version, "selftest-b", null);
            Check("复测判定（同游戏/同档位/同版本）", label != null && reason == null,
                label == null ? $"未生成：{reason}" : $"基线 {LabelAnalysis.F(label.BaselineScore)} → 复测 {LabelAnalysis.F(label.RetestScore)}（Δ {LabelAnalysis.Signed(label.Delta)}，{label.Basis}，{label.RetestBlocks} 块）");
            Check("复测只用共同项目且跳过无效块", label is { Basis: "Click+Track", RetestBlocks: 2, BaselineScore: 65, RetestScore: 75 } &&
                Math.Abs(label.Delta - 10) < 1e-9,
                label == null ? "无标签" : $"基线均值 {LabelAnalysis.F(label.BaselineScore)}（Click 70 / Track 60），复测均值 {LabelAnalysis.F(label.RetestScore)}（Click 80 / Track 70）");

            if (label != null) LabelStore.Append(LabelStore.RetestKind, label);
            var afterRetest = LabelAnalysis.Adoptions();
            Check("复测后不再待复测", afterRetest.Count == 1 && !afterRetest[0].Pending && afterRetest[0].Retest != null,
                afterRetest.Count == 0 ? "无采用记录" : $"Δ {LabelAnalysis.Signed(afterRetest[0].Retest?.Delta ?? double.NaN)}");

            var (second, secondReason) = LabelAnalysis.EvaluateRetest(blocks, Game, CalibrationScoring.Version, "selftest-c", null);
            Check("已复测档位不会重复判定", second == null, secondReason ?? "无待复测项，符合预期");

            // 4. 评分版本不同必须拒绝跨版本比较
            LabelStore.Append(LabelStore.AdoptionKind, new AdoptionLabel("selftest-d", null, Game, 800, 33.12, 1.2345, 987.6,
                "angular-block-loss-v2", 65, 8, baseline, "自检"));
            var (crossVersion, crossReason) = LabelAnalysis.EvaluateRetest(blocks, Game, CalibrationScoring.Version, "selftest-e", null);
            Check("跨评分版本拒绝比较", crossVersion == null && crossReason != null && crossReason.Contains("评分版本"), crossReason ?? "未给出原因");

            // 5. 不同游戏不得混算
            var (otherGame, otherReason) = LabelAnalysis.EvaluateRetest(blocks, "Valorant", CalibrationScoring.Version, "selftest-f", null);
            Check("不同游戏不参与复测", otherGame == null, otherReason ?? "无待复测项，符合预期");

            // 6. 会话候选快照（重启后补评手感 / 补登记采用都依赖它）
            LabelStore.Append(LabelStore.SessionKind, new SessionSummaryLabel("selftest-g", null, Game, 800, CalibrationScoring.Version, 36.0,
                [new SessionCandidateLabel(31.10, 1.3152, 1052.2, 82.1, 4, new Dictionary<string, double> { ["Click"] = 82.1 }),
                 new SessionCandidateLabel(36.00, 1.1358, 908.6, 87.4, 4, new Dictionary<string, double> { ["Click"] = 87.4, ["Track"] = 80.2 },
                    new Dictionary<string, double> { ["cm360"] = 36.0, ["logCm360"] = Math.Log(36.0), ["meanScore"] = 87.4, ["validBlocks"] = 4, ["blocks"] = 4 },
                    new Dictionary<string, double> { ["blockCount"] = 4, ["overshootsPerBlock"] = 1.5 })]));
            SessionSummaryLabel? last = LabelAnalysis.LastSession();
            Check("会话候选快照读回", last is { Candidates.Count: 2 } && Math.Abs(last.CenterCm - 36.0) < 1e-9 &&
                last.Candidates[1].ValidBlocks == 4 && Math.Abs(last.Candidates[1].ModeScores["Track"] - 80.2) < 1e-9,
                last == null ? "无快照" : $"档位数 {last.Candidates.Count}，当前设置 {LabelAnalysis.F(last.CenterCm)}，第二档模式分 {LabelAnalysis.DescribeModes(last.Candidates[1].ModeScores)}");

            // 7. 损坏行必须被跳过而不是抛异常
            File.AppendAllText(LabelStore.FilePath, "{ 这不是合法 JSON" + Environment.NewLine);
            Check("坏行容错", LabelStore.Count(LabelStore.FeelKind) == 1 && LabelAnalysis.LastSession() != null,
                $"手感条数仍为 {LabelStore.Count(LabelStore.FeelKind)}，会话快照仍可读");

            // 8. 影子模型：加载、推理、缺失特征回报、特征口径不符必须拒绝
            string modelPath = Path.Combine(directory, "preference-v1.json");
            File.WriteAllText(modelPath, """
                {"ModelVersion":"preference-logreg-v1","FeatureVersion":"candidate-features-1","Target":"Feel>=7",
                 "ScoreVersion":"angular-block-loss-v3","Game":"CS2",
                 "Features":["meanScore","validBlocks"],"Mean":[80,3],"Scale":[5,1],"Weights":[1.5,-0.5],"Bias":-0.2,
                 "Samples":10,"Groups":3,"Auc":0.5,"Notes":"自检合成模型"}
                """);
            OfflineModel? model = OfflineModel.Load(modelPath);
            int missingFull = -1, missingEmpty = -1;
            double full = model?.Predict(new Dictionary<string, double> { ["meanScore"] = 85, ["validBlocks"] = 4 }, out missingFull) ?? double.NaN;
            double empty = model?.Predict(new Dictionary<string, double>(), out missingEmpty) ?? double.NaN;
            Check("影子模型加载与推理", model != null && Math.Abs(full - 0.6900) < 0.005 && missingFull == 0,
                model == null ? "未加载" : $"z=+0.80 → p={full:0.0000}（期望 0.6900），缺失 {missingFull}");
            Check("缺失特征按训练均值填充并回报", Math.Abs(empty - 0.4502) < 0.005 && missingEmpty == 2,
                $"z=-0.20 → p={empty:0.0000}（期望 0.4502），缺失 {missingEmpty}/2");
            File.WriteAllText(modelPath, """{"ModelVersion":"x","FeatureVersion":"candidate-features-2","Features":["meanScore"],"Mean":[0],"Scale":[1],"Weights":[1],"Bias":0}""");
            Check("特征口径不符拒绝加载", OfflineModel.Load(modelPath) == null, "FeatureVersion 不匹配时返回 null");

            // 8b. 评分版本闸门：Score 口径变了就不能拿旧模型预测
            Check("评分版本闸门", model != null && model.CanPredict("angular-block-loss-v3") && !model.CanPredict("angular-block-loss-v4"),
                model == null ? "未加载" : $"v3 可预测={model.CanPredict("angular-block-loss-v3")}，v4 可预测={model.CanPredict("angular-block-loss-v4")}");
            Check("游戏闸门", model != null && model.CanPredictGame("CS2") && !model.CanPredictGame("Valorant"),
                model == null ? "未加载" : $"模型适用 {model.Game}：CS2 可预测={model.CanPredictGame("CS2")}，Valorant 可预测={model.CanPredictGame("Valorant")}");

            // 9. 真实路径（%LOCALAPPDATA%\AimSense\model）加载不得抛异常；未安装时返回 null
            OfflineModel? installed = OfflineModel.Load();
            Check("真实路径模型加载安全", installed == null || installed.Features.Count > 0,
                installed == null ? $"未安装模型（{OfflineModel.FilePath}），影子预测关闭" : installed.Describe());

            // 10. 本地问卷（profile）：只写本机、最后一条即最新、规模摘要不炸
            LabelStore.Append(LabelStore.ProfileKind, new SurveyLabel(1, Game, 800, "手臂 + 手腕", 40, 240,
                "GPX", 60, "布垫", "掌握", 8, 3, 12, "18–24", null));
            SurveyLabel? survey = LabelAnalysis.LastSurvey();
            Check("问卷写入/读回", survey is { Game: "CS2", MouseModel: "GPX", MouseWeightGrams: 60, DisplayRefreshHz: 240, Grip: "掌握" }
                && survey.AgeBracket == "18–24",
                survey == null ? "未读回" : $"{survey.Game} · {survey.MouseModel} {survey.MouseWeightGrams:0.#}g · 刷新 {survey.DisplayRefreshHz}Hz · 握法 {survey.Grip} · 年龄 {survey.AgeBracket}");
            LabelStore.Append(LabelStore.ProfileKind, new SurveyLabel(1, Game, 800, "主要手腕", 45, 360,
                "Viper", 74, "硬垫", "指握", 10, 6, 20, null, "更新后的问卷"));
            Check("问卷最后一条即最新", LabelAnalysis.LastSurvey() is { MouseModel: "Viper", AgeBracket: null },
                LabelAnalysis.LastSurvey()?.MouseModel ?? "无");
            string scale = LabelAnalysis.LocalScaleSummary(Path.Combine(directory, "无此目录"), Path.Combine(directory, "无此目录"));
            Check("本机采集规模摘要安全", scale.Contains("问卷 2"), scale);

            // 11. 上传包（预览用）：禁传键扫描、白名单剥路径、分区开关、会话键、安装 ID
            UploadPackage.OverrideDirectory = directory;
            var dirty = new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = 1,
                ["nested"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["TracePath"] = "C:\\Users\\someone\\x.jsonl" })
            };
            Check("禁传键扫描器有效（含嵌套）", UploadPackage.FirstForbiddenKey(dirty) != null,
                "嵌套 TracePath → " + UploadPackage.FirstForbiddenKey(dirty));

            // 故意造一条"脏"标签：带本地路径、带自由文本备注
            LabelStore.Append(LabelStore.FeelKind, new FeelLabel("selftest-privacy", @"C:\Users\someone\traces\x.jsonl",
                Game, 800, 33.12, 1.2345, 987.6, 9, "更好", 36.0, CalibrationScoring.Version, @"备注里也有人名 C:\Users\someone"));
            var rawFeel = LabelStore.Read(LabelStore.FeelKind).Last();
            string rawJson = rawFeel.GetRawText();
            var request = new UploadRequest(
                new UploadSession("CS2", 800, 90, 10, ["Click", "Track", "MovingClick"], 2560, 1600, 240,
                    CalibrationScoring.Version, "character-motion-2", "composite-paired-2", 28.8, 45, "2026-10-12"),
                [new UploadCandidate(33.12, new Dictionary<string, double> { ["meanScore"] = 84.6, ["blocks"] = 6 }, 84.6, 6, true)],
                [(LabelStore.FeelKind, rawFeel)],
                LabelAnalysis.LastSurvey());
            var package = UploadPackage.Build(request);
            string json = package.ToJsonString();
            // 注意：ToJsonString 会把非 ASCII 转义成 \\uXXXX，所以这里一律按**解码后的值**判定，
            // 不能拿原始文本 contains("备注") 去断言（那会永远为假，属于测试自身的假阴性）。
            bool sourceHasNote = LabelStore.Str(rawFeel, "Note") is { } rawNote && rawNote.Contains("备注") && rawNote.Contains("Users");
            Check("源标签确实带路径与备注（否则下面的剥离测试无意义）",
                sourceHasNote && LabelStore.Str(rawFeel, "SessionId") == "selftest-privacy" &&
                LabelStore.Str(rawFeel, "TracePath") is { } rawPath && rawPath.Contains("Users"),
                $"源标签：备注含人名={sourceHasNote}，TracePath 含 Users=True，SessionId=selftest-privacy");
            bool noForbidden = UploadPackage.FirstForbiddenKey(package) == null;
            Check("构建的上传包不含任何禁传键", noForbidden, noForbidden ? "递归扫描通过" : UploadPackage.FirstForbiddenKey(package)!);
            bool leakedUser = AnyString(package, x => x.Contains("Users"));
            bool leakedNote = AnyString(package, x => x.Contains("备注"));
            bool leakedSession = AnyString(package, x => x == "selftest-privacy");
            Check("本地路径 / 原始会话 ID / 自由文本已剥离",
                !leakedUser && !leakedNote && !leakedSession && !json.Contains("\"Note\"") && !json.Contains("\"Rank\"") && !json.Contains("\"TracePath\""),
                $"长度 {json.Length} 字符；Users 泄漏={leakedUser}，备注泄漏={leakedNote}，会话 ID 泄漏={leakedSession}");
            Check("会话键不可逆且稳定",
                UploadPackage.SessionKey("selftest-a").Length == 16 &&
                UploadPackage.SessionKey("selftest-a") == UploadPackage.SessionKey("selftest-a") &&
                !UploadPackage.SessionKey("selftest-a").Contains("selftest"),
                "SessionKey('selftest-a') = " + UploadPackage.SessionKey("selftest-a"));
            Check("Redactions 显式声明", package["Redactions"] is System.Text.Json.Nodes.JsonArray redactions && redactions.Count >= 15,
                $"Redactions {((System.Text.Json.Nodes.JsonArray)package["Redactions"]!).Count} 条");

            var labelsOnly = UploadPackage.Build(new UploadRequest(null, [], [(LabelStore.FeelKind, rawFeel)], null));
            string scope = labelsOnly["ConsentScope"]!.ToJsonString();
            Check("分区开关生效",
                !labelsOnly.ContainsKey("Session") && !labelsOnly.ContainsKey("Survey") && labelsOnly.ContainsKey("Labels") &&
                scope.Contains("labels") && !scope.Contains("survey") && !scope.Contains("sessionAggregate"),
                "关闭会话与问卷后 ConsentScope = " + scope);

            string id1 = UploadPackage.InstallId(), id2 = UploadPackage.InstallId();
            string id3 = UploadPackage.ResetInstallId();
            bool fileMatches = File.Exists(UploadPackage.InstallIdPath) && File.ReadAllText(UploadPackage.InstallIdPath).Trim() == id3;
            Check("安装 ID 可持久化且可重置",
                id1.Length == 32 && id1 == id2 && id3 != id1 && fileMatches,
                $"稳定={id1 == id2}，重置后变化={id3 != id1}，落盘一致={fileMatches}");

            // 12. 本机数据清点与删除：必须只删勾选的类别
            string dataRoot = Path.Combine(directory, "localdata");
            Directory.CreateDirectory(Path.Combine(dataRoot, "traces"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "history-v2"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "labels"));
            File.WriteAllText(Path.Combine(dataRoot, "traces", "a.jsonl"), "x");
            File.WriteAllText(Path.Combine(dataRoot, "traces", "b.jsonl"), "x");
            File.WriteAllText(Path.Combine(dataRoot, "history-v2", "h.json"), "x");
            File.WriteAllText(Path.Combine(dataRoot, "labels", "labels-v1.jsonl"), "{\"Kind\":\"feel\"}\n{\"Kind\":\"match\"}\n");
            File.WriteAllText(Path.Combine(dataRoot, "upload-preview-1.json"), "x");
            File.WriteAllText(Path.Combine(dataRoot, UploadPackage.InstallIdFileName), "abc");
            LocalDataSummary before = LocalData.Summarize(dataRoot);
            Check("本机数据清点",
                before.TraceFiles == 2 && before.HistoryFiles == 1 && before.LabelRecords == 2 && before.PreviewFiles == 1,
                LocalData.Describe(before));
            (int deleted, List<string> deleteDetails) = LocalData.Delete(dataRoot, labels: false, traces: true, history: false, previews: true, installId: false);
            LocalDataSummary after = LocalData.Summarize(dataRoot);
            Check("只删勾选的类别",
                deleted == 3 && after.TraceFiles == 0 && after.HistoryFiles == 1 && after.LabelRecords == 2 && after.PreviewFiles == 0 &&
                File.Exists(Path.Combine(dataRoot, UploadPackage.InstallIdFileName)),
                $"删除 {deleted} 个文件；剩余：{LocalData.Describe(after)}；明细：{string.Join("；", deleteDetails)}");

            // 13. 游戏换算系数表：自定义 / 覆盖内置 / 未知拒绝 / 反解 yaw
            GameProfileStore.OverrideDirectory = directory;
            Check("内置系数可用",
                GameProfileStore.Resolve("CS2") is { YawDegreesPerCount: 0.022 } && GameProfileStore.Resolve("Valorant") is { YawDegreesPerCount: 0.07 },
                $"CS2={GameProfileStore.Resolve("CS2")?.YawDegreesPerCount}，Valorant={GameProfileStore.Resolve("Valorant")?.YawDegreesPerCount}");
            Check("未知游戏不猜系数", GameProfileStore.Resolve("某不存在的游戏") == null, "返回 null，由调用方拒绝换算");
            GameProfileStore.Save(new CustomGame("三角洲行动", 0.02, false, .40, .40, .20));
            GameProfile resolvedCustom = GameProfileStore.Resolve("三角洲行动")!;
            Check("自定义游戏可换算且权重可覆盖",
                Math.Abs(resolvedCustom.YawDegreesPerCount - 0.02) < 1e-9 && Math.Abs(resolvedCustom.Weight(TrialMode.Click) - .40) < 1e-9 &&
                Math.Abs(resolvedCustom.Weight(TrialMode.Track) - .20) < 1e-9,
                $"yaw {resolvedCustom.YawDegreesPerCount} · 权重 点击 {resolvedCustom.Weight(TrialMode.Click):0.00} / 跟随 {resolvedCustom.Weight(TrialMode.Track):0.00}");
            GameProfileStore.Save(new CustomGame("CS2", 0.024, false));
            Check("同名覆盖内置系数", GameProfileStore.Resolve("CS2") is { YawDegreesPerCount: 0.024, Source: "用户自定义" },
                $"CS2 yaw 现为 {GameProfileStore.Resolve("CS2")?.YawDegreesPerCount}");
            GameProfileStore.Delete("CS2");
            Check("删除覆盖后恢复内置系数", Math.Abs((GameProfileStore.Resolve("CS2")?.YawDegreesPerCount ?? 0) - 0.022) < 1e-9,
                $"CS2 回到 {GameProfileStore.Resolve("CS2")?.YawDegreesPerCount}");
            double knownSensitivity = 360 * 2.54 / (800 * 30 * 0.022);
            double? solved = GameProfileStore.YawFromKnownPair(800, knownSensitivity, 30);
            Check("由已知灵敏度/cm360 反解 yaw", solved is { } solvedYaw && Math.Abs(solvedYaw - 0.022) < 1e-6,
                $"解出 {solved:0.000000}（期望 0.022000）");

            // 14. 起点模型：连续、单调、垫宽是物理上限、跟随类偏低灵敏度、区间与旧实现一致
            double[] centers = Enumerable.Range(0, 21).Select(i => StartPointModel.CenterCm(i / 20.0, 60, false)).ToArray();
            Check("起点随发力方式连续变化（不再只有 3 个值）",
                centers.Distinct().Count() == 21 && centers[0] > centers[^1],
                $"手腕 0%→100% 得到 {centers.Distinct().Count()} 个不同起点：{centers[0]:0.0} … {centers[^1]:0.0} cm/360");
            Check("垫宽是物理上限而不是乘数",
                Math.Abs(StartPointModel.CenterCm(0, 20, false) - 36) < 1e-9 && Math.Abs(StartPointModel.CenterCm(0, 60, false) - 52) < 1e-9 &&
                Math.Abs(StartPointModel.CenterCm(1, 20, false) - 22) < 1e-9,
                $"20cm 垫纯手臂被限到 {StartPointModel.CenterCm(0, 20, false):0.0}（=20×2×0.9）；60cm 垫仍是 {StartPointModel.CenterCm(0, 60, false):0.0}；纯手腕 {StartPointModel.CenterCm(1, 20, false):0.0} 未受限");
            Check("跟随类游戏整体偏低灵敏度",
                StartPointModel.CenterCm(0.5, 60, true) > StartPointModel.CenterCm(0.5, 60, false),
                $"{StartPointModel.CenterCm(0.5, 60, false):0.0} → {StartPointModel.CenterCm(0.5, 60, true):0.0} cm/360");
            (double rangeLow, double rangeHigh) = StartPointModel.Range(30, 0.25);
            Check("搜索区间与旧实现一致（±25%）", Math.Abs(rangeLow - 24) < 1e-9 && Math.Abs(rangeHigh - 37.5) < 1e-9,
                $"{rangeLow:0.0}–{rangeHigh:0.0} cm/360");

            // 15. 空壳包必须被拒绝（宁可不发），且快照必须携带契约特征
            bool refusedEmpty = false, refusedShell = false;
            try { UploadPackage.Build(new UploadRequest(null, [], [], null)); }
            catch (InvalidOperationException) { refusedEmpty = true; }
            Check("完全没有内容时拒绝生成上传包", refusedEmpty, refusedEmpty ? "已抛 InvalidOperationException" : "竟然生成了空包");
            try
            {
                UploadPackage.Build(new UploadRequest(null,
                    [new UploadCandidate(36, new Dictionary<string, double> { ["cm360"] = 36 }, null, 0, false)], [], null));
            }
            catch (InvalidOperationException) { refusedShell = true; }
            Check("空壳候选（0 有效块 / 特征不全）拒绝生成", refusedShell,
                refusedShell ? "已抛 InvalidOperationException" : "竟然把空壳发出去了");
            SessionSummaryLabel? snapshotForFeatures = LabelAnalysis.LastSession();
            SessionCandidateLabel? withFeatures = snapshotForFeatures?.Candidates.FirstOrDefault(x => x.Features is { Count: > 0 });
            Check("会话快照携带契约特征（重启后仍能生成完整包）",
                withFeatures?.Features is { Count: >= 5 } && withFeatures.Features.ContainsKey("validBlocks") &&
                withFeatures.BlockAggregates is { Count: > 0 },
                withFeatures == null ? "快照里没有特征（重启后会退化成空壳）"
                    : $"特征 {withFeatures.Features!.Count} 项、块级聚合 {withFeatures.BlockAggregates!.Count} 项");

            // 16. 逐帧运动学黄金向量：把 numpy 的三个口径细节锁进测试
            // （percentile 线性插值 / yaw 差分的 mod-360 包裹 / 速度 dt ≥1e-3 截断 / 超额峰度）
            KinSample[] fixture =
            [
                new(0.00, 1, 0, "Scoring", 0, 0),
                new(0.01, -2, 0, "Scoring", 10, 0),
                new(0.02, 3, 0, "Scoring", 350, 0),
                new(0.03, 0, 0, "Scoring", 355, 0),
            ];
            Dictionary<string, double> kinematics = KinematicsAnalysis.FromSamples(fixture);
            bool Kin(string key, double expected) =>
                kinematics.TryGetValue(key, out double value) && Math.Abs(value - expected) <= 1e-6 * Math.Max(1.0, Math.Abs(expected));
            Check("运动学黄金向量（15 项，含 numpy 口径细节）",
                Kin("KinSamples", 4) && Kin("KinScoringShare", 1) && Kin("KinMeanFrameDtMs", 10) && Kin("KinP95FrameDtMs", 10) &&
                Kin("KinMeanAbsDx", 1.5) && Kin("KinMeanAbsDy", 0) && Kin("KinMaxAbsDx", 3) && Kin("KinDirectionReversals", 2) &&
                Kin("KinDurationSeconds", 0.03) && Kin("KinDirectionReversalRate", 2 / 0.03) && Kin("KinPathLength", 6) &&
                Kin("KinAngularPathRad", 35 * Math.PI / 180) && Kin("KinMeanSpeed", 500 / 3.0) && Kin("KinP95Speed", 290) &&
                Kin("KinSpeedKurtosis", -1.5),
                $"反转={kinematics.GetValueOrDefault("KinDirectionReversals")} 角路径={kinematics.GetValueOrDefault("KinAngularPathRad"):0.000000} " +
                $"P95速度={kinematics.GetValueOrDefault("KinP95Speed")} 峰度={kinematics.GetValueOrDefault("KinSpeedKurtosis")} " +
                $"（手算期望：2 / 0.610865 / 290 / -1.5）");

            // 17. 混合包：0 块候选必须被剔除（真实数据里出现过 ValidBlocks=0 却 FeaturesComplete=true 的候选）
            System.Text.Json.Nodes.JsonObject mixed = UploadPackage.Build(new UploadRequest(null,
                [new UploadCandidate(31.1, new Dictionary<string, double> { ["cm360"] = 31.1, ["validBlocks"] = 4 }, 80, 4, true),
                 new UploadCandidate(33.8, new Dictionary<string, double> { ["cm360"] = 33.8, ["validBlocks"] = 0 }, null, 0, true)], [], null));
            int emittedCandidates = mixed["Candidates"]!.AsArray().Count;
            Check("混合包里剔除 0 块候选", emittedCandidates == 1,
                $"发出 {emittedCandidates} 个候选（期望 1：只保留有有效块的那个）");
            // 注：SearchRange 的拆分（声明区间→InitialSearchRange，SearchRange=实测 min/max）无法在这里断言——
            // 本用例的请求没有 Session 段。它由下一次真实导出的包来验证，见 _ui 里导出的 preview JSON。
        }
        finally
        {
            LabelStore.OverrideDirectory = null;
            UploadPackage.OverrideDirectory = null;
            GameProfileStore.OverrideDirectory = null;
            try { Directory.Delete(directory, true); } catch { /* 临时目录清理失败不影响结论 */ }
        }

        text.AppendLine(failures == 0 ? "结论：全部通过" : $"结论：{failures} 项失败");
        if (failures != 0) throw new InvalidOperationException(text.ToString());
        return text.ToString();
    }

    // 递归遍历上传包里的**解码后字符串值**（键名也算），用于验证剥离是否彻底。
    private static bool AnyString(System.Text.Json.Nodes.JsonNode? node, Func<string, bool> match) => node switch
    {
        System.Text.Json.Nodes.JsonObject obj => obj.Any(pair => match(pair.Key) || AnyString(pair.Value, match)),
        System.Text.Json.Nodes.JsonArray array => array.Any(child => AnyString(child, match)),
        System.Text.Json.Nodes.JsonValue value => value.TryGetValue(out string? text) && text != null && match(text),
        _ => false
    };

    private static BlockResult Block(double cm360, TrialMode mode, double score) => new(        new TrialBlock(new BlockRequest($"selftest-{mode}-{cm360:0.00}", mode, TrialPurpose.Search, cm360, 1)),
        new ScoreBreakdown(100 - score, 0, 0, 0), true, [], 6, 0, 0, 0, 0, 0, new BlockDiagnostics(0, 0, 0, null, null, 0, 0));
}

