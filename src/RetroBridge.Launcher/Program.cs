using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RetroBridge.Domain;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;

try
{
    switch (args)
    {
        case ["pair", var serverArg, .. var rest]:
            return await Pair(ParseServer(serverArg), DataDirectory(rest is [var d] ? d : Option(rest, "--data")));
        case ["configure", .. var rest]:
            {
                var data = DataDirectory(Option(rest, "--data"));
                var cores = Options(rest, "--core").Select(x => x.Split('=', 2)).ToDictionary(
                    x => x.Length == 2 && x[0] is "nes" or "gb" or "gbc" ? x[0] : throw new ArgumentException("Use --core <nes|gb|gbc>=<absolute core path>."), x => x[1]);
                LocalSettings.Save(data, new(Option(rest, "--retroarch") ?? throw new ArgumentException("--retroarch is required."), cores));
                Console.WriteLine($"Saved launch settings in {data}.");
                return 0;
            }
        // Invoked by the OS link handler. There is no console, so output goes to a log in the data directory.
        case ["open", var link, .. var rest]:
            {
                var data = DataDirectory(Option(rest, "--data"));
                var ticket = LaunchLink.ParseTicket(link) ?? throw new ArgumentException("Not a valid RetroBridge launch link.");
                Directory.CreateDirectory(data);
                var logPath = Path.Combine(data, "launcher.log");
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.Delete(logPath);
                // Not disposed here: the error handler below must still be able to write to it.
                var log = new StreamWriter(logPath, true) { AutoFlush = true };
                Console.SetOut(log); Console.SetError(log);
                Console.WriteLine($"[{DateTimeOffset.Now:u}] Opening launch link.");
                var settings = LocalSettings.Load(data);
                return await Redeem(data, ticket, game => (settings.RetroArch,
                    settings.Cores.GetValueOrDefault(game.Platform) ?? throw new InvalidOperationException($"No core is configured for {game.Platform}. Run configure --core {game.Platform}=<path>.")));
            }
        case ["listen", .. var rest]:
            {
                var background = rest.Contains("--background");
                return await Listen(DataDirectory(Option(rest.Where(x => x != "--background").ToArray(), "--data")), background);
            }
        case ["autostart", "on", .. var rest]:
            {
                var data = DataDirectory(Option(rest, "--data"));
                LocalSettings.Load(data);
                Pairing.Load(data);
                Console.WriteLine(Autostart.Enable(data));
                return 0;
            }
        case ["autostart", "off"]:
            Console.WriteLine(Autostart.Disable());
            return 0;
        case ["autostart", "status"]:
            Console.WriteLine(Autostart.Status());
            return 0;
        case ["launch", var data, var ticket, var executable, var core]:
            return await Redeem(data, ticket, _ => (executable, core));
        case ["register", .. var rest]:
            {
                var data = DataDirectory(Option(rest, "--data"));
                LocalSettings.Load(data);
                Pairing.Load(data);
                Console.WriteLine(LinkHandler.Register(data));
                return 0;
            }
        case ["unregister"]:
            Console.WriteLine(LinkHandler.Unregister());
            return 0;
        // Development path: an existing user token in RETROBRIDGE_TOKEN and a game ID.
        case [var serverArg, var gameId, var executable, var core, var data]:
            {
                var server = ParseServer(serverArg);
                var token = Environment.GetEnvironmentVariable("RETROBRIDGE_TOKEN");
                if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Set RETROBRIDGE_TOKEN to your Jellyfin user access token.");
                using var client = CreateClient(server, token, "retrobridge-launcher");
                var user = await CurrentUser(client);
                var games = await client.GetFromJsonAsync<Game[]>("RetroBridge/Games", json, ct) ?? [];
                var game = games.SingleOrDefault(x => x.Id == gameId) ?? throw new ArgumentException("Game not found. Scan the library and copy its game ID.");
                return await Launch(client, server, user, game, executable, core, data);
            }
        default:
            Console.WriteLine("Usage:");
            Console.WriteLine("  RetroBridge.Launcher pair <server-url> [--data <dir>]");
            Console.WriteLine("  RetroBridge.Launcher configure --retroarch <path> --core nes=<path> [--core gb=<path>] [--core gbc=<path>] [--data <dir>]");
            Console.WriteLine("  RetroBridge.Launcher register [--data <dir>]      (handle retrobridge:// links from the play page)");
            Console.WriteLine("  RetroBridge.Launcher unregister");
            Console.WriteLine("  RetroBridge.Launcher listen [--data <dir>]          (start games sent from any browser, including phones)");
            Console.WriteLine("  RetroBridge.Launcher autostart on|off|status [--data <dir>]   (listen automatically at login)");
            Console.WriteLine("  RetroBridge.Launcher open <retrobridge://launch?ticket=...> [--data <dir>]");
            Console.WriteLine("  RetroBridge.Launcher launch <data-directory> <ticket> <retroarch-path> <core-path>");
            Console.WriteLine($"Default data directory: {DataDirectory(null)}");
            Console.WriteLine("  RetroBridge.Launcher <server-url> <game-id> <retroarch-path> <core-path> <data-directory>  (development; RETROBRIDGE_TOKEN)");
            Console.WriteLine("HTTPS required except loopback.");
            return args is ["--help"] ? 0 : 2;
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
{
    Console.Error.WriteLine("Failed: " + ex.Message);
    return 1;
}

static string? Option(string[] args, string name) => Options(args, name).LastOrDefault();

static IEnumerable<string> Options(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
        if (args[i] == name) yield return i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value.");
        else if (args[i].StartsWith("--", StringComparison.Ordinal) && args[i] is not ("--data" or "--core" or "--retroarch")) throw new ArgumentException($"Unknown option {args[i]}.");
}

static string DataDirectory(string? value)
{
    if (value is not null) return Path.GetFullPath(value);
    var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
    if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("No per-user data folder is available; pass --data <dir>.");
    return Path.Combine(root, "RetroBridge");
}

async Task<int> Redeem(string data, string ticket, Func<Game, (string Executable, string Core)> resolve)
{
    var pairing = Pairing.Load(data);
    var server = ParseServer(pairing.Server);
    using var client = CreateClient(server, pairing.Token, pairing.DeviceId);
    using var response = await client.PostAsJsonAsync("RetroBridge/LaunchTickets/Redeem", new { Ticket = ticket }, json, ct);
    if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("This device is no longer paired. Run pair again.");
    if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Launch ticket is invalid or expired. Start the game again.");
    response.EnsureSuccessStatusCode();
    var game = await response.Content.ReadFromJsonAsync<Game>(json, ct) ?? throw new InvalidOperationException("Server returned no game.");
    var (executable, core) = resolve(game);
    return await Launch(client, server, await CurrentUser(client), game, executable, core, data);
}

// Checks in with the server and launches games sent to this device, one at a time, until cancelled.
// In background mode (autostart) output goes to listener.log, and conditions that a restart cannot fix
// (unpaired, access removed) end with exit code 0 so the service manager does not restart it in a loop.
async Task<int> Listen(string data, bool background)
{
    if (background)
    {
        Directory.CreateDirectory(data);
        var logPath = Path.Combine(data, "listener.log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.Delete(logPath);
        var log = new StreamWriter(logPath, true) { AutoFlush = true };
        Console.SetOut(log); Console.SetError(log);
    }
    var pairing = Pairing.Load(data);
    var settings = LocalSettings.Load(data);
    // One listener per data directory, so an autostarted and a manual listener cannot both claim launches.
    FileStream listenerLock;
    try { listenerLock = new FileStream(Path.Combine(data, "listener.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException)
    {
        Console.Error.WriteLine("Another listener is already running for this data directory.");
        return background ? 0 : 1;
    }
    using var _ = listenerLock;
    var server = ParseServer(pairing.Server);
    using var client = CreateClient(server, pairing.Token, pairing.DeviceId);
    client.Timeout = TimeSpan.FromSeconds(30);
    var delay = TimeSpan.FromSeconds(3);
    Guid user;
    // At login the network may not be up yet; keep retrying instead of exiting.
    while (true)
    {
        try { user = await CurrentUser(client); break; }
        catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:u}] Server unreachable ({ex.Message}); retrying.");
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine("This device is no longer paired. Run pair again.");
            return background ? 0 : 1;
        }
    }
    delay = TimeSpan.FromSeconds(3);
    Console.WriteLine($"[{DateTimeOffset.Now:u}] Listening for games sent to this device ({pairing.DeviceId}). Press Ctrl+C to stop.");
    while (!ct.IsCancellationRequested)
    {
        try
        {
            using var response = await client.PostAsync("RetroBridge/LaunchTickets/Claim", null, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Console.Error.WriteLine(response.StatusCode == HttpStatusCode.Unauthorized ? "This device is no longer paired. Run pair again." : "Your account no longer has RetroBridge access.");
                return background ? 0 : 1;
            }
            response.EnsureSuccessStatusCode();
            delay = TimeSpan.FromSeconds(3);
            if (response.StatusCode == HttpStatusCode.OK && await response.Content.ReadFromJsonAsync<Game>(json, ct) is { } game)
            {
                try
                {
                    var core = settings.Cores.GetValueOrDefault(game.Platform) ?? throw new InvalidOperationException($"No core is configured for {game.Platform}. Run configure --core {game.Platform}=<path>.");
                    await Launch(client, server, user, game, settings.RetroArch, core, data);
                }
                // A failed launch must not stop the listener.
                catch (Exception ex) when (ex is ArgumentException or IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { Console.Error.WriteLine($"Could not start {game.Title}: {ex.Message}"); }
                continue;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Server unreachable ({ex.Message}); retrying.");
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        await Task.Delay(delay, ct);
    }
    return 0;
}

static Uri ParseServer(string value)
{
    var server = new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    if ((server.Scheme != "https" && !(server.Scheme == "http" && server.IsLoopback)) ||
        !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
        throw new ArgumentException("Use an HTTPS server URL (HTTP is allowed only on loopback), without credentials, query, or fragment.");
    return server;
}

static HttpClient CreateClient(Uri server, string? token, string deviceId)
{
    if (token is not null && token.Any(c => c == '"' || c == '\\' || char.IsControl(c))) throw new ArgumentException("The access token contains invalid characters.");
    var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) };
    // Jellyfin 12.1 ignores X-Emby-Token; it requires the MediaBrowser Authorization scheme.
    // Jellyfin binds access tokens to DeviceId, so pairing and later requests must use the same one.
    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
        $"MediaBrowser Client=\"RetroBridge\", Device=\"{new string(Environment.MachineName.Where(char.IsAsciiLetterOrDigit).ToArray())}\", DeviceId=\"{deviceId}\", Version=\"{typeof(Game).Assembly.GetName().Version?.ToString(3)}\"" +
        (token is null ? "" : $", Token=\"{token}\""));
    return client;
}

async Task<Guid> CurrentUser(HttpClient client)
{
    var user = await client.GetFromJsonAsync<UserIdentity>("Users/Me", json, ct)
        ?? throw new InvalidOperationException("Server did not return a user identity.");
    // Jellyfin serializes user IDs without dashes, which Guid JSON conversion rejects.
    if (!Guid.TryParse(user.Id, out var userId) || userId == Guid.Empty) throw new InvalidOperationException("A user access token is required; server API keys are not supported.");
    return userId;
}

// Pairs this device with a Jellyfin account through Jellyfin Quick Connect; no password is entered here.
async Task<int> Pair(Uri server, string data)
{
    var deviceId = "retrobridge-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    using var client = CreateClient(server, null, deviceId);
    using (var enabled = await client.GetAsync("QuickConnect/Enabled", ct))
        if (!enabled.IsSuccessStatusCode || !await enabled.Content.ReadFromJsonAsync<bool>(json, ct))
            throw new InvalidOperationException("Quick Connect is disabled on this server. An administrator can enable it under Dashboard → General.");
    using var initiated = await client.PostAsync("QuickConnect/Initiate", null, ct);
    initiated.EnsureSuccessStatusCode();
    var request = await initiated.Content.ReadFromJsonAsync<QuickConnectState>(json, ct) ?? throw new InvalidOperationException("Quick Connect did not start.");
    Console.WriteLine($"Quick Connect code: {request.Code}");
    Console.WriteLine("In Jellyfin, open your profile → Quick Connect and enter this code. Waiting up to 5 minutes…");
    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (true)
    {
        if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Quick Connect timed out. Run pair again.");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        // Jellyfin's Quick Connect API takes the pairing secret as a query parameter.
        var state = await client.GetFromJsonAsync<QuickConnectState>("QuickConnect/Connect?secret=" + Uri.EscapeDataString(request.Secret), json, ct);
        if (state?.Authenticated == true) break;
    }
    using var authenticated = await client.PostAsJsonAsync("Users/AuthenticateWithQuickConnect", new { Secret = request.Secret }, json, ct);
    authenticated.EnsureSuccessStatusCode();
    var result = await authenticated.Content.ReadFromJsonAsync<AuthenticationResult>(json, ct);
    if (string.IsNullOrEmpty(result?.AccessToken)) throw new InvalidOperationException("Server did not return an access token.");
    Pairing.Save(data, new(server.AbsoluteUri, deviceId, result.AccessToken));
    Console.WriteLine($"Paired. Device ID: {deviceId}");
    Console.WriteLine($"Play page: {new Uri(server, "RetroBridge/Play")}  (bookmark it on any phone or computer)");
    Console.WriteLine("Revoke this device at any time in Jellyfin under Dashboard → Devices.");
    return 0;
}

async Task<int> Launch(HttpClient client, Uri server, Guid userId, Game game, string executable, string core, string data)
{
    if (string.IsNullOrEmpty(game.Extension) || Platforms.FromExtension(game.Extension) != game.Platform || game.Size < 0 || game.Size > 64 * 1024 * 1024 ||
        game.Id != game.Platform + "-" + game.Sha256)
        throw new InvalidOperationException("Unsupported or invalid game metadata (prototype limit: 64 MiB).");
    var saveDirectory = LaunchPlan.SaveDirectory(data, server.AbsoluteUri, userId, game);
    Directory.CreateDirectory(saveDirectory);
    var cache = Path.Combine(Path.GetFullPath(data), "cache");
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
    var plan = LaunchPlan.Create(Path.GetFullPath(executable), Path.GetFullPath(core), content, config);
    await SyncSaves(client, game, saveDirectory, beforeLaunch: true);
    Console.WriteLine($"Starting {game.Title}. Saves: {saveDirectory}");
    using var process = Process.Start(plan) ?? throw new InvalidOperationException("RetroArch could not be started.");
    // Keep the game lock until RetroArch exits, even if the CLI receives Ctrl+C.
    await process.WaitForExitAsync();
    Console.WriteLine($"RetroArch exited with code {process.ExitCode}.");
    await SyncSaves(client, game, saveDirectory, beforeLaunch: false);
    return process.ExitCode;
}

// Syncs battery saves with the server. Never blocks play: failures are logged and retried at the next launch,
// because sync.json only advances after the server has confirmed a transfer. Conflicts keep both copies; the
// local save (the one just played) wins and the other is kept beside it as <name>.server-r<revision>.
async Task SyncSaves(HttpClient client, Game game, string saveDirectory, bool beforeLaunch)
{
    var statePath = Path.Combine(saveDirectory, "sync.json");
    try
    {
        var state = File.Exists(statePath) ? JsonSerializer.Deserialize<Dictionary<string, SyncedSave>>(await File.ReadAllTextAsync(statePath, ct), json) ?? [] : [];
        var route = "RetroBridge/Games/" + Uri.EscapeDataString(game.Id) + "/Saves";
        using var listing = await client.GetAsync(route, ct);
        if (listing.StatusCode == HttpStatusCode.NotFound) return; // server without save sync, or game no longer visible
        listing.EnsureSuccessStatusCode();
        var server = (await listing.Content.ReadFromJsonAsync<ServerSave[]>(json, ct) ?? []).ToDictionary(x => x.Name);
        var names = server.Keys.Concat(SaveSync.Extensions.Select(ext => game.Id + ext).Where(n => File.Exists(Path.Combine(saveDirectory, n)))).Distinct();
        foreach (var name in names.Where(n => SaveSync.IsSyncedName(game.Id, n)))
        {
            var local = Path.Combine(saveDirectory, name);
            string? localSha = null;
            if (File.Exists(local))
                await using (var stream = File.OpenRead(local)) localSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            // An empty local file is never authoritative.
            if (localSha is not null && new FileInfo(local).Length == 0) localSha = null;
            server.TryGetValue(name, out var remote);
            state.TryGetValue(name, out var last);
            var action = SaveSync.Decide(localSha, last, remote);
            if (action == SyncAction.Conflict)
            {
                await DownloadSave(client, route, remote!, Path.Combine(saveDirectory, $"{name}.server-r{remote!.Revision}"));
                Console.WriteLine($"Save conflict for {name}: kept this computer's copy; the other device's copy is saved as {name}.server-r{remote.Revision}.");
                state[name] = new(localSha!, remote.Revision);
                action = beforeLaunch ? SyncAction.None : SyncAction.Upload;
            }
            if (action == SyncAction.Download && beforeLaunch)
            {
                await DownloadSave(client, route, remote!, local);
                state[name] = new(remote!.Sha256, remote.Revision);
                Console.WriteLine($"Downloaded {name} (revision {remote.Revision}) from the server.");
            }
            // Pending uploads (for example after playing offline) go out before or after a launch.
            else if (action == SyncAction.Upload)
            {
                var baseRevision = state.TryGetValue(name, out var known) ? known.Revision : 0;
                using var body = new ByteArrayContent(await File.ReadAllBytesAsync(local, ct));
                using var put = await client.PutAsync($"{route}/{Uri.EscapeDataString(name)}?baseRevision={baseRevision}", body, ct);
                if (put.StatusCode == HttpStatusCode.Conflict)
                {
                    Console.WriteLine($"Save {name} changed on the server meanwhile; it will be reconciled at the next launch.");
                    continue;
                }
                put.EnsureSuccessStatusCode();
                var saved = await put.Content.ReadFromJsonAsync<ServerSave>(json, ct) ?? throw new InvalidOperationException("Server returned no save.");
                state[name] = new(saved.Sha256, saved.Revision);
                Console.WriteLine($"Uploaded {name} (revision {saved.Revision}).");
            }
            else if (action == SyncAction.None && localSha is not null && remote is not null) state[name] = new(localSha, remote.Revision);
        }
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, json), ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
    {
        Console.Error.WriteLine($"Save sync skipped ({ex.Message}); your local save is unchanged and will sync next time.");
    }
}

async Task DownloadSave(HttpClient client, string route, ServerSave save, string destination)
{
    using var response = await client.GetAsync($"{route}/{Uri.EscapeDataString(save.Name)}", ct);
    response.EnsureSuccessStatusCode();
    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
    if (bytes.Length > SaveSync.MaxBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != save.Sha256)
        throw new IOException($"Downloaded save {save.Name} failed verification.");
    var temporary = destination + ".download";
    await File.WriteAllBytesAsync(temporary, bytes, ct);
    File.Move(temporary, destination, true);
}

static async Task<bool> Verified(string path, Game game, CancellationToken ct)
{
    if (!File.Exists(path)) return false;
    await using var stream = File.OpenRead(path);
    return stream.Length == game.Size &&
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == game.Sha256;
}

internal sealed record UserIdentity(string? Id);
internal sealed record QuickConnectState(string Secret, string Code, bool Authenticated);
internal sealed record AuthenticationResult(string? AccessToken);

// The device's access token, readable only by the current user (all OSes) in the data directory.
internal sealed record Pairing(string Server, string DeviceId, string Token)
{
    private static string PathIn(string data) => Path.Combine(Path.GetFullPath(data), "pairing.json");

    public static void Save(string data, Pairing pairing) => SecureFile.WriteAllText(PathIn(data), JsonSerializer.Serialize(pairing));

    public static Pairing Load(string data)
    {
        var path = PathIn(data);
        if (!File.Exists(path)) throw new InvalidOperationException("This device is not paired. Run: RetroBridge.Launcher pair <server-url> <data-directory>");
        using var stream = File.OpenRead(path);
        var pairing = JsonSerializer.Deserialize<Pairing>(stream);
        if (pairing is null || string.IsNullOrEmpty(pairing.Token) || !pairing.DeviceId.StartsWith("retrobridge-", StringComparison.Ordinal))
            throw new InvalidOperationException("Pairing file is invalid. Run pair again.");
        return pairing;
    }
}

// Executable and core paths chosen on this computer; launch links can never supply them.
internal sealed record LocalSettings(string RetroArch, Dictionary<string, string> Cores)
{
    private static string PathIn(string data) => Path.Combine(data, "settings.json");

    public static void Save(string data, LocalSettings settings)
    {
        foreach (var path in settings.Cores.Values.Prepend(settings.RetroArch))
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException($"Expected an existing absolute file path: {path}");
        if (settings.Cores.Count == 0) throw new ArgumentException("Configure at least one core, for example --core nes=<path>.");
        Directory.CreateDirectory(data);
        File.WriteAllText(PathIn(data), JsonSerializer.Serialize(settings));
    }

    public static LocalSettings Load(string data)
    {
        if (!File.Exists(PathIn(data))) throw new InvalidOperationException("Launch settings are missing. Run configure --retroarch <path> --core nes=<path>.");
        return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(PathIn(data))) ?? throw new InvalidOperationException("Launch settings are invalid. Run configure again.");
    }
}
