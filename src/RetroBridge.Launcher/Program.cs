using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RetroBridge.Domain;

if (args.Length != 5)
{
    Console.WriteLine("Usage: RetroBridge.Launcher <server-url> <game-id> <retroarch-path> <core-path> <data-directory>");
    Console.WriteLine("Set RETROBRIDGE_TOKEN to a Jellyfin administrator USER access token. HTTPS required except loopback.");
    return args.Length == 1 && args[0] == "--help" ? 0 : 2;
}

try
{
    var server = new Uri(args[0].TrimEnd('/') + "/", UriKind.Absolute);
    if ((server.Scheme != "https" && !(server.Scheme == "http" && server.IsLoopback)) ||
        !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
        throw new ArgumentException("Use an HTTPS server URL (HTTP is allowed only on loopback), without credentials, query, or fragment.");
    var token = Environment.GetEnvironmentVariable("RETROBRIDGE_TOKEN");
    if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Set RETROBRIDGE_TOKEN to your Jellyfin user access token.");
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var client = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) };
    client.DefaultRequestHeaders.Add("X-Emby-Token", token);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var ct = cancellation.Token;
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var user = await client.GetFromJsonAsync<UserIdentity>("Users/Me", json, ct)
        ?? throw new InvalidOperationException("Server did not return a user identity.");
    if (user.Id == Guid.Empty) throw new InvalidOperationException("A user access token is required; server API keys are not supported.");
    var games = await client.GetFromJsonAsync<Game[]>("RetroBridge/Games", json, ct) ?? [];
    var game = games.SingleOrDefault(x => x.Id == args[1]) ?? throw new ArgumentException("Game not found. Scan the library and copy its game ID.");
    if (string.IsNullOrEmpty(game.Extension) || Platforms.FromExtension(game.Extension) != game.Platform || game.Size < 0 || game.Size > 64 * 1024 * 1024 ||
        game.Id != game.Platform + "-" + game.Sha256)
        throw new InvalidOperationException("Unsupported or invalid game metadata (prototype limit: 64 MiB).");
    var saveDirectory = LaunchPlan.SaveDirectory(args[4], server.AbsoluteUri, user.Id, game);
    Directory.CreateDirectory(saveDirectory);
    var cache = Path.Combine(Path.GetFullPath(args[4]), "cache");
    Directory.CreateDirectory(cache);
    var content = Path.Combine(cache, game.Id + game.Extension);
    // Serialize launches for a game across companion processes to protect its saves and cache.
    await using var gameLock = new FileStream(Path.Combine(saveDirectory, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (!await Verified(content, game, ct))
    {
        var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var response = await client.GetAsync("RetroBridge/Games/" + Uri.EscapeDataString(game.Id) + "/Content", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += count;
                    if (total > game.Size) throw new IOException("Download exceeded expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            if (!await Verified(temporary, game, ct)) throw new IOException("Downloaded content failed checksum verification. Rescan the server library.");
            File.Move(temporary, content, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    var config = Path.Combine(saveDirectory, "retrobridge.cfg");
    await File.WriteAllTextAsync(config, LaunchPlan.Configuration(saveDirectory), ct);
    var plan = LaunchPlan.Create(Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), content, config);
    Console.WriteLine($"Starting {game.Title}. Saves: {saveDirectory}");
    using var process = Process.Start(plan) ?? throw new InvalidOperationException("RetroArch could not be started.");
    // Keep the game lock until RetroArch exits, even if the CLI receives Ctrl+C.
    await process.WaitForExitAsync();
    Console.WriteLine($"RetroArch exited with code {process.ExitCode}.");
    return process.ExitCode;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Launch cancelled."); return 130; }
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
{
    Console.Error.WriteLine("Launch failed: " + ex.Message);
    return 1;
}

static async Task<bool> Verified(string path, Game game, CancellationToken ct)
{
    if (!File.Exists(path)) return false;
    await using var stream = File.OpenRead(path);
    return stream.Length == game.Size &&
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == game.Sha256;
}

internal sealed record UserIdentity(Guid Id);
