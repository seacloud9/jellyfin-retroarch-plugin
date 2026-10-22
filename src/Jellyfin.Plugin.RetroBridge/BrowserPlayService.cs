using System.IO.Compression;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record BrowserAsset(string Url, string Sha256, string[] Files);

// Experimental in-browser play. When an administrator enables it, the server downloads pinned, checksum-verified
// emulator files from their upstream projects and serves them to players. Nothing third-party ships in the plugin,
// and players never contact a CDN. Licenses: Nostalgist (MIT); RetroArch Emscripten builds (GPL-3.0) of
// FCEUmm (GPL-2.0-or-later) and mGBA (MPL-2.0). See docs/backlog.md.
public sealed class BrowserPlayService
{
    public const long MaxDownloadBytes = 20 * 1024 * 1024;

    // Nostalgist 0.23.0 requires cores from retroarch-emscripten-build v1.22.2; update both together.
    public static readonly BrowserAsset[] Assets =
    [
        new("https://cdn.jsdelivr.net/npm/nostalgist@0.23.0/dist/nostalgist.umd.js",
            "37ad8729399fa8164cf818932dc856c9cbcb243613866518d48020fe5025d9e1", ["nostalgist.umd.js"]),
        new("https://cdn.jsdelivr.net/gh/arianrhodsandlot/retroarch-emscripten-build@v1.22.2/retroarch/fceumm_libretro.zip",
            "c890126923a13586b7735c4a551e6010afe06ed059dc8fdeb6b55d8fadf2cf1e", ["fceumm_libretro.js", "fceumm_libretro.wasm"]),
        new("https://cdn.jsdelivr.net/gh/arianrhodsandlot/retroarch-emscripten-build@v1.22.2/retroarch/mgba_libretro.zip",
            "d195371c3ea626c9246e9d3ab82ded4d93f3f99f85634af1588724cc44d47644", ["mgba_libretro.js", "mgba_libretro.wasm"]),
    ];

    // Gambatte is GPL-2.0-only and cannot be combined with the GPL-3.0 RetroArch build, so GB/GBC use mGBA.
    public static readonly IReadOnlyDictionary<string, string> Cores = new Dictionary<string, string> { ["nes"] = "fceumm", ["gb"] = "mgba", ["gbc"] = "mgba" };

    private readonly BrowserAsset[] _assets;
    private readonly HashSet<string> _served;
    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Tests supply their own assets; production uses the pinned list.
    public BrowserPlayService(string directory, HttpMessageHandler? handler = null, BrowserAsset[]? assets = null)
    {
        _directory = directory;
        _assets = assets ?? Assets;
        _served = _assets.SelectMany(x => x.Files).ToHashSet(StringComparer.Ordinal);
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBridge-Jellyfin/0.1");
    }

    public bool Ready => _served.All(name => File.Exists(Path.Combine(_directory, name)));

    // Downloads anything missing. Each download must match its pinned SHA-256 before any file is written.
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (var asset in _assets.Where(a => a.Files.Any(f => !File.Exists(Path.Combine(_directory, f)))))
            {
                var data = await DownloadAsync(asset.Url, cancellationToken);
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != asset.Sha256)
                    throw new InvalidDataException($"{Path.GetFileName(asset.Url)} does not match its pinned checksum.");
                if (asset.Url.EndsWith(".zip", StringComparison.Ordinal))
                {
                    using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
                    foreach (var name in asset.Files)
                    {
                        // Only the expected entry names are extracted; archive paths never become file paths.
                        var entry = zip.Entries.SingleOrDefault(e => e.FullName == name) ?? throw new InvalidDataException($"{name} is missing from {Path.GetFileName(asset.Url)}.");
                        await using var input = entry.Open();
                        using var buffer = new MemoryStream();
                        await input.CopyToAsync(buffer, cancellationToken);
                        Write(name, buffer.ToArray());
                    }
                }
                else Write(asset.Files[0], data);
            }
        }
        finally { _lock.Release(); }
    }

    public (Stream File, string ContentType)? Open(string name)
    {
        if (!_served.Contains(name)) return null;
        var path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return null;
        return (File.OpenRead(path), name.EndsWith(".wasm", StringComparison.Ordinal) ? "application/wasm" : "text/javascript");
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("Download is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > MaxDownloadBytes) throw new InvalidDataException("Download is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private void Write(string name, byte[] data)
    {
        var path = Path.Combine(_directory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, true);
    }
}
