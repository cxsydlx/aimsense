namespace AimSense;

internal static class CalibrationScoring
{
    public const string Version = "angular-block-loss-v3";
    public static ScoreBreakdown Click(double failureShare, double completionRatio) =>
        new(75 * Math.Clamp(failureShare, 0, 1), 25 * Math.Clamp(completionRatio, 0, 1), 0, 0);
    public static ScoreBreakdown Track(double share, double errorRadii) =>
        new(0, 0, 75 * (1 - Math.Clamp(share, 0, 1)), 25 * Math.Clamp(errorRadii / 3, 0, 1));
}

// Observations contain scored active time only. Angles are radians; time is seconds.
// Explicit target indices prevent pauses or target teleports from becoming movement evidence.
internal sealed class BlockTelemetryAccumulator(TrialMode mode)
{
    private sealed record Sample(int Target, double T, double Yaw, double Pitch, double TYaw, double TPitch, double Radius, bool On);
    private sealed record ShotResult(int Target, double Start, double Time, bool Hit, bool Timeout, double Deadline)
    { public double Duration => Math.Max(0, Time - Start); }
    private readonly List<Sample> _samples = [];
    private readonly List<ShotResult> _shots = [];
    private readonly HashSet<string> _invalid = [];
    private int _targetIndex = -1;
    private double _shotStart;
    private double _shotDeadline = 2;
    private bool _targetClosed;

