using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimSense;

internal sealed record ImportedCandidate(string Mode, double Cm360, double Sensitivity, double EDpi, int ValidBlocks, string Status);
internal sealed record TraceImportResult(string Path, string Summary, string Text, IReadOnlyList<ImportedCandidate> Candidates,
    int CompletedBlocks, int VerifiedBlocks, int ValidationBlocks, bool CompositeConfirmed)
{
    public IReadOnlyList<ReviewProgressRow> ReviewRows { get; init; } = [];
    public string ReviewSummary { get; init; } = "综合复核尚未开始。每项3档×2遍=6块，三项共18块。";
    public ResumeSessionState? ResumeState { get; init; }
}

internal static class TraceImportService
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static TraceImportResult Read(string path, CancellationToken cancellation = default, IProgress<string>? progress = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var events = new Dictionary<string, List<JsonElement>>(); var ids = new HashSet<string>();
        var blocks = new List<BlockResult>(); var warnings = new List<string>();
        var carried = new List<CarriedBlock>(); var starts = new Dictionary<TrialMode, JsonElement>();
        var inheritedDifficulty = new Dictionary<TrialMode,SessionDifficulty>();
        var archived = new List<CarriedBlock>();
        JsonElement? session = null, savedReport = null; int lines = 0, verified = 0, interrupted = 0;
        bool legacy = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 65536);
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested(); lines++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 64 * 1024 * 1024) throw new InvalidDataException($"第 {lines} 行超过64MB，请检查记录文件。");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException ex)
            {
                if (reader.EndOfStream) { warnings.Add($"末行（第{lines}行）未完整写入，已保留前面完成的块。"); break; }
                throw new InvalidDataException($"第 {lines} 行不是有效 JSON，不能可靠重算。", ex);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Kind", out var kind)) continue;
                switch (kind.GetString())
                {
                    case "priorValidation" when !legacy:
                        var prior=root.GetProperty("Result").Deserialize<BlockResult>(Options)!;
                        var priorEvents=root.GetProperty("Events").EnumerateArray().Select(e=>e.Clone()).ToArray();
                        TraceReplay.VerifyBlock(prior,priorEvents); archived.Add(new(prior,priorEvents)); break;
                    case "resumeDifficulty" when !legacy:
                        inheritedDifficulty[root.GetProperty("Mode").Deserialize<TrialMode>(Options)] = root.GetProperty("Difficulty").Deserialize<SessionDifficulty>(Options)!; break;
                    case "blockStart" when !legacy:
                        var started = root.GetProperty("Request").Deserialize<BlockRequest>(Options)!;
                        starts[started.Mode] = root.Clone(); break;
                    case "session":
                        if (session.HasValue) throw new InvalidDataException("文件包含多个会话，请分别导入。");
                        session = root.Clone();
                        legacy = root.TryGetProperty("ScoreVersion", out var version) && version.GetString() != CalibrationScoring.Version;
                        break;
                    case "sampleBatch" when !legacy:
                        string id = root.GetProperty("BlockId").GetString() ?? throw new InvalidDataException("计分块缺少编号。");
                        if (events.ContainsKey(id)) throw new InvalidDataException("重复的计分事件批次：" + id);
                        events[id] = root.GetProperty("Events").EnumerateArray().Select(e => e.Clone()).ToList();
                        break;
                    case "block" when !legacy:
                        var result = root.GetProperty("Result").Deserialize<BlockResult>(Options) ?? throw new InvalidDataException("计分块为空。");
                        if (!ids.Add(result.Request.Id)) throw new InvalidDataException("重复的完成块：" + result.Request.Id);
                        if (!events.Remove(result.Request.Id, out var observations)) throw new InvalidDataException("缺少计分事件：" + result.Request.Id);
                        TraceReplay.VerifyBlock(result, observations); blocks.Add(result); carried.Add(new(result, observations)); verified++;
                        break;
                    case "interrupted": interrupted++; break;
                    case "report": savedReport = root.Clone(); break;
                }
            }
            if (lines % 64 == 0) progress?.Report($"读取 {lines} 条记录，已重算 {verified} 个完成块…");
        }
        if (legacy)
        {
            string original = savedReport is { } old && old.TryGetProperty("Report", out var text) ? text.GetString() ?? "" : "没有保存报告。";
            return new(path, "旧评分版本，仅展示原记录，不与新版混算。", original, [], 0, 0, 0, false)
            { ReviewSummary = "旧评分版本：仅展示原报告，不生成新版复核明细。" };
        }
        JsonElement metadata = session ?? savedReport ?? throw new InvalidDataException("缺少会话元数据，无法确定游戏、DPI和搜索起点。");
        var gameValue = metadata.GetProperty("Game");
        var game = gameValue.ValueKind == JsonValueKind.String ? GameProfiles.Get(gameValue.GetString()!) : gameValue.Deserialize<GameProfile>(Options)!;
        double dpi = metadata.GetProperty("Dpi").GetDouble();
        if (game == null || !double.IsFinite(dpi) || dpi <= 0 || !double.IsFinite(game.YawDegreesPerCount) || game.YawDegreesPerCount <= 0)
            throw new InvalidDataException("DPI或游戏转换常数无效。");
        var searches = new List<ModeSearch>();
        foreach (var group in blocks.Where(b => b.Request.Purpose == TrialPurpose.Search).GroupBy(b => b.Request.Mode))
        {
            ModeSearch search;
            if (session.HasValue)
            {
                var source = session.Value; var modes = source.GetProperty("Modes").Deserialize<TrialMode[]>(Options)!;
                int index = Array.IndexOf(modes, group.Key);
                if (index < 0) throw new InvalidDataException("完成块项目不属于本次会话。");
                search = new(group.Key, source.GetProperty("Low").GetDouble(), source.GetProperty("High").GetDouble(), unchecked(source.GetProperty("Seed").GetInt32() + index));
            }
            else
            {
                var saved = savedReport!.Value.GetProperty("Searches").EnumerateArray().Single(s => s.GetProperty("Mode").Deserialize<TrialMode>(Options) == group.Key);
                search = new(group.Key, saved.GetProperty("InitialLowCm360").GetDouble(), saved.GetProperty("InitialHighCm360").GetDouble(), saved.GetProperty("Seed").GetInt32());
            }
            foreach (var block in group) { cancellation.ThrowIfCancellationRequested(); search.NextRequest(); search.Submit(block); }
            searches.Add(search);
        }
        if(session is {} searchSource)
        {
            var modes=searchSource.GetProperty("Modes").Deserialize<TrialMode[]>(Options)!;
            foreach(var mode in modes.Where(m=>!searches.Any(s=>s.Mode==m)))
            {
                var pilots=blocks.Where(b=>b.Request.Mode==mode&&b.Request.Purpose==TrialPurpose.Pilot).ToArray();
                if(pilots.Length>=2 && pilots.Last() is {Valid:true} last && (mode==TrialMode.Track ? last.TrackingShare>=.3 : last.Hits>=4))
                    searches.Add(new(mode,searchSource.GetProperty("Low").GetDouble(),searchSource.GetProperty("High").GetDouble(),unchecked(searchSource.GetProperty("Seed").GetInt32()+Array.IndexOf(modes,mode))));
            }
        }
        CompositeValidation? validation = null;
        string evidenceVersion = metadata.TryGetProperty("EvidenceVersion", out var ev) ? ev.GetString()! : "composite-paired-1";
        var validationBlocks = blocks.Where(b => b.Request.Purpose == TrialPurpose.Validation).ToArray();
        bool reviewWasStarted=starts.Values.Any(s=>s.GetProperty("Request").GetProperty("Purpose").Deserialize<TrialPurpose>(Options)==TrialPurpose.Validation) ||
            savedReport is {} savedReview &&savedReview.TryGetProperty("Validation",out var savedValidation) &&savedValidation.ValueKind==JsonValueKind.Object;
        if (validationBlocks.Length > 0 || reviewWasStarted)
        {
            if (searches.Count != 3 || searches.Any(s => !s.IsComplete || !s.HasEvidence)) throw new InvalidDataException("综合复核缺少完整的三项搜索依据。");
            int seed = session?.GetProperty("Seed").GetInt32() ?? savedReport!.Value.GetProperty("Validation").GetProperty("Seed").GetInt32();
            validation = new(game, searches.ToDictionary(s => s.Mode, s => s.BestCm360), seed, evidenceVersion);
            foreach (var block in validationBlocks) { validation.NextRequest(); validation.Submit(block); }
        }
        var candidates = searches.Where(s => s.HasEvidence).Select(s => new ImportedCandidate(RecommendationReport.Name(s.Mode),
            s.BestCm360, game.Sensitivity(dpi, s.BestCm360), game.Edpi(dpi, s.BestCm360),
            s.Results.Count(b => b.Valid && PairedEvidence.Same(b.Request.Cm360, s.BestCm360)), s.StopReason)).ToList();
        bool confirmed = validation is { HasCompleteEvidence: true, Ambiguous: false };
        if (confirmed) candidates.Add(new("综合复核", validation!.BestCm360!.Value, game.Sensitivity(dpi, validation.BestCm360.Value),
            game.Edpi(dpi, validation.BestCm360.Value), validation.Results.Count(b => b.Valid), "综合复核有完整成对证据"));
        else if (validation != null)
            foreach (double cm in validation.Candidates)
                candidates.Add(new("综合待复核档位", cm, game.Sensitivity(dpi, cm), game.Edpi(dpi, cm),
                    validation.Results.Count(b => b.Valid && PairedEvidence.Same(b.Request.Cm360, cm)),
                    validation.HasCompleteEvidence ? "差距不明确，保留相近候选" : "仅为复核设置，尚未确认统一推荐"));
        var summary = new StringBuilder();
        if(evidenceVersion=="composite-paired-1") summary.AppendLine("此记录使用旧版复核比较规则；原条件续测沿用旧规则以避免混算，新建或换显示条件重做复核使用新版全候选比较。");
        bool priorSearch=metadata.TryGetProperty("SearchEvidenceIsPrior",out var priorFlag) &&priorFlag.GetBoolean();
        if(priorSearch)
        {
            summary.AppendLine("显示条件已改变：历史分项成绩只用来生成候选，当前综合复核独立计分，旧复核不参与比较。");
            for(int i=0;i<candidates.Count;i++) if(candidates[i].Mode is "点击" or "跟随" or "移动点击") candidates[i]=candidates[i] with {Status="原显示条件候选；不代表当前分项最优"};
        }
        summary.AppendLine($"{game.Id} · {dpi:0} DPI · 已从计分事件重算 {verified} 个完成块，其中有效 {blocks.Count(b => b.Valid)} 块。");
        summary.AppendLine($"难度预检 {blocks.Count(b => b.Request.Purpose == TrialPurpose.Pilot)} 块；分项搜索 {blocks.Count(b => b.Request.Purpose == TrialPurpose.Search)} 块；中断块 {interrupted} 个（不计分）。");
        if (savedReport is { } report && report.TryGetProperty("Reason", out var reason)) summary.AppendLine("结束原因：" + reason.GetString());
        foreach (var mode in Enum.GetValues<TrialMode>())
        {
            var search = searches.FirstOrDefault(s => s.Mode == mode);
            summary.AppendLine($"{RecommendationReport.Name(mode)}：{(search == null ? "尚无搜索结果" : $"搜索 {search.Results.Count}/14 块，{(search.IsComplete ? "已结束" : "未完成")}")}；综合复核 {validationBlocks.Count(b => b.Request.Mode == mode)}/6 块。");
        }
        if (validation == null) summary.AppendLine("未进行综合复核，仅提供有证据的分项候选。额外复核仍是原来的三项，每项3档×2遍，共18个计分块。");
        else if (!validation.HasCompleteEvidence)
        {
            summary.AppendLine($"综合复核完成 {validationBlocks.Length}/18 块、有效 {validationBlocks.Count(b => b.Valid)}/18 块，缺少 {18 - validationBlocks.Count(b => b.Valid)} 个有效块，不能确认统一 eDPI。");
            foreach (double cm in validation.Candidates) summary.AppendLine(FormattableString.Invariant($"  待完成复核档位：{cm:0.00} cm/360 · 游戏灵敏度 {game.Sensitivity(dpi, cm):0.0000} · eDPI {game.Edpi(dpi, cm):0.0}（不是已确认综合推荐）。"));
            foreach (var mode in Enum.GetValues<TrialMode>())
            {
                var missing = validation.Candidates.SelectMany(cm => new[] { 6, 7 }.Where(script => !validationBlocks.Any(b => b.Valid && b.Request.Mode == mode && b.Request.ScriptId == script && PairedEvidence.Same(b.Request.Cm360, cm)))
                    .Select(script => FormattableString.Invariant($"{cm:0.00} cm/360·脚本{script}"))).ToArray();
                if (missing.Length > 0) summary.AppendLine($"  {RecommendationReport.Name(mode)}缺少：{string.Join("，", missing)}。");
            }
        }
        else if (validation.Ambiguous) summary.AppendLine("18个复核块已有完整有效证据，但档位差距未达到可重复改善门槛，保留相近区间。");
        foreach (var warning in warnings) summary.AppendLine("提示：" + warning);
        string detail = validation == null ? CompositeReviewPresentation.Plan : CompositeReviewPresentation.Details(validation, dpi);
        ResumeSessionState? resume = null;
        if (session is {} resumeSource && blocks.Count>0 && resumeSource.TryGetProperty("Display",out var recordedDisplay) && resumeSource.TryGetProperty("MotionVersion",out var motionVersion))
        {
            string motion=motionVersion.GetString()!;
            if (motion is "character-motion-1" or "character-motion-2")
            {
                var difficulties=starts.ToDictionary(x=>x.Key,x=>x.Value.GetProperty("Difficulty").Deserialize<SessionDifficulty>(Options)!);
                foreach(var inherited in inheritedDifficulty)
                    if(!difficulties.ContainsKey(inherited.Key)) difficulties[inherited.Key]=inherited.Value;
                foreach(var mode in resumeSource.GetProperty("Modes").Deserialize<TrialMode[]>(Options)!)
                {
                    if(!difficulties.ContainsKey(mode)) difficulties[mode]=new(1,1,1.2);
                    var last=blocks.LastOrDefault(b=>b.Request.Mode==mode && b.Request.Purpose==TrialPurpose.Pilot);
                    if(last!=null && !searches.Any(s=>s.Mode==mode) && starts.TryGetValue(mode,out var start) && start.GetProperty("Request").GetProperty("Id").GetString()==last.Request.Id)
                    {
                        bool passed=last.Valid && (mode==TrialMode.Track ? last.TrackingShare>=.3 : last.Hits>=4);
                        var prior=difficulties[mode];
                        if(!passed && last.Valid) difficulties[mode]=prior with {DistanceScale=Math.Max(.5,prior.DistanceScale*.75),SpeedScale=Math.Max(.25,prior.SpeedScale*.65),Deadline=1.5};
                        else if(blocks.Count(b=>b.Request.Mode==mode&&b.Request.Purpose==TrialPurpose.Pilot)==1 && passed && mode!=TrialMode.Track && last.Hits==6 && last.MeanShotSeconds<.5)
                            difficulties[mode]=prior with {Deadline=.9};
                    }
                }
                resume=new(path,game,dpi,resumeSource.GetProperty("Fov").GetDouble(),resumeSource.TryGetProperty("CharacterDistanceMeters",out var distance)?distance.GetDouble():10,
                    resumeSource.GetProperty("Low").GetDouble(),resumeSource.GetProperty("High").GetDouble(),resumeSource.GetProperty("Seed").GetInt32(),
                    resumeSource.GetProperty("Modes").Deserialize<TrialMode[]>(Options)!,motion,evidenceVersion,
                    new(recordedDisplay.GetProperty("Width").GetInt32(),recordedDisplay.GetProperty("Height").GetInt32(),recordedDisplay.GetProperty("RefreshHz").GetDouble(),recordedDisplay.GetProperty("DeviceName").GetString()!,recordedDisplay.TryGetProperty("DeviceId",out var displayId)?displayId.GetString()!:""),
                    carried,difficulties,searches,validation)
                {
                    SearchEvidenceIsPrior=priorSearch,ArchivedReview=archived,
                    PriorDisplay=resumeSource.TryGetProperty("PriorDisplay",out var priorDisplay) &&priorDisplay.ValueKind==JsonValueKind.Object ? priorDisplay.Deserialize<RecordedDisplay>(Options):null
                };
                if(!double.IsFinite(resume.Fov) || resume.Fov is <60 or >140 || !double.IsFinite(resume.Distance) || resume.Distance is <2 or >60 ||
                    resume.Dpi is <100 or >32000 || resume.Modes.Length is <1 or >3 || resume.Modes.Distinct().Count()!=resume.Modes.Length || resume.Modes.Any(m=>!Enum.IsDefined(m)) ||
                    resume.Difficulties.Values.Any(d=>d.Deadline is not (.9 or 1.2 or 1.5) || !double.IsFinite(d.SpeedScale) || d.SpeedScale is <.25 or >1 || !double.IsFinite(d.DistanceScale) || d.DistanceScale is <.5 or >1))
                    throw new InvalidDataException("续测元数据无效，不能可靠恢复难度。");
            }
        }
        string textReport = summary + "\n" + detail + "\n\n" + RecommendationReport.Build(game, dpi, searches, validation, path);
        return new(path, summary.ToString(), textReport, candidates, blocks.Count, verified, validationBlocks.Length, confirmed)
        {
            ReviewRows = validation == null ? [] : CompositeReviewPresentation.Rows(validation, dpi),
            ReviewSummary = validation == null ? "综合复核尚未开始；以下仅为分项结果。" : CompositeReviewPresentation.Summary(validation)
            , ResumeState = resume
        };
    }
}
