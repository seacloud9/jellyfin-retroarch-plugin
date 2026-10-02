using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jellyfin.Plugin.RetroBridge;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using RetroBridge.Domain;

namespace RetroBridge.Tests;

// Exercises real MVC routing and authorization with a test identity provider.
// This is not a substitute for testing Jellyfin's authentication/plugin loader.
public sealed class ApiTests : IDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly CatalogService _catalog = new();
    public ApiTests()
    {
        _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(_catalog);
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation, policy => policy.RequireAuthenticatedUser().RequireRole("Administrator")));
            services.AddControllers().AddApplicationPart(typeof(GamesController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).Start();
        _client = _host.GetTestClient();
    }

    [Theory]
    [InlineData("GET", "/RetroBridge/Games")]
    [InlineData("GET", "/RetroBridge/Games/unknown/Content")]
    [InlineData("POST", "/RetroBridge/Scan")]
    public async Task AnonymousRequestsAreRejected(string method, string path)
    {
        using var response = await _client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/RetroBridge/Games")]
    [InlineData("GET", "/RetroBridge/Games/unknown/Content")]
    [InlineData("POST", "/RetroBridge/Scan")]
    public async Task RegularUsersAreRejected(string method, string path)
    {
        _client.DefaultRequestHeaders.Add("Test-Role", "User");
        using var response = await _client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdministratorCanSearchAndFilterWithoutReceivingServerPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrobridge-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Alpha.nes"), "fixture a");
            await File.WriteAllTextAsync(Path.Combine(root, "Beta.gb"), "fixture b");
            await _catalog.ScanAsync([root], default);
            _client.DefaultRequestHeaders.Add("Test-Role", "Administrator");
            var games = await _client.GetFromJsonAsync<Game[]>("/RetroBridge/Games?search=ALPHA&platform=nes");
            Assert.Equal("Alpha", Assert.Single(games!).Title);
            var json = await _client.GetStringAsync("/RetroBridge/Games");
            Assert.DoesNotContain(root, json);
            Assert.DoesNotContain("RelativePath", json);
            Assert.Empty((await _client.GetFromJsonAsync<Game[]>("/RetroBridge/Games?platform=gbc"))!);
        }
        finally { Directory.Delete(root, true); }
    }

    public void Dispose() { _client.Dispose(); _host.Dispose(); }
}

internal sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role.ToString())], "test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
    }
}
