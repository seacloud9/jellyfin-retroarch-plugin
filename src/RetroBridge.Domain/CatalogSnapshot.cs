using System.Text.Json;

namespace RetroBridge.Domain;

// Persists the last published catalog so it survives server restarts without a rescan.
public static class CatalogSnapshot
{
    private const int FormatVersion = 1;
    private sealed record Document(int Version, IndexedGame[] Games);

    public static void Save(string path, IReadOnlyList<IndexedGame> games)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Document(FormatVersion, games.ToArray()));
                stream.Flush(true);
            }
            // Replace atomically so a crash never leaves a partial snapshot.
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Returns an empty catalog for a missing, unreadable, or unknown-version snapshot; drops invalid entries.
    public static IndexedGame[] Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<Document>(stream);
            if (document?.Version != FormatVersion || document.Games is null) return [];
            return document.Games.Where(IsValid).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static bool IsValid(IndexedGame? item) =>
        item?.Game is { } game && item.Root is not null && item.RelativePath is not null &&
        Path.IsPathFullyQualified(item.Root) && !Path.IsPathRooted(item.RelativePath) &&
        game.Extension is not null && Platforms.FromExtension(game.Extension) == game.Platform &&
        game.Sha256 is { Length: 64 } && game.Sha256.All(char.IsAsciiHexDigit) &&
        game.Id == game.Platform + "-" + game.Sha256 && game.Size >= 0 && game.Title is not null;
}
