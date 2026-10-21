using System.Security.Cryptography;

namespace RetroBridge.Domain;

public sealed record Game(string Id, string Title, string Platform, string Extension, long Size, string Sha256);
// Length and ModifiedTicks (UTC) let later scans skip rehashing unchanged files; -1/0 means unknown.
public sealed record IndexedGame(Game Game, string Root, string RelativePath, long Length = -1, long ModifiedTicks = 0);
public sealed record ScanIssue(string Path, string Message);
public sealed record ScanResult(IReadOnlyList<IndexedGame> Games, IReadOnlyList<ScanIssue> Issues, int Hashed = 0, int Reused = 0);

public static class Platforms
{
    public static string? FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".nes" => "nes",
        ".gb" => "gb",
        ".gbc" => "gbc",
        _ => null
    };
}

public static class SafeContent
{
    // Roots are trusted administrator input; all descendants must be real files/directories.
    // The ROM library must not be writable by untrusted users (filesystem replacement races).
    public static string Resolve(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Content is outside the configured root.");
        var current = fullRoot;
        RejectLink(current);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links are not supported in game libraries.");
    }
}

public sealed class CatalogScanner
{
    // With a previous catalog, files whose size and modification time are unchanged keep their hash.
    // Downloads are still checksum-verified by the companion, so a missed change fails safely.
    public async Task<ScanResult> ScanAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default, IEnumerable<IndexedGame>? previous = null, Action<int>? progress = null)
    {
        var known = (previous ?? []).Where(x => x.Length >= 0)
            .GroupBy(x => (x.Root, x.RelativePath)).ToDictionary(x => x.Key, x => x.First());
        int hashed = 0, reused = 0;
        var games = new Dictionary<string, IndexedGame>(StringComparer.Ordinal);
        var issues = new List<ScanIssue>();
        foreach (var configuredRoot in roots.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(configuredRoot))
            {
                issues.Add(new(configuredRoot, "Choose an absolute directory path."));
                continue;
            }
            var root = Path.GetFullPath(configuredRoot);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SafeContent.Resolve(root, Path.GetRelativePath(root, directory));
                    foreach (var path in Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var relative = Path.GetRelativePath(root, path);
                            SafeContent.Resolve(root, relative);
                            if (Directory.Exists(path)) { pending.Push(path); continue; }
                            var extension = Path.GetExtension(path).ToLowerInvariant();
                            var platform = Platforms.FromExtension(extension);
                            if (platform is null) continue;
                            var info = new FileInfo(path);
                            var modified = info.LastWriteTimeUtc.Ticks;
                            string hash;
                            if (known.TryGetValue((root, relative), out var prior) && prior.Length == info.Length && prior.ModifiedTicks == modified && prior.Game.Platform == platform)
                            {
                                hash = prior.Game.Sha256;
                                reused++;
                            }
                            else
                            {
                                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                                hashed++;
                            }
                            progress?.Invoke(hashed + reused);
                            var id = platform + "-" + hash;
                            games.TryAdd(id, new(new(id, Path.GetFileNameWithoutExtension(path), platform, extension, info.Length, hash), root, relative, info.Length, modified));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { issues.Add(new(path, ex.Message)); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { issues.Add(new(directory, ex.Message)); }
            }
        }
        return new(games.Values.OrderBy(x => x.Game.Title, StringComparer.OrdinalIgnoreCase).ToArray(), issues, hashed, reused);
    }
}
