namespace AimSense;

internal static class PairedEvidence
{
    public static ComparisonEvidence Compare(string stage, double baseline, double candidate, int[] scripts, IEnumerable<BlockResult> results)
    {
        var data = results.Where(r => r.Valid && double.IsFinite(r.Score)).ToArray();
        var changes = new List<double>();
        foreach (int script in scripts)
        {
            var a = data.LastOrDefault(r => r.Request.ScriptId == script && Same(r.Request.Cm360, baseline));
            var b = data.LastOrDefault(r => r.Request.ScriptId == script && Same(r.Request.Cm360, candidate));
            if (a != null && b != null) changes.Add(b.Score - a.Score);
        }
        bool complete = changes.Count == scripts.Length;
        bool accepted = complete && changes.All(x => x > 0) && changes.Average() >= 3;
        string reason = !complete ? "缺少成对有效采样，不能比较" : accepted
            ? "两个动作脚本都改善，平均表现分至少提高3分"
            : changes.Any(x => x <= 0) && changes.Any(x => x > 0) ? "两个脚本结果方向不一致，保留区间"
            : "未达到两个脚本均改善且平均提高3分，保留区间";
        return new(stage, baseline, candidate, scripts, changes.ToArray(), complete, accepted, reason);
    }
    public static bool Same(double a, double b) => Math.Abs(a - b) < .000001;
}

internal sealed class ModeSearch
{
    private readonly Queue<BlockRequest> _queue = new();
    private readonly List<BlockResult> _results = [];
    private readonly List<ComparisonEvidence> _comparisons = [];
    private readonly Random _random;
    private readonly double[] _initial;
    private BlockRequest? _pending;
    private int _stage;
    private int _serial;
    private int _direction = 1;
    private double _step = .08;
    private double _pairBaseline, _pairCandidate;
    private readonly HashSet<int> _unimprovedSides = [];
    public TrialMode Mode { get; }
    public int Seed { get; }
    public double InitialLowCm360 => _initial[0];
    public double InitialHighCm360 => _initial[2];
    public bool IsComplete { get; private set; }
    public IReadOnlyList<BlockResult> Results => _results;
    public IReadOnlyList<ComparisonEvidence> Comparisons => _comparisons;
    public double BestCm360 { get; private set; }
    public string StopReason { get; private set; } = "测试进行中";
    public bool HasEvidence => _results.Count(r => r.Valid && PairedEvidence.Same(r.Request.Cm360, BestCm360)) >= 2;

    public ModeSearch(TrialMode mode, double low, double high, int seed)
    {
        if (!double.IsFinite(low) || !double.IsFinite(high) || low <= 0 || high < low) throw new ArgumentOutOfRangeException(nameof(low));
        Mode = mode; Seed = seed; _random = new(seed);
        low = Math.Clamp(low, 5, 150); high = Math.Clamp(high, 5, 150);
        if (high / low < 1.03) { low = Math.Max(5, low / 1.08); high = Math.Min(150, high * 1.08); }
        _initial = [low, Math.Sqrt(low * high), high]; BestCm360 = _initial[1];
        Enqueue(_initial, [0, 1]);
    }

    public BlockRequest? NextRequest()
    {
        if (_pending != null) return _pending;
        return _pending = _queue.Count > 0 ? _queue.Dequeue() : null;
    }

