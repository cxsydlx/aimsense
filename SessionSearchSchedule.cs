namespace AimSense;

// Each mode retains its own paired scripts and evidence. Only their presentation order changes.
internal sealed class SessionSearchSchedule(TrialMode[] modes, int startingCursor = 0)
{
    private int _cursor = startingCursor;
    public BlockRequest? Next(IReadOnlyDictionary<TrialMode, ModeSearch> searches)
    {
        for (int i = 0; i < modes.Length; i++)
        {
            var mode = modes[_cursor];
            _cursor = (_cursor + 1) % modes.Length;
            if (searches.TryGetValue(mode, out var search) && search.NextRequest() is { } request)
                return request;
        }
        return null;
    }
}
