using Jellyfin.Plugin.RetroBridge;
using RetroBridge.Domain;

namespace RetroBridge.Tests;

public sealed class DemoLibraryTests : IDisposable
{
    private readonly DemoLibrary _demos = new(Path.Combine(Path.GetTempPath(), "retrobridge-demos-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void ShipsNovaCartsWithReadableTitles()
    {
        Assert.Equal(8, DemoLibrary.Names.Count);
        Assert.Contains("Hello 3D.nova", DemoLibrary.Names);
        Assert.All(DemoLibrary.Names, x => Assert.Equal("nova64", Platforms.FromExtension(Path.GetExtension(x))));
    }

    [Fact]
    public void InstallWritesCartsKeepsUnchangedFilesAndRemovesStaleOnes()
    {
        _demos.Install();
        var hello = Path.Combine(_demos.Directory, "Hello 3D.nova");
        var written = File.GetLastWriteTimeUtc(hello);
        File.WriteAllText(Path.Combine(_demos.Directory, "Removed Demo.nova"), "old");
        File.WriteAllText(Path.Combine(_demos.Directory, "Demoscene.nova"), "tampered");
        _demos.Install();
        Assert.Equal(DemoLibrary.Names, Directory.GetFiles(_demos.Directory).Select(x => Path.GetFileName(x)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(written, File.GetLastWriteTimeUtc(hello));
        Assert.NotEqual("tampered", File.ReadAllText(Path.Combine(_demos.Directory, "Demoscene.nova")));
    }

    [Fact]
    public void DemosAreAlwaysScannedAlongsideGameFolders()
    {
        Assert.Equal([_demos.Directory], _demos.Roots(new PluginConfiguration()));
        var configuration = new PluginConfiguration { GameRoots = ["/games"] };
        Assert.Equal(["/games", _demos.Directory], _demos.Roots(configuration));
        Assert.True(_demos.Allows(configuration, _demos.Directory));
        Assert.True(_demos.Allows(configuration, Path.GetFullPath("/games")));
        Assert.False(_demos.Allows(configuration, Path.GetFullPath("/elsewhere")));
    }

    public void Dispose() { if (Directory.Exists(_demos.Directory)) Directory.Delete(_demos.Directory, true); }
}
