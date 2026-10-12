using System.Text.Json;
namespace AimSense;
internal sealed record SessionDifficulty(double DistanceScale, double SpeedScale, double Deadline);
internal sealed record CarriedBlock(BlockResult Result, IReadOnlyList<JsonElement> Events);
internal sealed record RecordedDisplay(int Width, int Height, double RefreshHz, string DeviceName, string DeviceId = "");
internal sealed record ResumeSessionState(string SourcePath, GameProfile Game, double Dpi, double Fov, double Distance,
    double Low, double High, int Seed, TrialMode[] Modes, string MotionVersion, string EvidenceVersion, RecordedDisplay Display,
    IReadOnlyList<CarriedBlock> Blocks, IReadOnlyDictionary<TrialMode, SessionDifficulty> Difficulties,
    IReadOnlyList<ModeSearch> Searches, CompositeValidation? Validation)
{
    public bool SearchEvidenceIsPrior { get; init; }
    public IReadOnlyList<CarriedBlock> ArchivedReview { get; init; } = [];
    public RecordedDisplay? PriorDisplay { get; init; }
    public TrialMode? PendingPilot => Modes.Where(m => !Searches.Any(s => s.Mode==m) && Blocks.Count(b=>b.Result.Request.Mode==m && b.Result.Request.Purpose==TrialPurpose.Pilot)<3)
        .Select(m => (TrialMode?)m).FirstOrDefault();
    public bool CanContinue => PendingPilot.HasValue || Searches.Any(s=>!s.IsComplete) || Validation is {IsComplete:false} ||
        Validation==null && Modes.Length==3 && Searches.Count==3 && Searches.All(s=>s.HasEvidence);
    public string Description => (SearchEvidenceIsPrior ? "显示条件已改变：原分项成绩只用于生成候选；当前条件下的综合复核独立计分。\n" : "") +
        (Validation!=null ? CompositeReviewPresentation.Summary(Validation)+"\n继续会从下一个未完成复核块开始，中断块从头重做。" :
        PendingPilot.HasValue ? $"继续{RecommendationReport.Name(PendingPilot.Value)}难度预检；已有完整块保留。" :
        Searches.Any(s=>!s.IsComplete) ? "继续未完成的分项搜索，已有成绩保留；综合复核另需主动同意。" : "分项搜索完成，继续后将询问是否开始18块综合复核。");
    public int ScheduleCursor
    {
        get
        {
            var last=Blocks.LastOrDefault(b=>b.Result.Request.Purpose==TrialPurpose.Search);
            return last==null ? 0 : (Array.IndexOf(Modes,last.Result.Request.Mode)+1)%Modes.Length;
        }
    }
}
