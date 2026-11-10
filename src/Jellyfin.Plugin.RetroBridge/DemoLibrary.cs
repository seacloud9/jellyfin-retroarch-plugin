namespace Jellyfin.Plugin.RetroBridge;

// Nova64 demo carts (MIT, from seacloud9/nova64 tag v0.5.7) built into the plugin. They are written to a folder in
// the server's data directory, which is always scanned alongside the administrator's game folders. They are part of
// every installation on purpose: players always have games that work, and the game folders only add more.
public sealed class DemoLibrary
{
    private const string Prefix = "Jellyfin.Plugin.RetroBridge.Demos.";

    public DemoLibrary(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    public static IReadOnlyList<string> Names { get; } = typeof(DemoLibrary).Assembly.GetManifestResourceNames()
        .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal) && x.EndsWith(".nova", StringComparison.Ordinal))
        .Select(x => x[Prefix.Length..]).Order(StringComparer.Ordinal).ToArray();

    // Writes missing or changed carts and removes carts a newer plugin no longer ships. Unchanged files keep their
    // timestamps, so scans do not rehash them.
    public void Install()
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var name in Names)
        {
            using var resource = typeof(DemoLibrary).Assembly.GetManifestResourceStream(Prefix + name)!;
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var data = buffer.ToArray();
            var path = Path.Combine(Directory, name);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) continue;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, true);
        }
        foreach (var stale in System.IO.Directory.GetFiles(Directory).Where(x => !Names.Contains(Path.GetFileName(x))))
            File.Delete(stale);
    }

    // Folders to scan: the administrator's game folders, plus the demos.
    public string[] Roots(PluginConfiguration configuration)
    {
        // A failed install leaves the folder as it was; the scan then reports it like any unreadable folder.
        try { Install(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return [.. configuration.GameRoots, Directory];
    }

    // Whether games under this indexed root may still be served with the current settings.
    public bool Allows(PluginConfiguration configuration, string root) =>
        configuration.GameRoots.Any(x => Path.GetFullPath(x) == root) || root == Directory;
}
