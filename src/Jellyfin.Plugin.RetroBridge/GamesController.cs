using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetroBridge.Domain;

namespace Jellyfin.Plugin.RetroBridge;

[ApiController]
[Route("RetroBridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class GamesController(CatalogService catalog) : ControllerBase
{
    [HttpGet("Games")]
    public ActionResult<IEnumerable<Game>> List([FromQuery] string? search, [FromQuery] string? platform)
        => Ok(catalog.Games.Select(x => x.Game)
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(platform) || x.Platform == platform));

    [HttpPost("Scan")]
    public async Task<IActionResult> Scan(CancellationToken cancellationToken)
    {
        var result = await catalog.ScanAsync(Plugin.Instance!.Configuration.GameRoots.ToArray(), cancellationToken);
        if (result is null) return Conflict(new { Message = "A scan is already running." });
        return Ok(new { Count = result.Games.Count, Published = result.Issues.Count == 0, result.Issues });
    }

    [HttpGet("Games/{id}/Content")]
    public IActionResult ContentFile(string id)
    {
        var item = catalog.Games.FirstOrDefault(x => x.Game.Id == id);
        if (item is null) return NotFound();
        // Revoking a configured root takes effect immediately, before the next scan.
        if (!Plugin.Instance!.Configuration.GameRoots.Any(root => Path.GetFullPath(root) == item.Root)) return NotFound();
        try
        {
            var path = SafeContent.Resolve(item.Root, item.RelativePath);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return File(stream, "application/octet-stream", item.Game.Id + item.Game.Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return NotFound(new { Message = "Content is unavailable; check the game root and rescan." }); }
    }
}
