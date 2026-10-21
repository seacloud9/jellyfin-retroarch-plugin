namespace RetroBridge.Domain;

// What the companion knew about a save after its last successful sync.
public sealed record SyncedSave(string Sha256, long Revision);

// The server's current copy of a save.
public sealed record ServerSave(string Name, string Sha256, long Size, long Revision, DateTimeOffset Modified, string Device);

public enum SyncAction
{
    None,
    // Server is newer and the local file has not changed since the last sync.
    Download,
    // Local file changed and the server has not moved since the last sync.
    Upload,
    // Both changed. The caller keeps both copies; the most recently played one wins.
    Conflict,
}

public static class SaveSync
{
    // Battery saves only: save states are core-specific and large.
    public static readonly string[] Extensions = [".srm", ".sav", ".rtc"];
    public const long MaxBytes = 1024 * 1024;

    public static bool IsSyncedName(string gameId, string name) =>
        name.Length <= 200 && Extensions.Any(ext => name == gameId + ext);

    public static SyncAction Decide(string? localSha, SyncedSave? last, ServerSave? server)
    {
        if (localSha is null && server is null) return SyncAction.None;
        if (localSha is null) return SyncAction.Download;
        if (server is null) return SyncAction.Upload;
        if (localSha == server.Sha256) return SyncAction.None;
        var localChanged = last is null || last.Sha256 != localSha;
        var serverChanged = last is null || last.Revision != server.Revision;
        return (localChanged, serverChanged) switch
        {
            (false, true) => SyncAction.Download,
            (true, false) => SyncAction.Upload,
            (true, true) => SyncAction.Conflict,
            // Unchanged on both sides yet different: the record is stale; treat it as a conflict and keep both.
            _ => SyncAction.Conflict,
        };
    }
}
