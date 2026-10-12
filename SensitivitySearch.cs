namespace AimSense;

internal sealed record CandidateEvidence(double Cm360, double[] Losses, int OvershootTargets,
    int ValidTargets, int Hits, double TrackingShare)
{
    public double Mean => Losses.Length == 0 ? double.PositiveInfinity : Losses.Average();
    public double StandardError => Losses.Length < 2 ? double.PositiveInfinity
        : Math.Sqrt(Losses.Sum(x => Math.Pow(x - Mean, 2)) / (Losses.Length - 1) / Losses.Length);
}

internal sealed record SearchDecision(double BestCm360, double NextLow, double NextHigh,
    bool Ambiguous, bool DataValid, bool RepeatedOvershoot, double ScoreGap, bool PerformanceValid = true);

internal static class SensitivitySearch
{
    public static double[] Candidates(double low, double high) => [low, Math.Sqrt(low * high), high];

    public static SearchDecision Evaluate(IReadOnlyList<CandidateEvidence> trials, bool tracking)
    {
        var ordered = trials.OrderBy(t => t.Cm360).ToArray();
        double low = ordered[0].Cm360, high = ordered[^1].Cm360, center = Math.Sqrt(low * high);
        bool rawValid = ordered.All(t => t.ValidTargets >= 6 && t.Losses.Length >= 8);
        bool performanceValid = ordered.All(t => tracking ? t.TrackingShare >= .02 : t.Hits >= 3);
        bool valid = rawValid && performanceValid;
        var ranked = ordered.OrderBy(t => t.Mean).ThenBy(t => Math.Abs(Math.Log(t.Cm360 / center))).ToArray();
        double gap = ranked[1].Mean - ranked[0].Mean;
        double tolerance = Math.Max(2, Math.Sqrt(Math.Pow(ranked[0].StandardError, 2) + Math.Pow(ranked[1].StandardError, 2)));
        bool ambiguous = !valid || gap <= tolerance;
        CandidateEvidence best = ambiguous ? ordered.OrderBy(t => Math.Abs(Math.Log(t.Cm360 / center))).First() : ranked[0];
        // Repeated overrun supports testing slower candidates; it does not override clear performance evidence.
        if (valid && ambiguous && !tracking && best.OvershootTargets >= 2)
        {
            var slower = ordered.Where(t => t.Cm360 > best.Cm360 && t.OvershootTargets < best.OvershootTargets &&
                t.Mean <= best.Mean + tolerance).OrderBy(t => t.Mean).FirstOrDefault();
            if (slower is not null) best = slower;
        }
        double nextLow = low, nextHigh = high;
        if (valid && !ambiguous)
        {
            double halfStep = Math.Log(high / low) / 4;
            nextLow = Math.Clamp(best.Cm360 * Math.Exp(-halfStep), 5, 150);
            nextHigh = Math.Clamp(best.Cm360 * Math.Exp(halfStep), 5, 150);
        }
        return new(best.Cm360, nextLow, nextHigh, ambiguous, rawValid,
            !tracking && best.OvershootTargets >= 2, gap, performanceValid);
    }
}
