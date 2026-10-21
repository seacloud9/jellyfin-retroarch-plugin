using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record ArtworkResult(int Fetched, int Missing, int Failed, int Cached);

// Box art from the libretro thumbnails server. Only the Jellyfin server contacts it; players load images from Jellyfin.
// Images are stored by content ID, so remote names never become file paths.
public sealed partial class ArtworkService
{
    public const long MaxImageBytes = 4 * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(7);

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Loaded, HashSet<string> Names)> _indexes = new();

    public ArtworkService(string directory, HttpMessageHandler? handler = null, string baseUrl = "https://thumbnails.libretro.com/")
    {
        _directory = directory;
        _base = new Uri(baseUrl);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBridge-Jellyfin/0.1");
    }

    internal string PathFor(string gameId) => Path.Combine(_directory, gameId + ".png");
    private string CustomPathFor(string gameId) => Path.Combine(_directory, "custom", gameId);
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private string MissesPath => Path.Combine(_directory, "misses.json");

    public bool Has(string gameId) => File.Exists(CustomPathFor(gameId)) || File.Exists(PathFor(gameId));

    // An administrator's upload takes precedence over fetched art.
    public (Stream Image, string ContentType)? Open(string gameId)
    {
        if (File.Exists(CustomPathFor(gameId)))
        {
            var stream = File.OpenRead(CustomPathFor(gameId));
            var header = new byte[3];
            stream.ReadExactly(header);
            stream.Position = 0;
            return (stream, header.SequenceEqual(JpegSignature) ? "image/jpeg" : "image/png");
        }
        return File.Exists(PathFor(gameId)) ? (File.OpenRead(PathFor(gameId)), "image/png") : null;
    }

    // Accepts PNG or JPEG up to MaxImageBytes, identified by signature rather than the client's content type.
    public async Task SaveCustomAsync(string gameId, Stream input, CancellationToken cancellationToken)
    {
        var destination = CustomPathFor(gameId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > MaxImageBytes) throw new InvalidDataException("Images must be 4 MiB or smaller.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            var header = new byte[PngSignature.Length];
            int read;
            await using (var check = File.OpenRead(temporary)) read = await check.ReadAtLeastAsync(header, header.Length, false, cancellationToken);
            if (!(read == header.Length && header.SequenceEqual(PngSignature)) && !(read >= 3 && header.AsSpan(0, 3).SequenceEqual(JpegSignature)))
                throw new InvalidDataException("Upload a PNG or JPEG image.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public bool RemoveCustom(string gameId)
    {
        if (!File.Exists(CustomPathFor(gameId))) return false;
        File.Delete(CustomPathFor(gameId));
        return true;
    }

    // Fetches art for games without it. Recent misses are skipped unless retry is set. Returns null if a fetch is already running.
    public async Task<ArtworkResult?> FetchMissingAsync(IReadOnlyList<IndexedGame> games, bool retry, CancellationToken cancellationToken)
    {
        if (!await _fetchLock.WaitAsync(0, cancellationToken)) return null;
        try
        {
            Directory.CreateDirectory(_directory);
            var misses = retry ? [] : LoadMisses();
            int fetched = 0, missing = 0, failed = 0, cached = 0;
            foreach (var group in games.GroupBy(x => x.Game.Platform))
            {
                if (!ArtworkMatcher.Systems.TryGetValue(group.Key, out var system)) continue;
                HashSet<string> index;
                try { index = await IndexAsync(system, cancellationToken); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    failed += group.Count(x => !Has(x.Game.Id));
                    continue;
                }
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var game = item.Game;
                    if (Has(game.Id)) { cached++; continue; }
                    if (misses.TryGetValue(game.Id, out var missed) && DateTimeOffset.UtcNow - missed < MissLifetime) { missing++; continue; }
                    var name = ArtworkMatcher.Best(Path.GetFileNameWithoutExtension(item.RelativePath), game.Title, game.Region, index);
                    if (name is null) { misses[game.Id] = DateTimeOffset.UtcNow; missing++; continue; }
                    try
                    {
                        await DownloadAsync(new Uri(_base, Uri.EscapeDataString(system) + "/Named_Boxarts/" + Uri.EscapeDataString(name) + ".png"), PathFor(game.Id), cancellationToken);
                        misses.Remove(game.Id);
                        fetched++;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException) { failed++; }
                }
            }
            SaveMisses(misses);
            return new(fetched, missing, failed, cached);
        }
        finally { _fetchLock.Release(); }
    }

    private async Task<HashSet<string>> IndexAsync(string system, CancellationToken cancellationToken)
    {
        if (_indexes.TryGetValue(system, out var entry) && DateTimeOffset.UtcNow - entry.Loaded < IndexLifetime) return entry.Names;
        var html = await _http.GetStringAsync(new Uri(_base, Uri.EscapeDataString(system) + "/Named_Boxarts/"), cancellationToken);
        var names = Href().Matches(html).Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Where(x => !x.Contains('/')).ToHashSet(StringComparer.Ordinal);
        _indexes[system] = (DateTimeOffset.UtcNow, names);
        return names;
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"Artwork request returned {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Artwork is too large.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > MaxImageBytes) throw new InvalidDataException("Artwork is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            var header = new byte[PngSignature.Length];
            await using (var check = File.OpenRead(temporary))
                if (await check.ReadAtLeastAsync(header, header.Length, false, cancellationToken) < header.Length || !header.SequenceEqual(PngSignature))
                    throw new InvalidDataException("Artwork is not a PNG image.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Dictionary<string, DateTimeOffset> LoadMisses()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(MissesPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    private void SaveMisses(Dictionary<string, DateTimeOffset> misses) => File.WriteAllText(MissesPath, JsonSerializer.Serialize(misses));

    [GeneratedRegex("href=\"([^\"?#]+)\\.png\"")]
    private static partial Regex Href();
}
