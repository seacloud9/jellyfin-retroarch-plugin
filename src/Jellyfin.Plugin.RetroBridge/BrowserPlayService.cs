using System.IO.Compression;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record BrowserAsset(string Url, string Sha256, string[] Files);

// Experimental in-browser play. When an administrator enables it, the server downloads pinned, checksum-verified
// emulator files from their upstream projects and serves them to players. Nothing third-party ships in the plugin,
// and players never contact a CDN. Licenses: Nostalgist (MIT); RetroArch Emscripten builds (GPL-3.0) of
// FCEUmm (GPL-2.0-or-later), mGBA (MPL-2.0), ClownMDEmu (AGPL-3.0), and PCSX ReARMed (GPL-2.0-or-later).
// See docs/third-party.md.
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
        new("https://cdn.jsdelivr.net/gh/arianrhodsandlot/retroarch-emscripten-build@v1.22.2/retroarch/clownmdemu_libretro.zip",
            "a26892a765a1b0effad2591d1927db9431ea2d125f88bc231685d84681e22700", ["clownmdemu_libretro.js", "clownmdemu_libretro.wasm"]),
        new("https://cdn.jsdelivr.net/gh/arianrhodsandlot/retroarch-emscripten-build@v1.22.2/retroarch/pcsx_rearmed_libretro.zip",
            "4c058822c08d38df709e8a03a0e3a4209d178bada0478996a46d2cf058d6a530", ["pcsx_rearmed_libretro.js", "pcsx_rearmed_libretro.wasm"]),
    ];

    // Each browser core is linked with GPL-3.0 RetroArch, so its license must be GPL-3.0-compatible:
    // Gambatte (GPL-2.0-only) cannot be used, so GB/GBC use mGBA. Snes9x and Genesis Plus GX forbid commercial use,
    // which GPL-3.0 does not allow, so SNES has no browser core and Genesis uses ClownMDEmu. The pinned build has no
    // Nintendo 64 core. SNES and N64 therefore play natively only.
    public static readonly IReadOnlyDictionary<string, string> PinnedCores = new Dictionary<string, string>
    {
        ["nes"] = "fceumm", ["gb"] = "mgba", ["gbc"] = "mgba", ["gba"] = "mgba", ["genesis"] = "clownmdemu", ["psx"] = "pcsx_rearmed",
    };

    // The whole game is loaded into the page's memory, so very large discs are left to native play.
    public const long MaxGameBytes = 512L * 1024 * 1024;

    private readonly BrowserAsset[] _assets;
    private readonly HashSet<string> _served;
    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Core name by platform for this service's assets.
    public IReadOnlyDictionary<string, string> Cores { get; }

    // Tests supply their own assets and cores; production uses the pinned lists.
    public BrowserPlayService(string directory, HttpMessageHandler? handler = null, BrowserAsset[]? assets = null, IReadOnlyDictionary<string, string>? cores = null)
    {
        _directory = directory;
        _assets = assets ?? Assets;
        Cores = cores ?? PinnedCores;
        _served = _assets.SelectMany(x => x.Files).ToHashSet(StringComparer.Ordinal);
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBridge-Jellyfin/0.1");
    }

    public bool Ready => _served.All(Present);

    // A core is usable once the shared script and its own files are here. After an upgrade adds cores, the
    // systems that were already downloaded keep working until an administrator downloads the new ones.
    public bool CoreReady(string core)
    {
        string[] own = [core + "_libretro.js", core + "_libretro.wasm"];
        var shared = _served.Where(name => !name.Contains("_libretro.", StringComparison.Ordinal));
        return own.All(name => _served.Contains(name) && Present(name)) && shared.All(Present);
    }

    private bool Present(string name) => File.Exists(Path.Combine(_directory, name));

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
