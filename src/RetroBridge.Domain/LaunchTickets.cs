using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RetroBridge.Domain;

// Short-lived, single-use launch tickets bound to one user, device, and game.
// Only ticket hashes are stored, so a memory dump does not reveal redeemable tickets.
public sealed class LaunchTickets(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    // A listening companion checks in every few seconds; after this long it counts as offline.
    public static readonly TimeSpan ListenerTimeout = TimeSpan.FromSeconds(20);
    private sealed record Grant(Guid User, string Device, string Game, DateTimeOffset Expires, bool Remote);
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _listeners = new();

    public LaunchTickets() : this(TimeProvider.System) { }

    // Remote grants are delivered to the device's listener instead of a link; the caller gets no redeemable ticket.
    public DateTimeOffset IssueRemote(Guid user, string device, string game) => Add(user, device, game, true).Expires;

    public (string Ticket, DateTimeOffset Expires) Issue(Guid user, string device, string game) => Add(user, device, game, false);

    private (string Ticket, DateTimeOffset Expires) Add(Guid user, string device, string game, bool remote)
    {
        if (user == Guid.Empty || string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(game))
            throw new ArgumentException("A user, device, and game are required.");
        var now = time.GetUtcNow();
        foreach (var expired in _grants.Where(x => x.Value.Expires <= now)) _grants.TryRemove(expired);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var grant = new Grant(user, device, game, now + Lifetime, remote);
        _grants[Hash(ticket)] = grant;
        return (ticket, grant.Expires);
    }

    // Any redemption attempt consumes the ticket, so a leaked ticket cannot be retried.
    public string? Redeem(string ticket, Guid user, string device)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128 || !_grants.TryRemove(Hash(ticket), out var grant)) return null;
        return grant.Expires > time.GetUtcNow() && grant.User == user && string.Equals(grant.Device, device, StringComparison.Ordinal)
            ? grant.Game : null;
    }

    // Called by a device's listener with its own credentials: records a check-in and consumes the oldest pending remote launch.
    public string? Claim(Guid user, string device)
    {
        var now = time.GetUtcNow();
        _listeners[(user, device)] = now;
        foreach (var (key, grant) in _grants.Where(x => x.Value.Remote && x.Value.User == user && x.Value.Device == device).OrderBy(x => x.Value.Expires))
            if (_grants.TryRemove(key, out _) && grant.Expires > now) return grant.Game;
        return null;
    }

    public bool IsListening(Guid user, string device) =>
        _listeners.TryGetValue((user, device), out var seen) && time.GetUtcNow() - seen < ListenerTimeout;

    private static string Hash(string ticket) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
