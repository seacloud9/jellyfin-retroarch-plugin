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
    private readonly PluginConfiguration _configuration = new();
    private readonly TestTime _time = new();
    private int _saves;
    private static readonly Guid Player = Guid.NewGuid();
    public ApiTests()
    {
        _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(_catalog);
            services.AddSingleton(new LaunchTickets(_time));
            services.AddSingleton<IPairedDevices>(new TestDevices());
            services.AddSingleton<Func<PluginConfiguration>>(() => _configuration);
            services.AddSingleton<Action<PluginConfiguration>>(_ => _saves++);
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

    private async Task<(string Root, Game Game)> ScanFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrobridge-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Alpha.nes"), "fixture a");
        _configuration.GameRoots = [root];
        await _catalog.ScanAsync([root], default);
        return (root, Assert.Single(_catalog.Games).Game);
    }

    private HttpRequestMessage As(HttpMethod method, string path, string role, Guid? user, string? device, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Test-Role", role);
        if (user is { } id) request.Headers.Add("Test-User", id.ToString("N"));
        if (device is not null) request.Headers.Add("Test-Device", device);
        return request;
    }

    [Fact]
    public async Task OnlyAllowedPlayersCanListAndDownload()
    {
        var (root, game) = await ScanFixture();
        try
        {
            using (var denied = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Games", "User", Player, "d")))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            _configuration.PlayerUserIds = [Player.ToString("N")];
            using (var listed = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Games", "User", Player, "d")))
                Assert.Equal(game.Id, Assert.Single((await listed.Content.ReadFromJsonAsync<Game[]>())!).Id);
            using (var content = await _client.SendAsync(As(HttpMethod.Get, $"/RetroBridge/Games/{game.Id}/Content", "User", Player, "d")))
                Assert.Equal("fixture a", await content.Content.ReadAsStringAsync());
            using (var scan = await _client.SendAsync(As(HttpMethod.Post, "/RetroBridge/Scan", "User", Player, "d")))
                Assert.Equal(HttpStatusCode.Forbidden, scan.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TicketsAreBoundToUserDeviceAndGameAndSingleUse()
    {
        var (root, game) = await ScanFixture();
        var other = Guid.NewGuid();
        _configuration.PlayerUserIds = [Player.ToString("N"), other.ToString("N")];
        try
        {
            async Task<string> Issue()
            {
                using var response = await _client.SendAsync(As(HttpMethod.Post, $"/RetroBridge/Games/{game.Id}/LaunchTicket", "User", Player, "browser", new { DeviceId = "companion" }));
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("ticket").GetString()!;
            }
            async Task<HttpStatusCode> Redeem(string ticket, Guid user, string device)
            {
                using var response = await _client.SendAsync(As(HttpMethod.Post, "/RetroBridge/LaunchTickets/Redeem", "User", user, device, new { Ticket = ticket }));
                if (response.IsSuccessStatusCode) Assert.Equal(game.Id, (await response.Content.ReadFromJsonAsync<Game>())!.Id);
                return response.StatusCode;
            }

            var ticket = await Issue();
            Assert.Equal(HttpStatusCode.OK, await Redeem(ticket, Player, "companion"));
            Assert.Equal(HttpStatusCode.NotFound, await Redeem(ticket, Player, "companion"));
            Assert.Equal(HttpStatusCode.NotFound, await Redeem(await Issue(), Player, "another-device"));
            Assert.Equal(HttpStatusCode.NotFound, await Redeem(await Issue(), other, "companion"));
            var expiring = await Issue();
            _time.Advance(LaunchTickets.Lifetime);
            Assert.Equal(HttpStatusCode.NotFound, await Redeem(expiring, Player, "companion"));
            Assert.Equal(HttpStatusCode.NotFound, await Redeem("not-a-ticket", Player, "companion"));

            using var unknownGame = await _client.SendAsync(As(HttpMethod.Post, "/RetroBridge/Games/nes-unknown/LaunchTicket", "User", Player, "browser", new { DeviceId = "companion" }));
            Assert.Equal(HttpStatusCode.NotFound, unknownGame.StatusCode);
            _configuration.PlayerUserIds = [];
            using var revoked = await _client.SendAsync(As(HttpMethod.Post, $"/RetroBridge/Games/{game.Id}/LaunchTicket", "User", Player, "browser", new { DeviceId = "companion" }));
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PlayersSeeOnlyTheirOwnPairedDevices()
    {
        _configuration.PlayerUserIds = [Player.ToString("N")];
        using var response = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Devices", "User", Player, "browser"));
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("retrobridge-mine", json);
        Assert.DoesNotContain("retrobridge-theirs", json);
        Assert.DoesNotContain("Token", json);
        using var denied = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Devices", "User", Guid.NewGuid(), "browser"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task PlayPageIsStaticAndLockedDown()
    {
        using var response = await _client.GetAsync("/RetroBridge/Play");
        response.EnsureSuccessStatusCode();
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("retrobridge://launch?ticket=", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListeningDevicesReceiveLaunchesWithoutATicketInTheBrowser()
    {
        var (root, game) = await ScanFixture();
        _configuration.PlayerUserIds = [Player.ToString("N")];
        try
        {
            async Task<System.Text.Json.JsonElement> Request()
            {
                using var response = await _client.SendAsync(As(HttpMethod.Post, $"/RetroBridge/Games/{game.Id}/LaunchTicket", "User", Player, "phone", new { DeviceId = "retrobridge-mine" }));
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            }
            async Task<HttpResponseMessage> Claim(string device) => await _client.SendAsync(As(HttpMethod.Post, "/RetroBridge/LaunchTickets/Claim", "User", Player, device));

            Assert.False((await Request()).GetProperty("delivered").GetBoolean());
            using (var idle = await Claim("retrobridge-mine")) Assert.Equal(HttpStatusCode.NoContent, idle.StatusCode);
            using (var devices = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Devices", "User", Player, "phone")))
                Assert.Contains("\"listening\":true", await devices.Content.ReadAsStringAsync());

            var delivered = await Request();
            Assert.True(delivered.GetProperty("delivered").GetBoolean());
            Assert.False(delivered.TryGetProperty("ticket", out _));
            using (var other = await Claim("retrobridge-other")) Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
            using (var claimed = await Claim("retrobridge-mine")) Assert.Equal(game.Id, (await claimed.Content.ReadFromJsonAsync<Game>())!.Id);
            using (var again = await Claim("retrobridge-mine")) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

            Assert.True((await Request()).GetProperty("delivered").GetBoolean());
            _time.Advance(LaunchTickets.Lifetime);
            using (var expired = await Claim("retrobridge-mine")) Assert.Equal(HttpStatusCode.NoContent, expired.StatusCode);
            _time.Advance(LaunchTickets.ListenerTimeout);
            Assert.False((await Request()).GetProperty("delivered").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScansRunInTheBackgroundAndReportStatus()
    {
        var (root, game) = await ScanFixture();
        try
        {
            using var started = await _client.SendAsync(As(HttpMethod.Post, "/RetroBridge/Scan", "Administrator", Guid.NewGuid(), "d"));
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            System.Text.Json.JsonElement status;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                await Task.Delay(50);
                using var poll = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Scan", "Administrator", Guid.NewGuid(), "d"));
                status = await poll.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            } while ((status.GetProperty("running").GetBoolean() || status.GetProperty("last").ValueKind == System.Text.Json.JsonValueKind.Null) && DateTime.UtcNow < deadline);
            Assert.False(status.GetProperty("running").GetBoolean());
            Assert.Equal(1, status.GetProperty("last").GetProperty("reused").GetInt32());
            Assert.Equal(game.Id, Assert.Single(_catalog.Games).Game.Id);
            using var denied = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Scan", "User", Player, "d"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AdministratorsCanOverrideTitlesForEveryone()
    {
        var (root, game) = await ScanFixture();
        _configuration.PlayerUserIds = [Player.ToString("N")];
        try
        {
            async Task<HttpResponseMessage> Rename(string role, object body) =>
                await _client.SendAsync(As(HttpMethod.Post, $"/RetroBridge/Games/{game.Id}/Title", role, Player, "d", body));
            using (var denied = await Rename("User", new { Title = "Hacked" })) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var invalid = await Rename("Administrator", new { Title = "bad\ntitle" })) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var renamed = await Rename("Administrator", new { Title = "  Alpha Quest  " })) Assert.Equal("Alpha Quest", (await renamed.Content.ReadFromJsonAsync<Game>())!.Title);
            Assert.Equal(1, _saves);
            using (var listed = await _client.SendAsync(As(HttpMethod.Get, "/RetroBridge/Games?search=quest", "User", Player, "d")))
                Assert.Equal("Alpha Quest", Assert.Single((await listed.Content.ReadFromJsonAsync<Game[]>())!).Title);
            using (var reset = await Rename("Administrator", new { Title = " " })) Assert.Equal("Alpha", (await reset.Content.ReadFromJsonAsync<Game>())!.Title);
            Assert.Empty(_configuration.TitleOverrides);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestDevices : IPairedDevices
    {
        public IEnumerable<PairedDevice> ForUser(Guid user) =>
            user == Player ? [new("retrobridge-mine", "Laptop", null)] : [new("retrobridge-theirs", "Other", null)];
    }

    public void Dispose() { _client.Dispose(); _host.Dispose(); }
}

internal sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()) };
        if (Request.Headers.TryGetValue("Test-User", out var user)) claims.Add(new("Jellyfin-UserId", user.ToString()));
        if (Request.Headers.TryGetValue("Test-Device", out var device)) claims.Add(new("Jellyfin-DeviceId", device.ToString()));
        var identity = new ClaimsIdentity(claims, "test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
    }
}

internal sealed class TestTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
