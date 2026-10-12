using System.IO;
using System.Text.Json;
namespace AimSense;
internal static class TraceReplay {
public static string Verify(string path)
{
    var output = new System.Text.StringBuilder();
    var events = new Dictionary<string, List<JsonElement>>();
    var results = new List<BlockResult>();
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    JsonElement? report = null;
    foreach (string line in File.ReadLines(path))
    {
        using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
        if (!root.TryGetProperty("Kind", out var kind)) continue;
        if (kind.GetString() == "sampleBatch")
        {
            string id = root.GetProperty("BlockId").GetString()!;
            if (!events.TryGetValue(id, out var items)) events[id] = items = [];
            items.AddRange(root.GetProperty("Events").EnumerateArray().Select(e => e.Clone()));
        }
        if (kind.GetString() == "block") results.Add(root.GetProperty("Result").Deserialize<BlockResult>(options)!);
        if (kind.GetString() == "report") report = root.Clone();
    }
    int count = 0;
    foreach (var expected in results)
    {
        if (expected.ScoreVersion != CalibrationScoring.Version)
            throw new InvalidOperationException($"旧评分版本 {expected.ScoreVersion} 保持可读，不能按 {CalibrationScoring.Version} 重算或混算。");
        if (!events.TryGetValue(expected.Request.Id, out var items)) throw new Exception("Missing observations for " + expected.Request.Id);
        VerifyBlock(expected, items);
        count++;
    }
    output.AppendLine($"Replay verified {count} completed block results and diagnostics.");
    if (count == 0) throw new Exception("No completed blocks available for replay.");
    if (report.HasValue)
    {
        var source = report.Value;
        var game = source.GetProperty("Game").ValueKind == JsonValueKind.String ? GameProfiles.Get(source.GetProperty("Game").GetString()!) : source.GetProperty("Game").Deserialize<GameProfile>(options)!;
        double dpi = source.GetProperty("Dpi").GetDouble();
        var searches = new List<ModeSearch>();
        foreach (var saved in source.GetProperty("Searches").EnumerateArray())
        {
            var search = new ModeSearch(saved.GetProperty("Mode").Deserialize<TrialMode>(options),
                saved.GetProperty("InitialLowCm360").GetDouble(), saved.GetProperty("InitialHighCm360").GetDouble(), saved.GetProperty("Seed").GetInt32());
            foreach (var block in saved.GetProperty("Results").Deserialize<BlockResult[]>(options)!) { search.NextRequest(); search.Submit(block); }
            searches.Add(search);
        }
        CompositeValidation? validation = null;
        if (source.TryGetProperty("Validation", out var savedValidation) && savedValidation.ValueKind != JsonValueKind.Null)
        {
            validation = new CompositeValidation(game, savedValidation.GetProperty("Bests").Deserialize<Dictionary<TrialMode, double>>(options)!, savedValidation.GetProperty("Seed").GetInt32(),
                savedValidation.TryGetProperty("EvidenceVersion", out var evidenceVersion) ? evidenceVersion.GetString()! : "composite-paired-1");
            foreach (var block in savedValidation.GetProperty("Results").Deserialize<BlockResult[]>(options)!) { validation.NextRequest(); validation.Submit(block); }
        }
        string actual = RecommendationReport.Build(game, dpi, searches, validation,
            source.TryGetProperty("TracePath", out var trace) ? trace.GetString() : null, !source.TryGetProperty("ReportFormatVersion",out var reportFormat) || reportFormat.GetInt32()<2);
        if (NormalizeText(actual) != NormalizeText(source.GetProperty("Text").GetString() ?? "")) throw new Exception("Recommendation report did not match recomputed evidence.");
        output.AppendLine("Replay verified complete recommendation text from saved paired comparisons and validation blocks.");
    }
    return output.ToString();
}

internal static string NormalizeText(string text) => text.Replace("\r\r\n", "\n").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
internal static void VerifyBlock(BlockResult expected, IEnumerable<JsonElement> items)
{
        if (expected.ScoreVersion != CalibrationScoring.Version) throw new InvalidOperationException("评分版本不支持重算：" + expected.ScoreVersion);
        var accumulator = new BlockTelemetryAccumulator(expected.Request.Mode);
        foreach (var e in items)
        {
            double Number(string name) => e.GetProperty(name).GetDouble();
            switch (e.GetProperty("Kind").GetString())
            {
                case "begin": accumulator.BeginTarget(Number("Time"), Number("Deadline")); break;
                case "observe": accumulator.Observe(Number("Time"), Number("Yaw"), Number("Pitch"), Number("TargetYaw"), Number("TargetPitch"), Number("Radius"), e.GetProperty("OnHead").GetBoolean()); break;
                case "shot": accumulator.Shot(Number("Time"), e.GetProperty("Hit").GetBoolean(), e.GetProperty("Timeout").GetBoolean()); break;
                case "invalidate": accumulator.Invalidate(e.GetProperty("Reason").GetString()!); break;
            }
        }
        var actual = accumulator.Complete(expected.Block);
        if (actual.Valid != expected.Valid || Math.Abs(actual.Score - expected.Score) > 1e-8 || actual.Hits != expected.Hits || actual.Misses != expected.Misses || actual.Timeouts != expected.Timeouts ||
            Math.Abs(actual.TrackingShare - expected.TrackingShare) > 1e-8 || Math.Abs(actual.MeanAngularErrorRadii - expected.MeanAngularErrorRadii) > 1e-8 || !SameDiagnostics(actual.Diagnostics, expected.Diagnostics))
            throw new Exception("Replay result mismatch: " + expected.Request.Id);
}
private static bool SameDiagnostics(BlockDiagnostics a, BlockDiagnostics b)
{
    bool Close(double? x, double? y) => x.HasValue == y.HasValue && (!x.HasValue || Math.Abs(x.Value - y!.Value) <= 1e-8);
    return a.Overshoots == b.Overshoots && a.Corrections == b.Corrections && a.TurnSamples == b.TurnSamples &&
        a.RecoverySamples == b.RecoverySamples && Close(a.MeanOvershootRadii, b.MeanOvershootRadii) &&
        Close(a.MeanTurnResponseSeconds, b.MeanTurnResponseSeconds) && Close(a.MeanRecoverySeconds, b.MeanRecoverySeconds);
}
}