    public void BeginTarget(double timeSeconds, double deadlineSeconds = 2)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(deadlineSeconds) || timeSeconds < 0 || deadlineSeconds <= 0)
        { Invalidate("目标计时无效"); return; }
        if (_targetIndex >= 0 && !_targetClosed && mode != TrialMode.Track) Invalidate("上一个目标缺少结果");
        _targetIndex++; _shotStart = timeSeconds; _shotDeadline = deadlineSeconds; _targetClosed = false;
    }

    public void Observe(double timeSeconds, double yaw, double pitch, double targetYaw, double targetPitch, double angularRadius, bool onHead)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(yaw) || !double.IsFinite(pitch) || !double.IsFinite(targetYaw) || !double.IsFinite(targetPitch) || !double.IsFinite(angularRadius) || angularRadius <= 0 || timeSeconds < 0)
        { Invalidate("轨迹包含无效数值"); return; }
        if (mode != TrialMode.Track && (_targetIndex < 0 || _targetClosed)) return;
        if (_samples.Count > 0 && timeSeconds < _samples[^1].T) return;
        if (_samples.Count > 0 && timeSeconds == _samples[^1].T && _samples[^1].Target == Math.Max(0, _targetIndex))
        {
            _samples[^1] = new(Math.Max(0, _targetIndex), timeSeconds, yaw, pitch, targetYaw, targetPitch, angularRadius, onHead);
            return;
        }
        _samples.Add(new(Math.Max(0, _targetIndex), timeSeconds, yaw, pitch, targetYaw, targetPitch, angularRadius, onHead));
    }

    public void Shot(double timeSeconds, bool hit, bool timeout)
    {
        if (mode == TrialMode.Track) return;
        if (_targetIndex < 0 || _targetClosed || !double.IsFinite(timeSeconds) || timeSeconds < _shotStart)
        { Invalidate("目标结果重复或计时无效"); return; }
        _shots.Add(new(_targetIndex, _shotStart, timeSeconds, hit && !timeout, timeout, _shotDeadline));
        _targetClosed = true;
    }

    public void Invalidate(string reason) => _invalid.Add(reason);

    public BlockResult Complete(TrialBlock block)
    {
        if (block.Request.Mode != mode) Invalidate("测试模式与轨迹不一致");
        if (_samples.Count == 0 || mode == TrialMode.Track && _samples.Count < 2) Invalidate("轨迹采样不足");
        double total = 0, inside = 0, error = 0;
        var recovery = new List<double>(); var turns = new List<double>();
        int trackingOverruns = 0; double trackingOverrunSum = 0;
        foreach (var group in _samples.GroupBy(s => s.Target))
        {
            var points = group.ToArray();
            double? offStart = null, turnStart = null;
            double priorTargetVx = 0, priorTargetVy = 0;
            bool trackingCrossed = false; double trackingMax = 0;
            for (int i = 1; i < points.Length; i++)
            {
                var a = points[i - 1]; var b = points[i]; double dt = b.T - a.T;
                if (dt > .15) Invalidate("计分轨迹中断超过150毫秒");
                total += dt; inside += dt * ((a.On ? 1 : 0) + (b.On ? 1 : 0)) / 2;
                error += dt * (Error(a) + Error(b)) / 2;
                if (a.On && !b.On) offStart = (a.T + b.T) / 2;
                if (offStart.HasValue && b.On) { recovery.Add(Math.Max(0, (a.T + b.T) / 2 - offStart.Value)); offStart = null; }
                double tx = Wrap(b.TYaw - a.TYaw) * Math.Cos(b.TPitch) / dt, ty = (b.TPitch - a.TPitch) / dt;
                double cx = Wrap(b.Yaw - a.Yaw) * Math.Cos(b.TPitch) / dt, cy = (b.Pitch - a.Pitch) / dt;
                double speed = double.Hypot(tx, ty), previousSpeed = double.Hypot(priorTargetVx, priorTargetVy);
                bool changedDirection = speed > .02 && previousSpeed > .02 &&
                    tx * priorTargetVx + ty * priorTargetVy < -.5 * speed * previousSpeed;
                if (mode == TrialMode.Track && changedDirection) { turnStart = b.T; trackingCrossed = false; }
                if (turnStart.HasValue && speed > .02 && cx * tx + cy * ty > .1 * speed * speed)
                { turns.Add(b.T - turnStart.Value); turnStart = null; }
                if (mode == TrialMode.Track && speed > .02 && !changedDirection)
                {
                    double before = (Wrap(a.Yaw - a.TYaw) * Math.Cos(a.TPitch) * tx + (a.Pitch - a.TPitch) * ty) / speed;
                    double after = (Wrap(b.Yaw - b.TYaw) * Math.Cos(b.TPitch) * tx + (b.Pitch - b.TPitch) * ty) / speed;
                    // A target moving past a stationary cursor cannot cause cursor overshoot.
                    if (!trackingCrossed && before <= a.Radius && after > b.Radius && cx * tx + cy * ty > speed * speed)
                    { trackingCrossed = true; trackingMax = 0; trackingOverruns++; }
                    if (trackingCrossed && cx * tx + cy * ty > 0)
                    { double value = Math.Max(0, after / b.Radius - 1); trackingOverrunSum += Math.Max(0, value - trackingMax); trackingMax = Math.Max(trackingMax, value); }
                    if (trackingCrossed && b.On) trackingCrossed = false;
                }
                if (speed > .02) { priorTargetVx = tx; priorTargetVy = ty; }
            }
        }
        if (mode == TrialMode.Track)
        {
            if ((_samples.Count > 0 && (_samples[0].T > .15 || _samples[^1].T < block.DurationSeconds - .15)) || total < block.DurationSeconds - .15)
                Invalidate("正式计分时间不足");
        }
        else
        {
            if (_shots.Count != block.ExpectedShots) Invalidate("点击目标未全部完成");
            foreach (var shot in _shots)
            {
                var points = _samples.Where(s => s.Target == shot.Target).ToArray();
                if (points.Length == 0 || points[0].T - shot.Start > .15 || shot.Time - points[^1].T > .15)
                    Invalidate("目标活跃期间采样不足");
            }
        }
        double share = inside / Math.Max(total, .001), meanError = error / Math.Max(total, .001);
        int hits = _shots.Count(s => s.Hit), misses = _shots.Count(s => !s.Hit && !s.Timeout), timeouts = _shots.Count(s => s.Timeout);
        double ratio = _shots.Count == 0 ? 1 : _shots.Average(s => s.Hit ? Math.Clamp(s.Duration / s.Deadline, 0, 1) : 1);
        var breakdown = mode == TrialMode.Track ? CalibrationScoring.Track(share, meanError)
            : CalibrationScoring.Click(1 - hits / (double)Math.Max(1, _shots.Count), ratio);
        var (overruns, corrections, meanOverrun) = ClickDiagnostics();
        if (mode == TrialMode.Track) { overruns = trackingOverruns; meanOverrun = trackingOverrunSum / Math.Max(1, trackingOverruns); }
        return new(block, breakdown, _invalid.Count == 0, _invalid.ToArray(), hits, misses, timeouts,
            share, meanError, _shots.Where(s => s.Hit).Select(s => s.Duration).DefaultIfEmpty().Average(),
            new(overruns, corrections, meanOverrun, turns.Count == 0 ? null : turns.Average(),
                recovery.Count == 0 ? null : recovery.Average(), turns.Count, recovery.Count));
    }

    private (int, int, double) ClickDiagnostics()
    {
        if (mode == TrialMode.Track) return (0, 0, 0);
        int overruns = 0, corrections = 0; double maxSum = 0;
        foreach (var shot in _shots)
        {
            var points = _samples.Where(p => p.Target == shot.Target).ToArray();
            if (points.Length < 2) continue;
            var origin = points[0]; double max = 0; bool crossed = false, returning = false;
            for (int i = 1; i < points.Length; i++)
            {
                var p = points[i]; var previous = points[i - 1];
                double cos = Math.Cos(p.TPitch), tx = Wrap(p.TYaw - origin.Yaw) * cos, ty = p.TPitch - origin.Pitch;
                double distance = double.Hypot(tx, ty); if (distance <= 2 * p.Radius) continue;
                double dx = Wrap(p.Yaw - origin.Yaw) * cos, dy = p.Pitch - origin.Pitch;
                double px = Wrap(previous.Yaw - origin.Yaw) * cos, py = previous.Pitch - origin.Pitch;
                double along = (dx * tx + dy * ty) / distance, previousAlong = (px * tx + py * ty) / distance;
                double advance = along - previousAlong, edge = distance + p.Radius;
                if (!crossed && advance > 0 && previousAlong <= edge && along > edge)
                {
                    double fraction = (edge - previousAlong) / advance;
                    double crossX = px + (dx - px) * fraction, crossY = py + (dy - py) * fraction;
                    if (Math.Abs(crossX * ty - crossY * tx) / distance <= p.Radius) crossed = true;
                }
                if (crossed && advance > 0) max = Math.Max(max, Math.Max(0, (along - edge) / p.Radius));
                if (crossed && advance < -p.Radius * .05 && !returning) { corrections++; returning = true; }
            }
            if (max > 0) { overruns++; maxSum += max; }
        }
        return (overruns, corrections, overruns == 0 ? 0 : maxSum / overruns);
    }

    private static double Error(Sample s)
    {
        double dot = Math.Sin(s.Pitch) * Math.Sin(s.TPitch) + Math.Cos(s.Pitch) * Math.Cos(s.TPitch) * Math.Cos(Wrap(s.Yaw - s.TYaw));
        return Math.Acos(Math.Clamp(dot, -1, 1)) / s.Radius;
    }
    private static double Wrap(double x) => Math.IEEERemainder(x, 2 * Math.PI);
}
