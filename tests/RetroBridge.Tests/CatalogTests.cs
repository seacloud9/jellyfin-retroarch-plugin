using Jellyfin.Plugin.RetroBridge;
using RetroBridge.Domain;

namespace RetroBridge.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retrobridge-tests-" + Guid.NewGuid().ToString("N"));
    public CatalogTests() => Directory.CreateDirectory(_root);
    private Task Write(string name, string content = "synthetic test content, not a playable ROM") => File.WriteAllTextAsync(Path.Combine(_root, name), content);

    [Fact]
    public async Task ScansSupportedFormatsAndDeduplicatesContent()
    {
        await Write("original.nes"); await Write("duplicate.NES"); await Write("ignored.zip");
        await Write("different.gb", "different content");
        var result = await new CatalogScanner().ScanAsync([_root]);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Games.Count);
        Assert.All(result.Games, x => Assert.Equal(64, x.Game.Sha256.Length));
    }

    [Fact]
    public async Task IdentitySurvivesRescanAndRenameButChangesWithContent()
    {
        await Write("old.nes");
        var scanner = new CatalogScanner();
        var first = Assert.Single((await scanner.ScanAsync([_root])).Games).Game;
        File.Move(Path.Combine(_root, "old.nes"), Path.Combine(_root, "new.nes"));
        var renamed = Assert.Single((await scanner.ScanAsync([_root])).Games).Game;
        Assert.Equal(first.Id, renamed.Id);
        Assert.Equal("new", renamed.Title);
        await Write("new.nes", "new revision");
        Assert.NotEqual(first.Id, Assert.Single((await scanner.ScanAsync([_root])).Games).Game.Id);
    }

    [Fact]
    public async Task MissingRootIsReportedAndOtherRootsAreScanned()
    {
        await Write("fixture.gbc");
        var result = await new CatalogScanner().ScanAsync([Path.Combine(_root, "missing"), _root]);
        Assert.Single(result.Games); Assert.Single(result.Issues);
    }

    [Fact]
    public async Task CancellationAndErrorsPreservePublishedSnapshot()
    {
        await Write("fixture.nes");
        var service = new CatalogService();
        await service.ScanAsync([_root], default);
        var original = Assert.Single(service.Games);
        await service.ScanAsync([Path.Combine(_root, "missing")], default);
        Assert.Equal(original, Assert.Single(service.Games));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync([_root], cancellation.Token));
        Assert.Equal(original, Assert.Single(service.Games));
    }

    [Fact]
    public void RejectsPathTraversal() => Assert.Throws<IOException>(() => SafeContent.Resolve(_root, "../outside.nes"));

    [Fact]
    public async Task RejectsSymlinkFilesAndDirectoryLoops()
    {
        if (OperatingSystem.IsWindows()) return; // Windows runners do not always permit symlink creation.
        await Write("original.nes");
        File.CreateSymbolicLink(Path.Combine(_root, "link.nes"), Path.Combine(_root, "original.nes"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);
        var result = await new CatalogScanner().ScanAsync([_root]);
        Assert.Single(result.Games); Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public async Task PublishedCatalogSurvivesServiceRestart()
    {
        await Write("persisted.nes");
        var snapshot = Path.Combine(_root, "data", "catalog.json");
        var scanned = (await new CatalogService(snapshot).ScanAsync([Path.Combine(_root)], default))!.Games;
        var restarted = new CatalogService(snapshot);
        Assert.Equal(scanned, restarted.Games);
        Assert.Empty(new CatalogService().Games);
    }

    [Fact]
    public async Task FailedScanDoesNotOverwriteSnapshot()
    {
        await Write("kept.nes");
        var snapshot = Path.Combine(_root, "data", "catalog.json");
        var service = new CatalogService(snapshot);
        await service.ScanAsync([_root], default);
        var failed = await service.ScanAsync([Path.Combine(_root, "missing")], default);
        Assert.NotEmpty(failed!.Issues);
        Assert.Equal("kept", Assert.Single(new CatalogService(snapshot).Games).Game.Title);
    }

    [Fact]
    public async Task CorruptOrTamperedSnapshotIsNotTrusted()
    {
        var snapshot = Path.Combine(_root, "catalog.json");
        await File.WriteAllTextAsync(snapshot, "{ not json");
        Assert.Empty(new CatalogService(snapshot).Games);

        await Write("valid.nes");
        var valid = Assert.Single((await new CatalogScanner().ScanAsync([_root])).Games);
        var tampered = valid with { Game = valid.Game with { Id = "gb-" + valid.Game.Sha256 } };
        var absolute = valid with { RelativePath = Path.Combine(_root, "valid.nes") };
        CatalogSnapshot.Save(snapshot, [valid, tampered, absolute]);
        Assert.Equal(valid, Assert.Single(CatalogSnapshot.Load(snapshot)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task RescansOnlyHashNewOrChangedFiles()
    {
        await Write("one.nes", "first"); await Write("two.gb", "second");
        var scanner = new CatalogScanner();
        var first = await scanner.ScanAsync([_root]);
        Assert.Equal((2, 0), (first.Hashed, first.Reused));

        var unchanged = await scanner.ScanAsync([_root], default, first.Games);
        Assert.Equal((0, 2), (unchanged.Hashed, unchanged.Reused));
        Assert.Equal(first.Games, unchanged.Games);

        var edited = Path.Combine(_root, "one.nes");
        await File.WriteAllTextAsync(edited, "first, revised");
        File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(1));
        await Write("three.gbc", "third");
        var changed = await scanner.ScanAsync([_root], default, unchanged.Games);
        Assert.Equal((2, 1), (changed.Hashed, changed.Reused));
        Assert.Contains(changed.Games, x => x.RelativePath == "one.nes" && x.Game.Sha256 != first.Games.Single(g => g.RelativePath == "one.nes").Game.Sha256);
    }

    [Fact]
    public async Task SameSizeAndTimestampEditsNeedAFullRescan()
    {
        await Write("same.nes", "aaaa");
        var path = Path.Combine(_root, "same.nes");
        var stamp = File.GetLastWriteTimeUtc(path);
        var scanner = new CatalogScanner();
        var first = await scanner.ScanAsync([_root]);
        await File.WriteAllTextAsync(path, "bbbb");
        File.SetLastWriteTimeUtc(path, stamp);
        // Indistinguishable without hashing: the stale hash is kept (downloads then fail verification).
        Assert.Equal(first.Games, (await scanner.ScanAsync([_root], default, first.Games)).Games);
        Assert.NotEqual(first.Games[0].Game.Sha256, (await scanner.ScanAsync([_root])).Games[0].Game.Sha256);
    }

    [Fact]
    public async Task IncrementalStateSurvivesRestartThroughTheSnapshot()
    {
        await Write("kept.nes"); await Write("also.gb", "gb");
        var snapshot = Path.Combine(_root, "data", "catalog.json");
        await new CatalogService(snapshot).ScanAsync([_root], default);
        var restarted = new CatalogService(snapshot);
        var rescan = (await restarted.ScanAsync([_root], default))!;
        Assert.Equal((0, 2), (rescan.Hashed, rescan.Reused));
        var full = (await restarted.ScanAsync([_root], default, full: true))!;
        Assert.Equal((2, 0), (full.Hashed, full.Reused));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class TitleTests
{
    [Theory]
    [InlineData("Super_Mario_Bros_(USA)_[!]", "Super Mario Bros", "USA")]
    [InlineData("Tetris (World) (Rev 1)", "Tetris", "World")]
    [InlineData("Pokemon - Red Version (USA, Europe) (SGB Enhanced)", "Pokemon - Red Version", "USA, Europe")]
    [InlineData("Zelda (Beta) (japan)", "Zelda", "Japan")]
    [InlineData("homebrew-demo", "homebrew-demo", null)]
    [InlineData("Super Mario Bros. (World)", "Super Mario Bros.", "World")]
    [InlineData("(USA)", "(USA)", "USA")]
    public void CleansFileNames(string file, string title, string? region) => Assert.Equal((title, region), Titles.FromFileName(file));
}

public sealed class ArtworkMatcherTests
{
    private static readonly string[] Index =
    [
        "Super Mario Bros. (World)", "Super Mario Bros. (Japan) (Beta)", "Tetris (World) (Rev 1)", "Tetris (Japan)",
        "Legend of Zelda, The (USA)", "Legend of Zelda, The (Europe)", "Pokemon - Red Version (USA, Europe) (SGB Enhanced)",
        "Mega Man 2 (USA)", "Mega Man 2 (Europe)", "Q_bert (USA)",
    ];

    [Theory]
    [InlineData("Super Mario Bros. (World)", "Super Mario Bros.", "World", "Super Mario Bros. (World)")]
    [InlineData("smb", "Super Mario Bros", null, "Super Mario Bros. (World)")]
    [InlineData("The_Legend_of_Zelda_(E)", "The Legend of Zelda", null, "Legend of Zelda, The (USA)")]
    [InlineData("zelda", "The Legend of Zelda", "Europe", "Legend of Zelda, The (Europe)")]
    [InlineData("tetris-jp", "Tetris", "Japan", "Tetris (Japan)")]
    [InlineData("pokered", "Pokemon - Red Version", null, "Pokemon - Red Version (USA, Europe) (SGB Enhanced)")]
    [InlineData("Q*bert (USA)", "Q bert", "USA", "Q_bert (USA)")]
    [InlineData("homebrew", "My Homebrew", null, null)]
    public void FindsTheBestThumbnail(string file, string title, string? region, string? expected) =>
        Assert.Equal(expected, ArtworkMatcher.Best(file, title, region, Index));
}
