using MediaBrowser.Controller.Devices;

namespace Jellyfin.Plugin.RetroBridge;

public sealed record PairedDevice(string Id, string Name, DateTime? LastActive);

// The caller's companion devices. Never exposes access tokens.
public interface IPairedDevices
{
    IEnumerable<PairedDevice> ForUser(Guid user);
}

public sealed class JellyfinPairedDevices(IDeviceManager devices) : IPairedDevices
{
    public IEnumerable<PairedDevice> ForUser(Guid user) => devices.GetDevicesForUser(user).Items
        .Where(x => x.Id is not null && x.Id.StartsWith("retrobridge-", StringComparison.Ordinal) && x.LastUserId == user)
        .Select(x => new PairedDevice(x.Id!, x.CustomName ?? x.Name ?? x.Id!, x.DateLastActivity));
}
