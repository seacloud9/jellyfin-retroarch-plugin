using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record TicketRequest(string? DeviceId);
public sealed record RedeemRequest(string? Ticket);
public sealed record TitleRequest(string? Title);
public sealed record AccessRequest(string[]? PlayerUserIds);

[ApiController]
[Route("RetroBridge")]
[Authorize]
public sealed class GamesController(CatalogService catalog, LaunchTickets tickets, IPairedDevices devices, ArtworkService artwork, SaveStore saves, Func<PluginConfiguration> configuration, Action<PluginConfiguration> saveConfiguration) : ControllerBase
{
    // Claim types set by Jellyfin's authentication handler.
    internal const string UserIdClaim = "Jellyfin-UserId";
    internal const string DeviceIdClaim = "Jellyfin-DeviceId";

    [HttpGet("Games")]
    public ActionResult<IEnumerable<Game>> List([FromQuery] string? search, [FromQuery] string? platform)
    {
        if (!CanPlay()) return Forbid();
        return Ok(Visible().Select(x => Present(x.Game))
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(platform) || x.Platform == platform));
    }

    // Empty or whitespace restores the title derived from the file name.
    [HttpPost("Games/{id}/Title")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<Game> SetTitle(string id, [FromBody] TitleRequest request)
    {
        var game = catalog.Games.FirstOrDefault(x => x.Game.Id == id)?.Game;
        if (game is null) return NotFound();
        var title = request.Title?.Trim() ?? "";
        if (title.Length > 200 || title.Any(char.IsControl)) return BadRequest(new { Message = "Use a title of up to 200 characters without control characters." });
        var config = configuration();
        var overrides = config.TitleOverrides.Where(x => x.GameId != id).ToList();
        if (title.Length > 0) overrides.Add(new TitleOverride { GameId = id, Title = title });
        config.TitleOverrides = overrides.ToArray();
        saveConfiguration(config);
        return Ok(Present(game));
    }

    // An empty or missing list makes the game available to every player again.
    [HttpPost("Games/{id}/Access")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult SetAccess(string id, [FromBody] AccessRequest request)
    {
        if (catalog.Games.All(x => x.Game.Id != id)) return NotFound();
        var players = new List<string>();
        foreach (var value in request.PlayerUserIds ?? [])
            if (Guid.TryParse(value, out var player) && player != Guid.Empty) players.Add(player.ToString("N"));
            else return BadRequest(new { Message = "Player IDs must be Jellyfin user IDs." });
        var config = configuration();
        var restricted = config.RestrictedGames.Where(x => x.GameId != id).ToList();
        if (players.Count > 0) restricted.Add(new GameAccess { GameId = id, PlayerUserIds = [.. players.Distinct()] });
        config.RestrictedGames = [.. restricted];
        saveConfiguration(config);
        return Ok(new { GameId = id, PlayerUserIds = players.Distinct() });
    }

    [HttpGet("Games/{id}/Art")]
    public IActionResult Art(string id)
    {
        if (!CanPlay()) return Forbid();
        if (Visible().All(x => x.Game.Id != id) || artwork.Open(id) is not { } art) return NotFound();
        // Short cache: administrators can replace art at any time.
        Response.Headers.CacheControl = "private, max-age=300";
        return File(art.Image, art.ContentType);
    }

    // Raw PNG or JPEG body. Replaces fetched art; fetches never overwrite an upload.
    [HttpPut("Games/{id}/Art")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(ArtworkService.MaxImageBytes + 1024)]
    public async Task<IActionResult> UploadArt(string id, CancellationToken cancellationToken)
    {
        if (catalog.Games.All(x => x.Game.Id != id)) return NotFound();
        try { await artwork.SaveCustomAsync(id, Request.Body, cancellationToken); }
        catch (InvalidDataException ex) { return BadRequest(new { ex.Message }); }
        // Kestrel enforces RequestSizeLimit while the body is read.
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return StatusCode(StatusCodes.Status413PayloadTooLarge, new { Message = "Images must be 4 MiB or smaller." }); }
        return NoContent();
    }

    [HttpDelete("Games/{id}/Art")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult RemoveArt(string id) =>
        catalog.Games.Any(x => x.Game.Id == id) && artwork.RemoveCustom(id) ? NoContent() : NotFound();

    // Fetches missing art now (202), retrying recent misses. ?wait=true returns the counts.
    [HttpPost("Artwork")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> FetchArtwork([FromQuery] bool wait, CancellationToken cancellationToken)
    {
        if (!configuration().FetchArtwork) return Conflict(new { Message = "Artwork fetching is turned off in the plugin settings." });
        var games = catalog.Games;
        if (!wait)
        {
            _ = Task.Run(() => artwork.FetchMissingAsync(games, true, CancellationToken.None));
            return Accepted();
        }
        return await artwork.FetchMissingAsync(games, true, cancellationToken) is { } result ? Ok(result) : Conflict(new { Message = "Artwork is already being fetched." });
    }

    // Server copies of the caller's battery saves for a game, used by companions to sync between devices.
    [HttpGet("Games/{id}/Saves")]
    public IActionResult ListSaves(string id)
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        return Visible().Any(x => x.Game.Id == id) ? Ok(saves.List(user, id)) : NotFound();
    }

    // Latest save per visible game, with the saving device's name when it is one of the caller's devices.
    [HttpGet("Saves")]
    public IActionResult SaveStatus()
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        var visible = Visible().Select(x => x.Game.Id).ToHashSet(StringComparer.Ordinal);
        var names = devices.ForUser(user).ToDictionary(x => x.Id, x => x.Name);
        return Ok(saves.Latest(user).Where(x => visible.Contains(x.Key)).ToDictionary(x => x.Key, x => new
        {
            x.Value.Revision,
            x.Value.Modified,
            Device = names.GetValueOrDefault(x.Value.Device) ?? "another device",
        }));
    }

    [HttpGet("Games/{id}/Saves/{name}")]
    public IActionResult DownloadSave(string id, string name)
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        if (Visible().All(x => x.Game.Id != id) || saves.Open(user, id, name) is not { } stream) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(stream, "application/octet-stream", name);
    }

    // ?baseRevision is the revision the upload was based on (0 for none); 409 returns the current server copy.
    [HttpPut("Games/{id}/Saves/{name}")]
    [RequestSizeLimit(SaveSync.MaxBytes + 1024)]
    public async Task<IActionResult> UploadSave(string id, string name, [FromQuery] long baseRevision, CancellationToken cancellationToken)
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        if (Visible().All(x => x.Game.Id != id)) return NotFound();
        if (!SaveSync.IsSyncedName(id, name)) return BadRequest(new { Message = "Only battery saves named after the game can be synced." });
        try
        {
            var result = await saves.WriteAsync(user, id, name, Request.Body, baseRevision, User.FindFirst(DeviceIdClaim)?.Value ?? "", cancellationToken);
            return result.Saved is { } saved ? Ok(saved) : Conflict(result.Current);
        }
        catch (InvalidDataException ex) { return BadRequest(new { ex.Message }); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return StatusCode(StatusCodes.Status413PayloadTooLarge, new { Message = "Saves must be 1 MiB or smaller." }); }
    }

    [HttpGet("Devices")]
    public ActionResult<IEnumerable<PairedDevice>> Devices()
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        return Ok(devices.ForUser(user).Select(x => x with { Listening = tickets.IsListening(user, x.Id) }).OrderByDescending(x => x.LastActive));
    }

    // Static player page; it authenticates with the browser's existing Jellyfin session and holds no data itself.
    [HttpGet("Play")]
    [AllowAnonymous]
    public IActionResult Play()
    {
        var stream = typeof(GamesController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.RetroBridge.Configuration.playPage.html")!;
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src blob:; frame-ancestors 'none'";
        Response.Headers.CacheControl = "no-store";
        return File(stream, "text/html; charset=utf-8");
    }

    // Starts a background scan (202). ?wait=true waits for the result instead, for scripts and tests.
    [HttpPost("Scan")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Scan([FromQuery] bool full, [FromQuery] bool wait, CancellationToken cancellationToken)
    {
        var roots = configuration().GameRoots.ToArray();
        if (!wait)
            return catalog.TryStartBackground(roots, full) ? Accepted(ScanStatusBody()) : Conflict(new { Message = "A scan is already running." });
        var result = await catalog.ScanAsync(roots, cancellationToken, full);
        if (result is null) return Conflict(new { Message = "A scan is already running." });
        return Ok(Summary(result));
    }

    [HttpGet("Scan")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ScanStatus() => Ok(ScanStatusBody());

    private object ScanStatusBody()
    {
        var status = catalog.Status;
        return new { status.Running, status.Processed, Last = status.Last is null ? null : Summary(status.Last), status.LastCompleted };
    }

    private static object Summary(ScanResult result) =>
        new { Count = result.Games.Count, Published = result.Issues.Count == 0, result.Hashed, result.Reused, result.Issues };

    // Issues a ticket for the caller's paired companion device; the browser never sees a device credential.
    [HttpPost("Games/{id}/LaunchTicket")]
    public IActionResult LaunchTicket(string id, [FromBody] TicketRequest request)
    {
        if (!CanPlay() || UserId() is not { } user) return Forbid();
        if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length > 256) return BadRequest(new { Message = "Choose a paired device." });
        if (Visible().All(x => x.Game.Id != id)) return NotFound();
        // A listening device receives the launch directly, so any browser (including a phone) can start it.
        if (tickets.IsListening(user, request.DeviceId))
            return Ok(new { Delivered = true, Expires = tickets.IssueRemote(user, request.DeviceId, id) });
        var (ticket, expires) = tickets.Issue(user, request.DeviceId, id);
        return Ok(new { Delivered = false, Ticket = ticket, Expires = expires });
    }

    [HttpPost("LaunchTickets/Redeem")]
    public ActionResult<Game> Redeem([FromBody] RedeemRequest request)
    {
        if (!CanPlay() || UserId() is not { } user || User.FindFirst(DeviceIdClaim)?.Value is not { Length: > 0 } device) return Forbid();
        var id = tickets.Redeem(request.Ticket ?? "", user, device);
        var game = id is null ? null : Visible().FirstOrDefault(x => x.Game.Id == id)?.Game is { } found ? Present(found) : null;
        // One response for expired, replayed, foreign, and unknown tickets.
        return game is null ? NotFound(new { Message = "Launch ticket is invalid or expired. Start the game again." }) : Ok(game);
    }

    // Polled by the companion's listen command with the device's own token.
    [HttpPost("LaunchTickets/Claim")]
    public ActionResult<Game> Claim()
    {
        if (!CanPlay() || UserId() is not { } user || User.FindFirst(DeviceIdClaim)?.Value is not { Length: > 0 } device) return Forbid();
        var id = tickets.Claim(user, device);
        var game = id is null ? null : Visible().FirstOrDefault(x => x.Game.Id == id)?.Game is { } found ? Present(found) : null;
        return game is null ? NoContent() : Ok(game);
    }

    [HttpGet("Games/{id}/Content")]
    public IActionResult ContentFile(string id)
    {
        if (!CanPlay()) return Forbid();
        var item = Visible().FirstOrDefault(x => x.Game.Id == id);
        if (item is null) return NotFound();
        // Revoking a configured root takes effect immediately, before the next scan.
        if (!configuration().GameRoots.Any(root => Path.GetFullPath(root) == item.Root)) return NotFound();
        try
        {
            var path = SafeContent.Resolve(item.Root, item.RelativePath);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return File(stream, "application/octet-stream", item.Game.Id + item.Game.Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return NotFound(new { Message = "Content is unavailable; check the game root and rescan." }); }
    }

    private Game Present(Game game) =>
        (configuration().TitleOverrides.FirstOrDefault(x => x.GameId == game.Id) is { } custom ? game with { Title = custom.Title } : game) with { HasArt = artwork.Has(game.Id) };

    // Games the caller may see. Restricted games are indistinguishable from missing ones for everyone else.
    private IEnumerable<IndexedGame> Visible()
    {
        if (User.IsInRole("Administrator")) return catalog.Games;
        var user = UserId();
        var restricted = configuration().RestrictedGames.ToDictionary(x => x.GameId, x => x.PlayerUserIds);
        return catalog.Games.Where(x => !restricted.TryGetValue(x.Game.Id, out var players) ||
            (user is { } id && players.Any(p => Guid.TryParse(p, out var allowed) && allowed == id)));
    }

    private Guid? UserId() => Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : null;

    private bool CanPlay() => User.IsInRole("Administrator") ||
        (UserId() is { } user && configuration().PlayerUserIds.Any(x => Guid.TryParse(x, out var allowed) && allowed == user));
}
