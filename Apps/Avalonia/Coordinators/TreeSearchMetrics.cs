namespace DevProjex.Avalonia.Coordinators;

public interface ITreeSearchMetricsSink
{
    void RecordTreeSearch(TreeSearchMetrics metrics);

    void RecordTreeSearchNavigation(int step, int currentIndex, int matchCount);
}

public readonly record struct TreeSearchMetrics(
    string Query,
    TimeSpan Duration,
    int TotalNodes,
    int MatchCount,
    bool UsedCache);
