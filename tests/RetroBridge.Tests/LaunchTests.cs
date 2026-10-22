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

    private const string Ticket = "AbCdEfGhIjKlMnOpQrStUv-_0123456789";

    [Fact]
    public void LaunchLinkRoundTripsOnlyTheTicket() => Assert.Equal(Ticket, LaunchLink.ParseTicket(LaunchLink.Create(Ticket)));

    [Theory]
    [InlineData("retrobridge://launch?ticket=short")]
    [InlineData("retrobridge://launch?ticket=" + Ticket + "&server=https://evil")]
    [InlineData("retrobridge://launch?server=https://evil&ticket=" + Ticket)]
    [InlineData("retrobridge://launch?ticket=" + Ticket + "%26core=x")]
    [InlineData("retrobridge://launch/../x?ticket=" + Ticket)]
    [InlineData("retrobridge://evil?ticket=" + Ticket)]
    [InlineData("retrobridge://user@launch?ticket=" + Ticket)]
    [InlineData("retrobridge://launch:99?ticket=" + Ticket)]
    [InlineData("retrobridge://launch?ticket=" + Ticket + "#x")]
    [InlineData("https://launch?ticket=" + Ticket)]
    [InlineData("retrobridge://launch?ticket=" + Ticket + "\"; rm -rf ~")]
    [InlineData("not a url")]
    public void LaunchLinkRejectsAnythingButAnOpaqueTicket(string link) => Assert.Null(LaunchLink.ParseTicket(link));
}

public sealed class SaveSyncTests
{
    private static ServerSave Server(string sha, long revision) => new("nes-x.srm", sha, 8192, revision, DateTimeOffset.UnixEpoch, "retrobridge-a");

    [Theory]
    [InlineData(null, null, null, null, null, SyncAction.None)]
    [InlineData(null, null, null, "s1", 1L, SyncAction.Download)]        // fresh device
    [InlineData("l1", null, null, null, null, SyncAction.Upload)]        // first upload
    [InlineData("s1", null, null, "s1", 1L, SyncAction.None)]            // identical without history
    [InlineData("a", "a", 1L, "b", 2L, SyncAction.Download)]             // another device played
    [InlineData("b", "a", 1L, "a", 1L, SyncAction.Upload)]               // played here
    [InlineData("b", "a", 1L, "c", 2L, SyncAction.Conflict)]             // played in both places
    [InlineData("l1", null, null, "s1", 1L, SyncAction.Conflict)]        // existing local save meets server save
    public void Decides(string? local, string? lastSha, long? lastRevision, string? serverSha, long? serverRevision, SyncAction expected) =>
        Assert.Equal(expected, SaveSync.Decide(local, lastSha is null ? null : new(lastSha, lastRevision!.Value),
            serverSha is null ? null : Server(serverSha, serverRevision!.Value)));

    [Theory]
    [InlineData("nes-abc.srm", true)]
    [InlineData("nes-abc.rtc", true)]
    [InlineData("nes-abc.state", false)]
    [InlineData("nes-other.srm", false)]
    [InlineData("../nes-abc.srm", false)]
    [InlineData("nes-abc.srm/../../x", false)]
    public void OnlyBatterySavesForTheGameAreSynced(string name, bool expected) => Assert.Equal(expected, SaveSync.IsSyncedName("nes-abc", name));
}

public sealed class SecureFileTests
{
    [Fact]
    public void SecretsAreReadableOnlyByTheCurrentUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retrobridge-secure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "pairing.json");
            SecureFile.WriteAllText(path, "{\"Token\":\"secret\"}");
            Assert.Equal("{\"Token\":\"secret\"}", File.ReadAllText(path));
            Assert.True(SecureFile.IsPrivate(path));
            SecureFile.WriteAllText(path, "{\"Token\":\"rotated\"}");
            Assert.True(SecureFile.IsPrivate(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
