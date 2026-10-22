using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Jellyfin.Plugin.RetroBridge;

namespace RetroBridge.Tests;

public sealed class BrowserPlayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retrobridge-web-" + Guid.NewGuid().ToString("N"));

    internal static readonly byte[] Script = "console.log(1)"u8.ToArray();
    internal static readonly byte[] Core = Zip(("core_libretro.js", "core"u8.ToArray()), ("core_libretro.wasm", [0, 0x61, 0x73, 0x6D]),
        ("../../escape.txt", "x"u8.ToArray()), ("notes.txt", "x"u8.ToArray()));
    internal static readonly BrowserAsset[] TestAssets =
    [
        new("https://upstream.test/emu.js", Hash(Script), ["emu.js"]),
        new("https://upstream.test/core.zip", Hash(Core), ["core_libretro.js", "core_libretro.wasm"]),
    ];

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(data);
        return buffer.ToArray();
    }

    [Fact]
    public async Task DownloadsVerifiesAndExtractsOnlyExpectedFiles()
    {
        var service = new BrowserPlayService(_directory, new FakeUpstream(), TestAssets);
        Assert.False(service.Ready);
        await service.PrepareAsync(default);
        Assert.True(service.Ready);
        Assert.Equal(new[] { "core_libretro.js", "core_libretro.wasm", "emu.js" }, Directory.GetFiles(_directory).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_directory)!, "escape.txt")));
        Assert.Null(service.Open("notes.txt"));
        Assert.Null(service.Open("../catalog.json"));
    }

    [Fact]
    public async Task ChecksumMismatchWritesNothing()
    {
        var tampered = new BrowserAsset[] { new("https://upstream.test/emu.js", new string('0', 64), ["emu.js"]) };
        var service = new BrowserPlayService(_directory, new FakeUpstream(), tampered);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(default));
        Assert.False(service.Ready);
        Assert.Empty(Directory.Exists(_directory) ? Directory.GetFiles(_directory) : []);
    }

    [Fact]
    public void PinnedAssetsCoverEveryBrowserCore()
    {
        var files = BrowserPlayService.Assets.SelectMany(a => a.Files).ToHashSet();
        Assert.Contains("nostalgist.umd.js", files);
        foreach (var core in BrowserPlayService.Cores.Values)
            Assert.True(files.Contains(core + "_libretro.js") && files.Contains(core + "_libretro.wasm"), core);
        Assert.All(BrowserPlayService.Assets, a => Assert.Matches("^[0-9a-f]{64}$", a.Sha256));
        Assert.DoesNotContain("gambatte", string.Join(",", files));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    internal sealed class FakeUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/emu.js" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Script) },
                "/core.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Core) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
    }
}
