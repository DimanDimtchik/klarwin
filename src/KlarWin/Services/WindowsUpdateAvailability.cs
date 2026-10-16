namespace KlarWin.Services;

/// <summary>
/// Lightweight probe: are there pending Windows Update titles matching a package?
/// Used before linking to WU — if nothing matches, KlarWin opens the app's own update UI instead.
/// </summary>
public static class WindowsUpdateAvailability
{
    private static readonly object Gate = new();
    private static DateTime _cacheUtc = DateTime.MinValue;
    private static IReadOnlyList<string> _pendingTitles = [];
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const int ProbeTimeoutMs = 12_000;

    public static bool HasPendingMatching(params string[] needles)
    {
        if (needles.Length == 0) return false;
        var titles = GetPendingTitlesCached();
        foreach (var title in titles)
        {
            foreach (var needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle)
                    && title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> GetPendingTitlesCached()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cacheUtc < CacheDuration)
                return _pendingTitles;
        }

        var fresh = ProbePendingTitles();
        lock (Gate)
        {
            _pendingTitles = fresh;
            _cacheUtc = DateTime.UtcNow;
            return _pendingTitles;
        }
    }

    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _cacheUtc = DateTime.MinValue;
            _pendingTitles = [];
        }
    }

    private static IReadOnlyList<string> ProbePendingTitles()
    {
        try
        {
            var task = Task.Run(ProbePendingTitlesCore);
            if (!task.Wait(ProbeTimeoutMs))
                return [];

            return task.IsFaulted ? [] : task.Result;
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ProbePendingTitlesCore()
    {
        var list = new List<string>();
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null) return list;

            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            var count = (int)result.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    string title = result.Updates[i].Title?.ToString() ?? "";
                    if (title.Length > 0)
                        list.Add(title);
                }
                catch
                {
                    // skip single update
                }
            }
        }
        catch
        {
            // WU COM unavailable / offline
        }

        return list;
    }
}
