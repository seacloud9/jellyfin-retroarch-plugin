namespace RetroBridge.Domain;

// retrobridge://launch?ticket=<opaque>. The link carries only a single-use ticket: the server comes from
// the local pairing, and the executable and cores from local settings, so a crafted link cannot choose them.
public static class LaunchLink
{
    public const string Scheme = "retrobridge";

    public static string Create(string ticket) =>
        IsTicket(ticket) ? $"{Scheme}://launch?ticket={ticket}" : throw new ArgumentException("Invalid ticket.");

    public static string? ParseTicket(string link)
    {
        if (link.Length > 512 || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Scheme || uri.Host != "launch" || uri.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var query = uri.Query.TrimStart('?');
        if (!query.StartsWith("ticket=", StringComparison.Ordinal)) return null;
        var ticket = query["ticket=".Length..];
        return IsTicket(ticket) ? ticket : null;
    }

    // Tickets are base64url without padding; anything else (including '&' or '%') is rejected.
    private static bool IsTicket(string value) =>
        value.Length is >= 20 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
