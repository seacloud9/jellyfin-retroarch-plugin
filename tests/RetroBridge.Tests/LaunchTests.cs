using RetroBridge.Domain;

namespace RetroBridge.Tests;

public sealed class LaunchTests
{
    private static readonly Game Game = new("nes-" + new string('a', 64), "Fixture", "nes", ".nes", 1, new string('a', 64));

    [Fact]
    public void SavesAreStableAndIsolatedByServerUserAndRevision()
    {
        var root = Path.GetTempPath(); var user = Guid.NewGuid();
        var baseline = LaunchPlan.SaveDirectory(root, "https://one/", user, Game);
        Assert.Equal(baseline, LaunchPlan.SaveDirectory(root, "https://one/", user, Game));
        Assert.NotEqual(baseline, LaunchPlan.SaveDirectory(root, "https://two/", user, Game));
        Assert.NotEqual(baseline, LaunchPlan.SaveDirectory(root, "https://one/", Guid.NewGuid(), Game));
        Assert.NotEqual(baseline, LaunchPlan.SaveDirectory(root, "https://one/", user, Game with { Sha256 = new string('b', 64) }));
    }

    [Fact]
    public void InvalidChecksumCannotEscapeSaveDirectory() => Assert.Throws<ArgumentException>(() => LaunchPlan.SaveDirectory(Path.GetTempPath(), "server", Guid.NewGuid(), Game with { Sha256 = "../../escape" }));

    [Fact]
    public void InvalidPlatformCannotEscapeSaveDirectory() => Assert.Throws<ArgumentException>(() => LaunchPlan.SaveDirectory(Path.GetTempPath(), "server", Guid.NewGuid(), Game with { Platform = "../../escape" }));

    [Theory]
    [InlineData("bad\"path")]
    [InlineData("bad\npath")]
    [InlineData("bad\rpath")]
    public void RejectsConfigInjection(string path) => Assert.Throws<ArgumentException>(() => LaunchPlan.Configuration(path));

    [Fact]
    public void ArgumentsPreserveSpacesAndShellMetacharacters()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrobridge launch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new[] { "emulator", "core with spaces", "game;echo.nes", "config" }.Select(x => Path.Combine(root, x)).ToArray();
            foreach (var path in paths) File.WriteAllText(path, "fixture");
            var result = LaunchPlan.Create(paths[0], paths[1], paths[2], paths[3]);
            Assert.False(result.UseShellExecute);
            Assert.Equal(new[] { "-L", paths[1], "--appendconfig", paths[3], paths[2] }, result.ArgumentList);
        }
        finally { Directory.Delete(root, true); }
    }
}
