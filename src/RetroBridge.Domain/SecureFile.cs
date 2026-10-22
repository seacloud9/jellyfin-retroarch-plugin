using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RetroBridge.Domain;

// Writes secrets (device tokens) so that only the current user can read them, on every OS.
public static class SecureFile
{
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream)) writer.Write(contents);
        // Restrict before the secret takes its final name, so it is never readable by others there.
        if (OperatingSystem.IsWindows()) RestrictToCurrentUser(temporary);
        File.Move(temporary, path, true);
    }

    // Removes inherited permissions (which usually grant Users or Administrators access) and allows only this user.
    [SupportedOSPlatform("windows")]
    public static void RestrictToCurrentUser(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    // True when no other account can read the file.
    public static bool IsPrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        return IsPrivateOnWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsPrivateOnWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        return rules.All(rule => rule.AccessControlType == AccessControlType.Deny || rule.IdentityReference.Equals(identity.User));
    }
}
