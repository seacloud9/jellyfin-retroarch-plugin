using System.Diagnostics;
using System.Runtime.Versioning;
using RetroBridge.Domain;

// Registers this companion as the per-user handler for retrobridge:// links. Nothing is installed system-wide.
internal static class LinkHandler
{
    private const string MacBundleId = "org.retrobridge.launcher";
    private const string LsRegister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    internal static string AppHost
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RetroBridge.Launcher.exe" : "RetroBridge.Launcher");
            return File.Exists(path) ? path : throw new InvalidOperationException("The launcher executable was not found next to RetroBridge.Launcher.dll.");
        }
    }

    // Framework-dependent builds need DOTNET_ROOT because links and login items start without the user's shell environment.
    // shared/Microsoft.NETCore.App/<version>/ is three levels below the .NET root. Self-contained builds need nothing (null).
    internal static string? DotnetRoot
    {
        get
        {
            var runtime = Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            // The runtime lives next to the app in a self-contained build.
            if (runtime.Length == 0 || runtime == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) return null;
            return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        }
    }

    internal static IEnumerable<string> RegisteredPaths(string data) => DotnetRoot is { } root ? [data, AppHost, root] : [data, AppHost];

    public static string Register(string data)
    {
        // Registration embeds these paths in generated files; reject characters that would need quoting rules.
        char[] unsupported = OperatingSystem.IsWindows() ? ['"', '%', '\n', '\r'] : ['"', '\\', '\'', '`', '$', '%', '\n', '\r'];
        foreach (var value in RegisteredPaths(data))
            if (value.IndexOfAny(unsupported) >= 0)
                throw new ArgumentException($"Path contains characters not supported for link registration: {value}");
        if (OperatingSystem.IsWindows()) return RegisterWindows(data);
        if (OperatingSystem.IsMacOS()) return RegisterMac(data);
        if (OperatingSystem.IsLinux()) return RegisterLinux(data);
        throw new PlatformNotSupportedException("Link registration supports Windows, macOS, and Linux.");
    }

    public static string Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LaunchLink.Scheme, false);
            return "Removed the retrobridge:// handler for this user.";
        }
        if (OperatingSystem.IsMacOS())
        {
            var app = MacApp;
            if (Directory.Exists(app)) { Run(LsRegister, "-u", app); Directory.Delete(app, true); }
            return "Removed " + app;
        }
        var desktop = LinuxDesktopFile;
        if (File.Exists(desktop)) File.Delete(desktop);
        TryRun("update-desktop-database", Path.GetDirectoryName(desktop)!);
        return "Removed " + desktop;
    }

    [SupportedOSPlatform("windows")]
    private static string RegisterWindows(string data)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + LaunchLink.Scheme);
        key.SetValue("", "URL:RetroBridge launch link");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{AppHost}\" open \"%1\" --data \"{data}\"");
        return @"Registered retrobridge:// for this user (HKCU\Software\Classes\retrobridge).";
    }

    private static string MacApp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "RetroBridge Launcher.app");

    // An AppleScript applet receives the URL as an Apple Event and passes it on as a single quoted argument.
    private static string RegisterMac(string data)
    {
        var app = MacApp;
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        if (Directory.Exists(app)) Directory.Delete(app, true);
        var script = Path.Combine(Path.GetTempPath(), "retrobridge-handler-" + Guid.NewGuid().ToString("N") + ".applescript");
        File.WriteAllText(script, $"""
            on open location theURL
                do shell script {(DotnetRoot is { } root ? $"\"DOTNET_ROOT=\" & quoted form of \"{root}\" & \" \" & " : "")}quoted form of "{AppHost}" & " open " & quoted form of theURL & " --data " & quoted form of "{data}" & " > /dev/null 2>&1 &"
            end open location
            on run
                display dialog "RetroBridge Launcher opens retrobridge:// links from the Jellyfin play page." buttons {"OK"} default button 1
            end run
            """);
        try { Run("/usr/bin/osacompile", "-o", app, script); }
        finally { File.Delete(script); }
        var plist = Path.Combine(app, "Contents", "Info.plist");
        Run("/usr/bin/plutil", "-replace", "CFBundleIdentifier", "-string", MacBundleId, plist);
        Run("/usr/bin/plutil", "-replace", "LSUIElement", "-bool", "YES", plist);
        Run("/usr/bin/plutil", "-replace", "CFBundleURLTypes", "-json",
            $$"""[{"CFBundleURLName":"RetroBridge launch link","CFBundleURLSchemes":["{{LaunchLink.Scheme}}"]}]""", plist);
        // Editing Info.plist invalidates osacompile's ad-hoc signature.
        Run("/usr/bin/codesign", "--force", "--sign", "-", app);
        Run(LsRegister, "-f", app);
        return "Registered retrobridge:// via " + app;
    }

    private static string LinuxDesktopFile
    {
        get
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome)) dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(dataHome, "applications", "retrobridge-launcher.desktop");
        }
    }

    private static string RegisterLinux(string data)
    {
        var desktop = LinuxDesktopFile;
        Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
        File.WriteAllText(desktop, $"""
            [Desktop Entry]
            Type=Application
            Name=RetroBridge Launcher
            Exec={(DotnetRoot is { } root ? $"env \"DOTNET_ROOT={root}\" " : "")}"{AppHost}" open %u --data "{data}"
            MimeType=x-scheme-handler/{LaunchLink.Scheme};
            NoDisplay=true
            Terminal=false

            """);
        var defaulted = TryRun("xdg-mime", "default", Path.GetFileName(desktop), "x-scheme-handler/" + LaunchLink.Scheme);
        TryRun("update-desktop-database", Path.GetDirectoryName(desktop)!);
        return "Registered retrobridge:// via " + desktop + (defaulted ? "" : " (xdg-mime not found; set it as the default handler for x-scheme-handler/retrobridge)");
    }

    internal static void Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not run {file}.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {error.Trim()}");
    }

    internal static bool TryRun(string file, params string[] arguments)
    {
        try { Run(file, arguments); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
