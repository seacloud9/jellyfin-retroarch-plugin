using MediaBrowser.Controller.Devices;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record PairedDevice(string Id, string Name, DateTime? LastActive, bool Listening = false);

// The caller's companion devices. Never exposes access tokens.
public interface IPairedDevices
{
    IEnumerable<PairedDevice> ForUser(Guid user);
}

public sealed class JellyfinPairedDevices(IDeviceManager devices) : IPairedDevices
{
    private const string Prefix = "retrobridge-";

    // Desktop and Android pairing both generate a random 16-byte hex suffix.
    internal static bool IsCompanionId(string id) => id.Length == Prefix.Length + 32 &&
        id.StartsWith(Prefix, StringComparison.Ordinal) && id.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public IEnumerable<PairedDevice> ForUser(Guid user) => devices.GetDevicesForUser(user).Items
        .Where(x => x.Id is not null && IsCompanionId(x.Id) && x.LastUserId == user)
        .Select(x => new PairedDevice(x.Id!, x.CustomName ?? x.Name ?? x.Id!, x.DateLastActivity));
}
