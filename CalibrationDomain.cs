namespace AimSense;

internal enum TrialMode { Click, Track, MovingClick }
internal enum TrialPurpose { Pilot, Search, Validation }

internal sealed record GameProfile(string Id, double YawDegreesPerCount, bool TrackingFamily,
    string Source, string SourceVersion,
    double? ClickWeight = null, double? MovingClickWeight = null, double? TrackWeight = null)
{
    public double Sensitivity(double dpi, double cm360)
    {
        if (!double.IsFinite(dpi) || !double.IsFinite(cm360) || dpi <= 0 || cm360 <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        return 360 * 2.54 / (dpi * cm360 * YawDegreesPerCount);
    }
    public double Cm360(double dpi, double sensitivity) => Sensitivity(dpi, sensitivity);
    public double Edpi(double dpi, double cm360) => dpi * Sensitivity(dpi, cm360);
    // 模式权重默认由"点击系 / 跟随系"决定；自定义游戏可以逐项覆盖（见 GameProfileStore）。
    public double Weight(TrialMode mode) => mode switch
    {
        TrialMode.Click => ClickWeight ?? (TrackingFamily ? .20 : .45),
        TrialMode.MovingClick => MovingClickWeight ?? (TrackingFamily ? .30 : .35),
        _ => TrackWeight ?? (TrackingFamily ? .50 : .20)
    };
    public bool HasCustomWeights => ClickWeight.HasValue || MovingClickWeight.HasValue || TrackWeight.HasValue;
    // 自定义 ComboBox 模板不会应用 DisplayMemberPath（踩过两次：鼠标设备、显示器），
    // 所以下拉里显示什么一律由 ToString 决定；顺手把 yaw 也带出来。
    public override string ToString() =>
        $"{Id}（yaw {YawDegreesPerCount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}）";
}

internal static class GameProfiles
{
    public const string PresetVersion = "hipfire-yaw-2026-10-11-v1";
    public const string SourceUrl = "https://github.com/PLivdan/ballsheet";
    // 内置只保证这几个；其余游戏走 GameProfileStore 的自定义系数表（必须由用户给出 yaw）。
    public static readonly string[] BuiltInIds = ["CS2", "Valorant", "Apex", "OW2"];
    public static GameProfile Get(string game) => game.ToLowerInvariant() switch
    {
        "cs2" => new("CS2", .022, false, SourceUrl, PresetVersion),
        "valorant" => new("Valorant", .07, false, SourceUrl, PresetVersion),
        "apex" or "apex legends" => new("Apex", .022, true, SourceUrl, PresetVersion),
        "ow2" or "overwatch 2" or "overwatch" => new("OW2", .0066, true, SourceUrl, PresetVersion),
        _ => throw new ArgumentException("未知游戏，不能猜测转换系数。", nameof(game))
    };
}

internal sealed record BlockRequest(string Id, TrialMode Mode, TrialPurpose Purpose, double Cm360, int ScriptId);
internal sealed record TrialBlock(BlockRequest Request, double DurationSeconds = 12, int ExpectedShots = 6);
internal sealed record ScoreBreakdown(double FailureLoss, double TimeLoss, double OffTargetLoss,
    double AngularErrorLoss)
{
    public double Loss => Math.Clamp(FailureLoss + TimeLoss + OffTargetLoss + AngularErrorLoss, 0, 100);
    public double Score => 100 - Loss;
}
internal sealed record BlockDiagnostics(int Overshoots, int Corrections, double MeanOvershootRadii,
    double? MeanTurnResponseSeconds, double? MeanRecoverySeconds, int TurnSamples, int RecoverySamples);
internal sealed record BlockResult(TrialBlock Block, ScoreBreakdown Breakdown, bool Valid,
    string[] InvalidReasons, int Hits, int Misses, int Timeouts, double TrackingShare,
    double MeanAngularErrorRadii, double MeanShotSeconds, BlockDiagnostics Diagnostics,
    string ScoreVersion = CalibrationScoring.Version)
{
    public double Score => Breakdown.Score;
    public double Loss => Breakdown.Loss;
    public BlockRequest Request => Block.Request;
}
internal sealed record ComparisonEvidence(string Stage, double BaselineCm360, double CandidateCm360,
    int[] ScriptIds, double[] PairedImprovements, bool Complete, bool Accepted, string Reason)
{
    public double MeanImprovement => PairedImprovements.Length == 0 ? 0 : PairedImprovements.Average();
}
