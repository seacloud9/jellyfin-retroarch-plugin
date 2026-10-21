using System.Text;
using System.Text.RegularExpressions;

namespace RetroBridge.Domain;

// Matches games to libretro-thumbnails names ("Super Mario Bros. (World)") from the server's directory index.
public static partial class ArtworkMatcher
{
    public static readonly IReadOnlyDictionary<string, string> Systems = new Dictionary<string, string>
    {
        ["nes"] = "Nintendo - Nintendo Entertainment System",
        ["gb"] = "Nintendo - Game Boy",
        ["gbc"] = "Nintendo - Game Boy Color",
    };

    private static readonly string[] PreferredRegions = ["USA", "World", "Europe", "Japan"];

    // libretro-thumbnails replaces these characters with '_' in file names.
    public static string ThumbnailName(string name) => new([.. name.Select(c => "&*/:`<>?\\|\"".Contains(c) ? '_' : c)]);

    public static string? Best(string fileStem, string title, string? region, IEnumerable<string> available)
    {
        var names = available as ICollection<string> ?? available.ToList();
        var exact = ThumbnailName(fileStem.Replace('_', ' '));
        if (names.Contains(exact)) return exact;
        var key = Key(title);
        if (key.Length == 0) return null;
        return names.Where(x => Key(StripTags(x)) == key)
            .OrderBy(x => Unwanted().IsMatch(x) ? 1 : 0)
            .ThenBy(x => region is not null && Tags(x).Contains(region, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => Rank(x))
            .ThenBy(x => x.Length)
            .ThenBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int Rank(string name)
    {
        var tags = Tags(name);
        var index = Array.FindIndex(PreferredRegions, r => tags.Any(t => t.Split(',', StringSplitOptions.TrimEntries).Contains(r)));
        return index < 0 ? PreferredRegions.Length : index;
    }

    private static string[] Tags(string name) => [.. TagPattern().Matches(name).Select(m => m.Groups[1].Value.Trim())];

    private static string StripTags(string name) => TagPattern().Replace(name, " ");

    // Case, punctuation, and a leading or trailing "The" do not matter: "Legend of Zelda, The" == "The Legend of Zelda".
    private static string Key(string title)
    {
        var lower = title.ToLowerInvariant().Trim();
        if (lower.EndsWith(", the", StringComparison.Ordinal)) lower = lower[..^5];
        if (lower.StartsWith("the ", StringComparison.Ordinal)) lower = lower[4..];
        var builder = new StringBuilder();
        foreach (var c in lower) if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
        return builder.ToString();
    }

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\((Beta|Proto|Prototype|Demo|Sample|Unl|Pirate)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Unwanted();
}
