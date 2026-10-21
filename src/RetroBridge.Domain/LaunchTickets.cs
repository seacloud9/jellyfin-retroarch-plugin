using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RetroBridge.Domain;

// Short-lived, single-use launch tickets bound to one user, device, and game.
// Only ticket hashes are stored, so a memory dump does not reveal redeemable tickets.
public sealed class LaunchTickets(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private sealed record Grant(Guid User, string Device, string Game, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);

    public LaunchTickets() : this(TimeProvider.System) { }

    public (string Ticket, DateTimeOffset Expires) Issue(Guid user, string device, string game)
    {
        if (user == Guid.Empty || string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(game))
            throw new ArgumentException("A user, device, and game are required.");
        var now = time.GetUtcNow();
        foreach (var expired in _grants.Where(x => x.Value.Expires <= now)) _grants.TryRemove(expired);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var grant = new Grant(user, device, game, now + Lifetime);
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

    private static string Hash(string ticket) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
