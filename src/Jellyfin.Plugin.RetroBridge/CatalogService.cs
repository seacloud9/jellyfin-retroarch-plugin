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
    }
}

public sealed class CatalogService
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly string? _snapshotPath;
    private IndexedGame[] _games;

    // Without a snapshot path the catalog is memory-only.
    public CatalogService(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath;
        _games = snapshotPath is null ? [] : CatalogSnapshot.Load(snapshotPath);
    }

    public IReadOnlyList<IndexedGame> Games => Volatile.Read(ref _games);

    public async Task<ScanResult?> ScanAsync(string[] roots, CancellationToken cancellationToken, bool full = false)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken)) return null;
        try
        {
            var result = await Task.Run(() => new CatalogScanner().ScanAsync(roots, cancellationToken, full ? null : Games), cancellationToken);
            // A failed or cancelled scan must not erase the previous catalog.
            if (result.Issues.Count == 0)
            {
                var games = result.Games.ToArray();
                if (_snapshotPath is not null) CatalogSnapshot.Save(_snapshotPath, games);
                Volatile.Write(ref _games, games);
            }
            return result;
        }
        finally { _scanLock.Release(); }
    }
}
