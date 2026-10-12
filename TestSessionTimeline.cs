namespace AimSense;

internal enum SessionPhase { Idle, DisplayConfirm, Instructions, Demo, InputCheck, Preview, Adaptation, Scoring, Transition, Rest, Paused, Finished, ReviewConsent }
internal readonly record struct BlockClock(SessionPhase Phase, double ScoredTime, int Slot, double SlotTime);
internal static class TestSessionTimeline
{
    public const double PreviewSeconds = 2, AdaptationSeconds = 2, ScoringSeconds = 12, BlockSeconds = 16;
    public static BlockClock At(double elapsed, TrialMode mode, double deadline)
    {
        if (elapsed < 2) return new(SessionPhase.Preview, 0, 0, 0);
        if (elapsed < 4) return new(SessionPhase.Adaptation, 0, 0, 0);
        double scored = Math.Clamp(elapsed - 4, 0, 12);
        int slot = Math.Min(5, (int)(scored / 2));
        double time = scored - slot * 2;
        return new(mode != TrialMode.Track && time >= deadline ? SessionPhase.Transition : SessionPhase.Scoring, scored, slot, time);
    }
}