    public void Submit(BlockResult result)
    {
        if (_pending == null || result.Request != _pending) throw new InvalidOperationException("结果与当前测试块不匹配");
        _results.Add(result); _pending = null;
        if (_queue.Count > 0) return;
        if (_stage == 0)
        {
            var initial = _initial.Where(x => !PairedEvidence.Same(x, _initial[1])).Select(x =>
                PairedEvidence.Compare("初始比较", _initial[1], x, [0, 1], _results)).ToArray();
            _comparisons.AddRange(initial);
            var accepted = initial.Where(x => x.Accepted).OrderByDescending(x => x.MeanImprovement).FirstOrDefault();
            if (accepted != null) BestCm360 = accepted.CandidateCm360;
            if (!HasEvidence)
            {
                // Valid data from other candidates stays visible, but cannot replace an unmeasured baseline.
                StopReason = "起始档缺少两个有效块，无法确认调整方向；保留已有有效候选记录";
                IsComplete = true; return;
            }
            _direction = accepted != null ? Math.Sign(accepted.CandidateCm360 - accepted.BaselineCm360)
                : _results.Where(r => r.Valid && PairedEvidence.Same(r.Request.Cm360, BestCm360)).Sum(r => r.Diagnostics.Overshoots) >= 2 ? 1
                : initial.Where(x => x.Complete).OrderByDescending(x => x.MeanImprovement).Select(x => Math.Sign(x.CandidateCm360 - x.BaselineCm360)).DefaultIfEmpty(1).First();
        }
        else
        {
            int firstScript = _stage * 2;
            var comparison = PairedEvidence.Compare($"相邻比较{_stage}", _pairBaseline, _pairCandidate,
                [firstScript, firstScript + 1], _results);
            _comparisons.Add(comparison);
            if (comparison.Accepted)
            {
                BestCm360 = _pairCandidate; _step = Math.Max(.03, _step / 2); _unimprovedSides.Clear();
            }
            else
            {
                if (comparison.Complete) _unimprovedSides.Add(_direction);
                _direction = -_direction;
            }
            if (_unimprovedSides.Count == 2)
            { IsComplete = true; StopReason = "两侧邻近档均无可重复改善，保留当前候选及相近区间"; return; }
            if (_stage >= 2)
            { IsComplete = true; StopReason = "已达到14块搜索预算；这是预算内较好候选，尚未证明收敛"; return; }
        }
        _stage++;
        _pairBaseline = BestCm360;
        _pairCandidate = Math.Clamp(BestCm360 * (1 + _direction * _step), 5, 150);
        if (PairedEvidence.Same(_pairCandidate, BestCm360))
        { _direction = -_direction; _pairCandidate = Math.Clamp(BestCm360 * (1 + _direction * _step), 5, 150); }
        Enqueue([_pairBaseline, _pairCandidate], [_stage * 2, _stage * 2 + 1]);
    }

    private void Enqueue(double[] candidates, int[] scripts)
    {
        var order = candidates.OrderBy(_ => _random.Next()).ToArray();
        foreach (int script in scripts)
        {
            foreach (double cm in order) _queue.Enqueue(new($"{Mode}-search-{++_serial}", Mode, TrialPurpose.Search, cm, script));
            Array.Reverse(order);
        }
    }
}

internal sealed record CompositeConflict(TrialMode Mode, double WinnerCm360, double AlternativeCm360, double[] Deficits);

