namespace AimSense;

// Owns scored timing and target outcomes independently of WPF events and display configuration.
internal sealed class TrialBlockRunner(BlockRequest request, double deadline, FpsCamera camera,
    Func<double, int, ActorPose> poseAt, List<object> events)
{
    private readonly BlockTelemetryAccumulator _telemetry = new(request.Mode);
    private bool _ended;
    public int Slot { get; private set; } = -1;
    public bool[] Resolved { get; } = new bool[6];
    public BlockClock Advance(double elapsed)
    {
        var clock = TestSessionTimeline.At(elapsed, request.Mode, deadline);
        if (_ended) return clock;
        if (clock.Phase is SessionPhase.Preview or SessionPhase.Adaptation) return clock;
        if (request.Mode == TrialMode.Track)
        {
            if (Slot == -1) { Slot = 0; Observe(0, poseAt(0, 0)); }
            Observe(clock.ScoredTime, poseAt(clock.ScoredTime, 0));
        }
        else
        {
            while (Slot < clock.Slot)
            {
                if (Slot >= 0 && !Resolved[Slot]) FinishTimeout();
                Slot++;
                double start = Slot * 2;
                _telemetry.BeginTarget(start, deadline);
                events.Add(new { Kind = "begin", Time = start, Deadline = deadline });
                Observe(start, poseAt(start, Slot));
            }
            if (!Resolved[Slot])
            {
                double time = Math.Min(clock.ScoredTime, Slot * 2 + deadline);
                Observe(time, poseAt(time, Slot));
                if (clock.SlotTime >= deadline) FinishTimeout();
            }
            if (Resolved[Slot]) clock = clock with { Phase = SessionPhase.Transition };
        }
        if (elapsed >= TestSessionTimeline.BlockSeconds) _ended = true;
        return clock;
    }
    private void FinishTimeout()
    {
        double time = Slot * 2 + deadline;
        Observe(time, poseAt(time, Slot)); Shot(time, false, true);
    }
    public void Click(double elapsed)
    {
        var clock = Advance(elapsed);
        if (_ended || request.Mode == TrialMode.Track || clock.Phase != SessionPhase.Scoring || Resolved[clock.Slot]) return;
        var pose = poseAt(clock.ScoredTime, clock.Slot);
        Observe(clock.ScoredTime, pose); Shot(clock.ScoredTime, camera.ContainsHead(pose), false);
    }
    private void Observe(double time, ActorPose pose)
    {
        var target = camera.TargetAngles(pose);
        double radius = Math.Atan(ActorPose.HeadRadii.X / Math.Max(.01, (pose.HeadCenter - camera.Position).Length()));
        double yaw = camera.Yaw * Math.PI / 180, pitch = camera.Pitch * Math.PI / 180;
        double tyaw = target.Yaw * Math.PI / 180, tpitch = target.Pitch * Math.PI / 180;
        bool on = camera.ContainsHead(pose);
        _telemetry.Observe(time, yaw, pitch, tyaw, tpitch, radius, on);
        events.Add(new { Kind = "observe", Time = time, Yaw = yaw, Pitch = pitch, TargetYaw = tyaw, TargetPitch = tpitch, Radius = radius, OnHead = on });
    }
    private void Shot(double time, bool hit, bool timeout)
    {
        _telemetry.Shot(time, hit, timeout); Resolved[Slot] = true;
        events.Add(new { Kind = "shot", Time = time, Hit = hit, Timeout = timeout });
    }
    private readonly HashSet<string> _invalidReasons = [];
    public void Invalidate(string reason) { if (!_invalidReasons.Add(reason)) return; _telemetry.Invalidate(reason); events.Add(new { Kind = "invalidate", Reason = reason }); }
    public BlockResult Complete() => _telemetry.Complete(new(request));
}

internal sealed class TestSessionStateMachine
{
    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
    public void Change(SessionPhase next)
    {
        if (Phase == SessionPhase.Paused && next is not (SessionPhase.Preview or SessionPhase.Finished))
            throw new InvalidOperationException("恢复被中断块须重新进入预告阶段。");
        Phase = next;
    }
}
