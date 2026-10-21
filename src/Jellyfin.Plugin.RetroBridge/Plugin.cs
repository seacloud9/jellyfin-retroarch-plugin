using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RetroBridge;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string[] GameRoots { get; set; } = [];

    // Non-administrator Jellyfin users allowed to list, launch, and download games. Administrators always may.
    public string[] PlayerUserIds { get; set; } = [];

    // Administrator-chosen titles by content ID; they survive renames and rescans.
    public TitleOverride[] TitleOverrides { get; set; } = [];

    // Fetch box art from thumbnails.libretro.com after scans. Only the server contacts it.
    public bool FetchArtwork { get; set; } = true;
}

public sealed class TitleOverride
{
    public string GameId { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "RetroBridge";
    public override string Description => "Experimental native RetroArch game library (prototype).";
    public override Guid Id => Guid.Parse("4be0a14e-f03e-4e52-b702-3e38b8cedf17");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "RetroBridge", EmbeddedResourcePath = "Jellyfin.Plugin.RetroBridge.Configuration.configPage.html" },
        // Main-menu entry for players; it hands off to the standalone /RetroBridge/Play page.
        new()
        {
            Name = "RetroBridgePlay",
            DisplayName = "Retro Games",
            EnableInMainMenu = true,
            MenuIcon = "sports_esports",
            EmbeddedResourcePath = "Jellyfin.Plugin.RetroBridge.Configuration.menuPage.html",
        },
    ];
}