internal sealed class CompositeValidation
{
    private readonly Queue<BlockRequest> _queue = new();
    private readonly List<BlockResult> _results = [];
    private BlockRequest? _pending;
    private readonly GameProfile _game;
    public const string CurrentEvidenceVersion = "composite-paired-2";
    public string EvidenceVersion { get; }
    public int Seed { get; }
    public GameProfile Game => _game;
    public IReadOnlyDictionary<TrialMode, double> Bests { get; }
    public IReadOnlyList<double> Candidates { get; }
    public IReadOnlyList<BlockResult> Results => _results;
    public bool IsComplete => _queue.Count == 0 && _pending == null;
    public bool HasCompleteEvidence => IsComplete && Candidates.All(c => Enum.GetValues<TrialMode>().All(m =>
        new[] { 6, 7 }.All(s => _results.Any(r => r.Valid && r.Request.Mode == m && r.Request.ScriptId == s && PairedEvidence.Same(r.Request.Cm360, c)))));
    public double? BestCm360 => HasCompleteEvidence ? Candidates.OrderByDescending(WeightedScore).ThenBy(c => Math.Abs(Math.Log(c / Candidates[0]))).First() : null;
    public bool Ambiguous
    {
        get
        {
            if (!HasCompleteEvidence) return true;
            var ranked = Candidates.OrderByDescending(WeightedScore).ToArray();
            var opponents = EvidenceVersion == "composite-paired-1" ? ranked.Skip(1).Take(1) : ranked.Skip(1);
            return opponents.Any(c => WeightedScore(ranked[0]) - WeightedScore(c) < 3 ||
                new[] { 6, 7 }.Any(s => WeightedScore(ranked[0], s) <= WeightedScore(c, s)));
        }
    }
    public IReadOnlyList<CompositeConflict> Conflicts
    {
        get
        {
            if (!BestCm360.HasValue) return [];
            double winner = BestCm360.Value;
            return Enum.GetValues<TrialMode>().SelectMany(mode => Candidates.Where(c => !PairedEvidence.Same(c, winner)).Select(c =>
                new CompositeConflict(mode, winner, c, new[] { 6, 7 }.Select(s => Score(c, mode, s) - Score(winner, mode, s)).ToArray())))
                .Where(x => x.Deficits.All(d => d > 8)).ToArray();
        }
    }

    public CompositeValidation(GameProfile game, IReadOnlyDictionary<TrialMode, double> bests, int seed, string evidenceVersion = CurrentEvidenceVersion)
    {
        if (evidenceVersion is not ("composite-paired-1" or CurrentEvidenceVersion)) throw new ArgumentException("不支持此复核比较版本。");
        EvidenceVersion = evidenceVersion;
        if (Enum.GetValues<TrialMode>().Any(m => !bests.TryGetValue(m, out var cm) || !double.IsFinite(cm) || cm < 5 || cm > 150))
            throw new ArgumentException("综合复核需要三个有效分项候选。", nameof(bests));
        _game = game; Seed = seed; Bests = new Dictionary<TrialMode, double>(bests);
        double middle = Math.Exp(bests.Sum(x => game.Weight(x.Key) * Math.Log(x.Value)));
        var candidates = new List<double>();
        void Add(double cm) { cm = Math.Clamp(cm, 5, 150); if (candidates.Count < 3 && candidates.All(c => Math.Abs(Math.Log(cm / c)) >= Math.Log(1.03))) candidates.Add(cm); }
        Add(middle); Add(bests.Values.Min()); Add(bests.Values.Max()); Add(middle * .95); Add(middle * 1.05);
        for (int step = 2; candidates.Count < 3 && step < 20; step++) { Add(middle * Math.Pow(.95, step)); Add(middle * Math.Pow(1.05, step)); }
        Candidates = candidates;
        var random = new Random(seed); int serial = 0;
        foreach (int script in new[] { 6, 7 })
        foreach (var mode in Enum.GetValues<TrialMode>())
        foreach (double cm in candidates.OrderBy(_ => random.Next()))
            _queue.Enqueue(new($"validation-{++serial}", mode, TrialPurpose.Validation, cm, script));
    }

    public BlockRequest? NextRequest() => _pending ??= _queue.Count > 0 ? _queue.Dequeue() : null;
    public void Submit(BlockResult result)
    {
        if (_pending == null || result.Request != _pending) throw new InvalidOperationException("结果与当前复核块不匹配");
        _results.Add(result); _pending = null;
    }
    public double Score(double cm, TrialMode mode, int script) => _results.Where(r => r.Valid && r.Request.Mode == mode &&
        r.Request.ScriptId == script && PairedEvidence.Same(r.Request.Cm360, cm)).Select(r => r.Score).DefaultIfEmpty(double.NaN).Average();
    public double WeightedScore(double cm) => new[] { 6, 7 }.Average(s => WeightedScore(cm, s));
    public double WeightedScore(double cm, int script) => Enum.GetValues<TrialMode>().Sum(m => _game.Weight(m) * Score(cm, m, script));
}
