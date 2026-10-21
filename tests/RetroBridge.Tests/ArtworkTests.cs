using System.Net;
using Jellyfin.Plugin.RetroBridge;
using RetroBridge.Domain;

namespace RetroBridge.Tests;

public sealed class ArtworkTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retrobridge-art-" + Guid.NewGuid().ToString("N"));
    private readonly FakeThumbnails _server = new();

    private static IndexedGame Game(string file, string platform = "nes")
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file)));
        var (title, region) = Titles.FromFileName(file);
        return new(new(platform + "-" + sha, title, platform, "." + platform, 1, sha, region), "/games", file + "." + platform);
    }

    private ArtworkService Service() => new(_directory, _server, "https://thumbs.test/");

    [Fact]
    public async Task DownloadsMatchedArtAndRemembersMisses()
    {
        _server.Index["Nintendo - Nintendo Entertainment System"] = ["Super Mario Bros. (World)", "Fake (USA)", "Huge (USA)"];
        _server.Images["Super Mario Bros. (World)"] = Png;
        _server.Images["Fake (USA)"] = "<html>not an image</html>"u8.ToArray();
        _server.Images["Huge (USA)"] = [.. Png, .. new byte[ArtworkService.MaxImageBytes]];
        var mario = Game("Super_Mario_Bros_(World)");
        var games = new[] { mario, Game("Fake (USA)"), Game("Huge (USA)"), Game("unknown homebrew") };
        var service = Service();

        Assert.Equal(new ArtworkResult(1, 1, 2, 0), await service.FetchMissingAsync(games, false, default));
        Assert.True(service.Has(mario.Game.Id));
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(_directory, mario.Game.Id + ".png")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

        var requests = _server.Requests;
        Assert.Equal(new ArtworkResult(0, 1, 2, 1), await service.FetchMissingAsync(games, false, default));
        Assert.DoesNotContain(_server.Log.Skip(requests), x => x.Contains("unknown"));
        _server.Index["Nintendo - Nintendo Entertainment System"].Add("Unknown Homebrew (World)");
        _server.Images["Unknown Homebrew (World)"] = Png;
        Assert.Equal(new ArtworkResult(0, 1, 2, 1), await service.FetchMissingAsync(games, false, default));
        Assert.Equal(1, (await Service().FetchMissingAsync(games, true, default))!.Fetched);
    }

    [Fact]
    public async Task ServerOutagesAreReportedNotThrown()
    {
        _server.Down = true;
        Assert.Equal(new ArtworkResult(0, 0, 1, 0), await Service().FetchMissingAsync([Game("Tetris (World)", "gb")], false, default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FakeThumbnails : HttpMessageHandler
    {
        public Dictionary<string, List<string>> Index { get; } = [];
        public Dictionary<string, byte[]> Images { get; } = [];
        public List<string> Log { get; } = [];
        public int Requests => Log.Count;
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("offline");
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Trim('/');
            Log.Add(path);
            var parts = path.Split('/');
            if (parts is [var system, "Named_Boxarts"] && Index.TryGetValue(system, out var names))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(string.Concat(names.Select(n => $"<a href=\"{Uri.EscapeDataString(n)}.png\">x</a>"))) });
            if (parts is [_, "Named_Boxarts", var file] && Images.TryGetValue(file[..^4], out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
