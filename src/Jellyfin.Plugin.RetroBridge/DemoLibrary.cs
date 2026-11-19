using System.Security.Cryptography;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

// The starter library lives in the assembly. Its IDs match scanned content so upgrades preserve
// title overrides, access rules, saves and direct links from the old extracted demo folder.
public static class DemoLibrary
{
    private const string Prefix = "Jellyfin.Plugin.RetroBridge.Demos.";
    public static IReadOnlyList<IndexedGame> Games { get; } = typeof(DemoLibrary).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".nova", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .Select(resource =>
        {
            using var cart = typeof(DemoLibrary).Assembly.GetManifestResourceStream(resource)!;
            var hash = Convert.ToHexStringLower(SHA256.HashData(cart));
            var file = resource[Prefix.Length..];
            var game = new Game("nova64-" + hash, Path.GetFileNameWithoutExtension(file), "nova64", ".nova", cart.Length, hash, IsBundled: true);
            return new IndexedGame(game, "", file);
        }).ToArray();

    public static Stream OpenCart(IndexedGame game) =>
        typeof(DemoLibrary).Assembly.GetManifestResourceStream(Prefix + game.RelativePath)!;

    public static Stream OpenScreenshot(IndexedGame game) =>
        typeof(DemoLibrary).Assembly.GetManifestResourceStream(Prefix + Path.ChangeExtension(game.RelativePath, ".png"))!;
}
