using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RetroBridge;

// Appears under Dashboard → Scheduled Tasks, where administrators can run it, change triggers, and see history.
public sealed class ScanTask(CatalogService catalog, DemoLibrary demos) : IScheduledTask
{
    public string Name => "Scan RetroBridge game library";
    public string Key => "RetroBridgeScan";
    public string Description => "Indexes new and changed games in the RetroBridge game folders and the built-in Nova64 demos.";
    public string Category => "RetroBridge";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // The total is unknown until the walk finishes; estimate from the current catalog.
        var expected = Math.Max(catalog.Games.Count, 1);
        var result = await catalog.ScanAsync(demos.Roots(Plugin.Instance!.Configuration), cancellationToken,
            progress: count => progress.Report(Math.Min(99, 100.0 * count / expected)))
            ?? throw new InvalidOperationException("A RetroBridge scan is already running.");
        progress.Report(100);
        // Failing the task surfaces unpublished scans in the task history.
        if (result.Issues.Count > 0)
            throw new IOException($"Scan not published; {result.Issues.Count} issue(s), first: {result.Issues[0].Path}: {result.Issues[0].Message}");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];
}
