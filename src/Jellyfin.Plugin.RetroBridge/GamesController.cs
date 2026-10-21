using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record TicketRequest(string? DeviceId);
public sealed record RedeemRequest(string? Ticket);
public sealed record TitleRequest(string? Title);

[ApiController]
[Route("RetroBridge")]
[Authorize]
public sealed class GamesController(CatalogService catalog, LaunchTickets tickets, IPairedDevices devices, Func<PluginConfiguration> configuration, Action<PluginConfiguration> saveConfiguration) : ControllerBase
{
    // Claim types set by Jellyfin's authentication handler.
    internal const string UserIdClaim = "Jellyfin-UserId";
    internal const string DeviceIdClaim = "Jellyfin-DeviceId";

    [HttpGet("Games")]
    public ActionResult<IEnumerable<Game>> List([FromQuery] string? search, [FromQuery] string? platform)
    {
        if (!CanPlay()) return Forbid();
        return Ok(catalog.Games.Select(x => Present(x.Game))
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
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
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
        if (catalog.Games.All(x => x.Game.Id != id)) return NotFound();
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
        var game = id is null ? null : catalog.Games.FirstOrDefault(x => x.Game.Id == id)?.Game is { } found ? Present(found) : null;
        // One response for expired, replayed, foreign, and unknown tickets.
        return game is null ? NotFound(new { Message = "Launch ticket is invalid or expired. Start the game again." }) : Ok(game);
    }

    // Polled by the companion's listen command with the device's own token.
    [HttpPost("LaunchTickets/Claim")]
    public ActionResult<Game> Claim()
    {
        if (!CanPlay() || UserId() is not { } user || User.FindFirst(DeviceIdClaim)?.Value is not { Length: > 0 } device) return Forbid();
        var id = tickets.Claim(user, device);
        var game = id is null ? null : catalog.Games.FirstOrDefault(x => x.Game.Id == id)?.Game is { } found ? Present(found) : null;
        return game is null ? NoContent() : Ok(game);
    }

    [HttpGet("Games/{id}/Content")]
    public IActionResult ContentFile(string id)
    {
        if (!CanPlay()) return Forbid();
        var item = catalog.Games.FirstOrDefault(x => x.Game.Id == id);
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
        configuration().TitleOverrides.FirstOrDefault(x => x.GameId == game.Id) is { } custom ? game with { Title = custom.Title } : game;

    private Guid? UserId() => Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : null;

    private bool CanPlay() => User.IsInRole("Administrator") ||
        (UserId() is { } user && configuration().PlayerUserIds.Any(x => Guid.TryParse(x, out var allowed) && allowed == user));
}
