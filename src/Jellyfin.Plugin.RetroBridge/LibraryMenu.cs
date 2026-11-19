using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Jellyfin.Plugin.RetroBridge;

// Jellyfin Web reads menuLinks from web/config.json for every user. Route that one request through
// Jellyfin's normal base-URL and HTTP pipeline so even read-only web installations get the entry.
public sealed class LibraryMenu(IConfiguration settings) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (settings.HostWebClient())
        {
            app.Use(async (context, proceed) =>
            {
                const string suffix = "/web/config.json";
                var path = context.Request.Path.Value;
                if (path?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true &&
                    (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
                    context.Request.Path = path[..^suffix.Length] + "/RetroBridge/ClientConfig";
                await proceed(context);
            });
        }
        next(app);
    };
}

[ApiController]
public sealed class LibraryMenuController(IApplicationPaths paths, IConfiguration settings) : ControllerBase
{
    [HttpGet("RetroBridge/ClientConfig")]
    [HttpHead("RetroBridge/ClientConfig")]
    [AllowAnonymous]
    public async Task<IActionResult> ClientConfig(CancellationToken cancellationToken)
    {
        if (!settings.HostWebClient()) return NotFound();
        var path = Path.Combine(paths.WebPath, "config.json");
        if (!System.IO.File.Exists(path)) return NotFound();
        await using var file = System.IO.File.OpenRead(path);
        JsonNode? config;
        try { config = await JsonNode.ParseAsync(file, cancellationToken: cancellationToken); }
        catch (JsonException) { return PhysicalFile(path, "application/json"); }
        if (config is not JsonObject web) return PhysicalFile(path, "application/json");
        if (web["menuLinks"] is null) web["menuLinks"] = new JsonArray();
        if (web["menuLinks"] is JsonArray links && !links.OfType<JsonObject>().Any(link => link["url"]?.ToString() == "../RetroBridge/Play"))
            links.Add(new JsonObject { ["name"] = "Retro Games", ["icon"] = "sports_esports", ["url"] = "../RetroBridge/Play" });
        Response.Headers.CacheControl = "no-store";
        return Content(web.ToJsonString(), "application/json");
    }
}
