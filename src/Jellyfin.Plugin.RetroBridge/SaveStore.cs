using System.Security.Cryptography;
using System.Text.Json;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record SaveWriteResult(ServerSave? Saved, ServerSave? Current);

// Battery saves per user and game: <root>/<user>/<game id>/<name>, metadata in saves.json, and the previous
// versions in history/ so a conflicting upload never destroys data. Names are validated by SaveSync.IsSyncedName,
// game IDs by the catalog, and users are GUIDs, so no client value reaches a path unchecked.
public sealed class SaveStore(string root)
{
    public const int KeepHistory = 5;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string GameDirectory(Guid user, string gameId)
    {
        if (gameId.Length is 0 or > 80 || !gameId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid game ID.");
        return Path.Combine(root, user.ToString("N"), gameId);
    }

    private static Dictionary<string, ServerSave> Load(string directory)
    {
        var path = Path.Combine(directory, "saves.json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, ServerSave>>(File.ReadAllText(path)) ?? [];
    }

    public IReadOnlyList<ServerSave> List(Guid user, string gameId) => [.. Load(GameDirectory(user, gameId)).Values.OrderBy(x => x.Name, StringComparer.Ordinal)];

    // The most recent save per game for a user, for the play page.
    public IReadOnlyDictionary<string, ServerSave> Latest(Guid user)
    {
        var directory = Path.Combine(root, user.ToString("N"));
        if (!Directory.Exists(directory)) return new Dictionary<string, ServerSave>();
        return Directory.GetDirectories(directory)
            .Select(game => (Game: Path.GetFileName(game), Save: Load(game).Values.MaxBy(x => x.Modified)))
            .Where(x => x.Save is not null)
            .ToDictionary(x => x.Game, x => x.Save!);
    }

    public Stream? Open(Guid user, string gameId, string name)
    {
        if (!SaveSync.IsSyncedName(gameId, name)) return null;
        var path = Path.Combine(GameDirectory(user, gameId), name);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    // Optimistic concurrency: baseRevision must equal the current revision (0 when the server has none).
    public async Task<SaveWriteResult> WriteAsync(Guid user, string gameId, string name, Stream body, long baseRevision, string device, CancellationToken cancellationToken)
    {
        if (!SaveSync.IsSyncedName(gameId, name)) throw new ArgumentException("Only battery saves named after the game can be synced.");
        var directory = GameDirectory(user, gameId);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, name + "." + Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > SaveSync.MaxBytes) throw new InvalidDataException("Saves must be 1 MiB or smaller.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            string sha;
            await using (var input = File.OpenRead(temporary)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            var size = new FileInfo(temporary).Length;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                var saves = Load(directory);
                saves.TryGetValue(name, out var current);
                if ((current?.Revision ?? 0) != baseRevision) return new(null, current);
                if (current?.Sha256 == sha) return new(current, current);
                var target = Path.Combine(directory, name);
                if (current is not null && File.Exists(target))
                {
                    var history = Path.Combine(directory, "history");
                    Directory.CreateDirectory(history);
                    File.Copy(target, Path.Combine(history, $"{name}.{current.Revision}"), true);
                    foreach (var old in Directory.GetFiles(history, name + ".*")
                                 .OrderByDescending(x => long.TryParse(Path.GetExtension(x).TrimStart('.'), out var r) ? r : 0).Skip(KeepHistory))
                        File.Delete(old);
                }
                File.Move(temporary, target, true);
                var saved = new ServerSave(name, sha, size, (current?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, device);
                saves[name] = saved;
                var metadata = Path.Combine(directory, "saves.json");
                var pending = metadata + ".tmp";
                await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(saves), cancellationToken);
                File.Move(pending, metadata, true);
                return new(saved, saved);
            }
            finally { _lock.Release(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
