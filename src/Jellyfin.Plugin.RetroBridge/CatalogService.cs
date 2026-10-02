using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) => services.AddSingleton<CatalogService>();
}

public sealed class CatalogService
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private IndexedGame[] _games = [];
    public IReadOnlyList<IndexedGame> Games => Volatile.Read(ref _games);

    public async Task<ScanResult?> ScanAsync(string[] roots, CancellationToken cancellationToken)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken)) return null;
        try
        {
            var result = await Task.Run(() => new CatalogScanner().ScanAsync(roots, cancellationToken), cancellationToken);
            // A failed or cancelled scan must not erase the previous catalog.
            if (result.Issues.Count == 0) Volatile.Write(ref _games, result.Games.ToArray());
            return result;
        }
        finally { _scanLock.Release(); }
    }
}
