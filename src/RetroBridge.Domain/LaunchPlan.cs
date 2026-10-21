using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RetroBridge.Domain;

public static class LaunchPlan
{
    public static string SaveDirectory(string dataDirectory, string server, Guid user, Game game)
    {
        var serverId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server)));
        if (game.Platform is not ("nes" or "gb" or "gbc"))
            throw new ArgumentException("Unsupported platform.");
        if (string.IsNullOrEmpty(game.Sha256) || game.Sha256.Length != 64 || !game.Sha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid content checksum.");
        return Path.Combine(Path.GetFullPath(dataDirectory), "saves", serverId, user.ToString("N"), game.Platform, game.Sha256);
    }

    public static ProcessStartInfo Create(string executable, string core, string content, string config)
    {
        foreach (var path in new[] { executable, core, content, config })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new FileNotFoundException("Expected an existing absolute file path.", path);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-L", core, "--appendconfig", config, content })
            start.ArgumentList.Add(argument);
        return start;
    }

    public static string Configuration(string saveDirectory)
    {
        if (saveDirectory.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("Save path cannot contain quotes or newlines.");
        var path = saveDirectory.Replace('\\', '/');
        // No per-core subfolders: save sync expects <save directory>/<game id>.srm.
        return $"savefile_directory = \"{path}\"\nsavestate_directory = \"{path}\"\nconfig_save_on_exit = \"false\"\n" +
            "sort_savefiles_enable = \"false\"\nsort_savefiles_by_content_enable = \"false\"\n" +
            "sort_savestates_enable = \"false\"\nsort_savestates_by_content_enable = \"false\"\n";
    }
}
