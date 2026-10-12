using System.Diagnostics;

namespace AimSense;

internal sealed record ArenaPlayback(MotionScript Script, TrialMode Mode, double OriginYaw,
    long Started, double Deadline, bool Demo = false)
{
    private static readonly double[] Offsets = [-12, 10, -7, 14, -15, 6];
    public ArenaPose Sample(long now, int resolvedMask)
    {
        double elapsed = Math.Max(0, (now - Started) / (double)Stopwatch.Frequency);
        double time = Demo ? Math.Min(12, elapsed) : Math.Clamp(elapsed - 4, 0, 12);
        int slot = Mode == TrialMode.Track ? 0 : Math.Min(5, (int)(time / 2));
        double slotTime = time - slot * 2;
        double offset = Mode == TrialMode.Track ? 0 : Demo ? Offsets[slot] : TargetLayout.Offset(Script.Id, slot, Script.Version);
        var pose = Script.Sample(Mode == TrialMode.Click ? 0 : time).RotateYaw(OriginYaw + offset);
        bool hidden = Mode != TrialMode.Track && (Demo ? slotTime >= 1.1 : elapsed >= 4 && (slotTime >= Deadline || (resolvedMask & (1 << slot)) != 0));
        return new(pose, hidden, !Demo && elapsed < 2, slotTime);
    }
}
internal readonly record struct ArenaPose(ActorPose Pose, bool Hidden, bool Preview, double SlotTime);

internal static class TargetLayout
{
    private static readonly double[][] Patterns =
    [
        [-12,10,-7,14,-15,6], [12,-10,7,-14,15,-6],
        [6,-15,14,-7,10,-12], [-6,15,-14,7,-10,12],
        [-7,14,-12,6,-15,10], [7,-14,12,-6,15,-10],
        [10,-7,6,-15,14,-12], [-14,12,-10,15,-6,7]
    ];
    public static double Offset(int script, int slot, string version) => version == "character-motion-1"
        ? Patterns[0][slot] * (script%2==0?1:-1) : Patterns[script][slot];
}
