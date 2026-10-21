using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton(provider =>
            // DataPath outlives plugin directory replacement during upgrades.
            new CatalogService(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "retrobridge", "catalog.json")));
        services.AddSingleton<LaunchTickets>();
        services.AddSingleton<IPairedDevices, JellyfinPairedDevices>();
        services.AddSingleton<Func<PluginConfiguration>>(() => Plugin.Instance!.Configuration);
        services.AddSingleton<Action<PluginConfiguration>>(configuration => Plugin.Instance!.UpdateConfiguration(configuration));
    }
}

public sealed record ScanStatus(bool Running, int Processed, ScanResult? Last, DateTimeOffset? LastCompleted);

public sealed class CatalogService
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly string? _snapshotPath;
    private IndexedGame[] _games;
    private int _processed;
    private volatile bool _running;
    private ScanResult? _last;
    private DateTimeOffset? _lastCompleted;

    // Without a snapshot path the catalog is memory-only.
    public CatalogService(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath;
        _games = snapshotPath is null ? [] : CatalogSnapshot.Load(snapshotPath);
    }

    public IReadOnlyList<IndexedGame> Games => Volatile.Read(ref _games);

    public ScanStatus Status => new(_running, Volatile.Read(ref _processed), Volatile.Read(ref _last), _lastCompleted);

    // Returns null when another scan is running. progress receives the number of game files processed so far.
    public async Task<ScanResult?> ScanAsync(string[] roots, CancellationToken cancellationToken, bool full = false, Action<int>? progress = null)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken)) return null;
        try
        {
            _running = true;
            Volatile.Write(ref _processed, 0);
            var result = await Task.Run(() => new CatalogScanner().ScanAsync(roots, cancellationToken, full ? null : Games, count =>
            {
                Volatile.Write(ref _processed, count);
                progress?.Invoke(count);
            }), cancellationToken);
            // A failed or cancelled scan must not erase the previous catalog.
            if (result.Issues.Count == 0)
            {
                var games = result.Games.ToArray();
                if (_snapshotPath is not null) CatalogSnapshot.Save(_snapshotPath, games);
                Volatile.Write(ref _games, games);
            }
            Volatile.Write(ref _last, result);
            _lastCompleted = DateTimeOffset.UtcNow;
            return result;
        }
        finally { _running = false; _scanLock.Release(); }
    }

    // Starts a scan without waiting for it; false when one is already running.
    public bool TryStartBackground(string[] roots, bool full)
    {
        if (_scanLock.CurrentCount == 0) return false;
        _ = Task.Run(async () =>
        {
            try { await ScanAsync(roots, CancellationToken.None, full); }
            // Scan problems are reported through Issues; anything else must not crash the server.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        });
        return true;
    }
}
