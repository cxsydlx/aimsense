namespace AimSense;

internal readonly record struct AimSample(double TimeMs, double X, double Y, double TargetX,
    double TargetY, double Radius, int RawDX = 0, int RawDY = 0, bool Raw = false,
    double RadiusY = 0, bool? OnTarget = null);

internal sealed record AimMetrics(double Loss, double DurationMs, bool Hit, bool Timeout,
    double OvershootRadii, int Corrections, double TrackingShare, double MeanError,
    bool DataValid, double ReactionMs);

internal static class TrajectoryAnalysis
{
    public static AimMetrics Analyze(IReadOnlyList<AimSample> points, double deadline, bool tracking,
        bool movingTarget, bool hit, bool timeout)
    {
        if (points.Count < 2) return new(150, 0, hit, timeout, 0, 0, 0, 0, false, 0);
        AimSample start = points[0];
        double duration = points[^1].TimeMs, insideTime = 0, totalTime = 0, errorTime = 0;
        double overshoot = 0, reaction = duration;
        bool moved = false, crossed = false, valid = true;
        int rawCount = 0;
        // Aggregate velocity over 20 ms before counting corrective submovements.
        var speedBins = new List<(double Time, double Speed, double Distance)>();
        double binDistance = 0, binStart = start.TimeMs;
        for (int i = 1; i < points.Count; i++)
        {
            AimSample a = points[i - 1], b = points[i];
            double dt = b.TimeMs - a.TimeMs;
            if (b.Raw) rawCount++;
            if (dt <= 0) { valid = false; continue; }
            double cursorDistance = double.Hypot(b.X - a.X, b.Y - a.Y);
            double targetDistance = double.Hypot(b.TargetX - a.TargetX, b.TargetY - a.TargetY);
            if (dt > 100 && cursorDistance + targetDistance > b.Radius) valid = false;
            if (!moved && double.Hypot(b.X - start.X, b.Y - start.Y) >= Math.Max(1, start.Radius * .2))
            { moved = true; reaction = b.TimeMs; }
            double radius = Math.Max(1, (a.Radius + b.Radius) / 2);
            double e0x = a.X - a.TargetX, e0y = a.Y - a.TargetY;
            double e1x = b.X - b.TargetX, e1y = b.Y - b.TargetY;
            e0y *= a.RadiusY > 0 ? a.Radius / a.RadiusY : 1;
            e1y *= b.RadiusY > 0 ? b.Radius / b.RadiusY : 1;
            insideTime += dt * (a.OnTarget.HasValue && b.OnTarget.HasValue
                ? ((a.OnTarget.Value ? 1 : 0) + (b.OnTarget.Value ? 1 : 0)) / 2.0
                : InsideFraction(e0x, e0y, e1x, e1y, radius));
            errorTime += dt * (Math.Min(4, double.Hypot(e0x, e0y) / radius) +
                Math.Min(4, double.Hypot(e1x, e1y) / radius)) / 2;
            totalTime += dt;
            if (!tracking)
            {
                // Use the known target at this instant, rather than treating the click endpoint as the target.
                double vx = b.TargetX - start.X, vy = b.TargetY - start.Y;
                double distance = double.Hypot(vx, vy);
                if (distance > 2 * radius)
                {
                    double along0 = ((a.X - start.X) * vx + (a.Y - start.Y) * vy) / distance;
                    double along1 = ((b.X - start.X) * vx + (b.Y - start.Y) * vy) / distance;
                    double side0 = ((a.X - start.X) * vy - (a.Y - start.Y) * vx) / distance;
                    double side1 = ((b.X - start.X) * vy - (b.Y - start.Y) * vx) / distance;
                    double edge = distance + b.Radius;
                    if (along1 > edge && along0 <= edge && along1 > along0)
                    {
                        double u = (edge - along0) / (along1 - along0);
                        if (Math.Abs(side0 + u * (side1 - side0)) <= b.Radius) crossed = true;
                    }
                    if (crossed && along1 > edge && along1 > along0)
                        overshoot = Math.Max(overshoot, Math.Min(3, (along1 - edge) / b.Radius));
                }
            }
            binDistance += cursorDistance;
            double binTime = b.TimeMs - binStart;
            if (binTime >= 20)
            {
                speedBins.Add((b.TimeMs, binDistance / binTime, binDistance));
                binDistance = 0; binStart = b.TimeMs;
            }
        }
        int corrections = 0;
        if (speedBins.Count >= 3)
        {
            double peak = speedBins.Max(s => s.Speed);
            // First substantial peak, followed by a slow interval and a purposeful re-acceleration.
            int firstPeak = speedBins.FindIndex(s => s.Speed >= peak * .7);
            bool slow = false;
            for (int i = Math.Max(0, firstPeak + 1); i < speedBins.Count; i++)
            {
                var bin = speedBins[i];
                if (bin.Speed <= peak * .15) slow = true;
                else if (slow && bin.Speed >= peak * .35 && bin.Distance >= start.Radius * .25)
                { corrections++; slow = false; }
            }
        }
        double share = insideTime / Math.Max(1, totalTime), error = errorTime / Math.Max(1, totalTime);
        double loss = tracking ? 100 * (1 - share) + 10 * error
            : (hit ? 0 : 100) + 20 * Math.Clamp(duration / deadline, 0, 1)
              + 10 * overshoot + (movingTarget ? 3 : 5) * Math.Min(3, corrections);
        return new(loss, duration, hit, timeout, overshoot, corrections, share, error, valid && rawCount >= 2, reaction);
    }

    // Exact fraction inside a circle for a linearly interpolated cursor-to-target error segment.
    internal static double InsideFraction(double x0, double y0, double x1, double y1, double radius)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double a = dx * dx + dy * dy, c = x0 * x0 + y0 * y0 - radius * radius;
        if (a < 1e-12) return c <= 0 ? 1 : 0;
        double b = 2 * (x0 * dx + y0 * dy), discriminant = b * b - 4 * a * c;
        if (discriminant <= 0) return 0;
        double root = Math.Sqrt(discriminant);
        double entry = Math.Max(0, (-b - root) / (2 * a));
        double exit = Math.Min(1, (-b + root) / (2 * a));
        return Math.Max(0, exit - entry);
    }
}
