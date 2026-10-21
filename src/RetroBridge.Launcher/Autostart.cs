using System.Runtime.Versioning;
using System.Security;

// Starts `listen --background` at login for the current user only, using each OS's standard mechanism.
internal static class Autostart
{
    private const string Label = "org.retrobridge.listener";
    private const string WindowsValue = "RetroBridge Listener";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacAgent => Path.Combine(Home, "Library", "LaunchAgents", Label + ".plist");
    private static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");
    private static string SystemdUnit => Path.Combine(ConfigHome, "systemd", "user", "retrobridge-listener.service");
    private static string XdgAutostart => Path.Combine(ConfigHome, "autostart", "retrobridge-listener.desktop");

    public static string Enable(string data)
    {
        char[] unsupported = OperatingSystem.IsWindows() ? ['"', '%', '\n', '\r'] : ['"', '\\', '\'', '`', '$', '%', '\n', '\r', '<', '>', '&'];
        foreach (var value in new[] { data, LinkHandler.AppHost, LinkHandler.DotnetRoot })
            if (value.IndexOfAny(unsupported) >= 0) throw new ArgumentException($"Path contains characters not supported for autostart: {value}");
        if (OperatingSystem.IsWindows()) return EnableWindows(data);
        if (OperatingSystem.IsMacOS()) return EnableMac(data);
        if (OperatingSystem.IsLinux()) return EnableLinux(data);
        throw new PlatformNotSupportedException("Autostart supports Windows, macOS, and Linux.");
    }

    public static string Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(WindowsValue, false);
            return "Autostart disabled. A listener that is already running keeps running until you sign out.";
        }
        if (OperatingSystem.IsMacOS())
        {
            LinkHandler.TryRun("/bin/launchctl", "bootout", $"gui/{GetUid()}/{Label}");
            File.Delete(MacAgent);
            return "Autostart disabled and the listener stopped.";
        }
        if (File.Exists(SystemdUnit))
        {
            LinkHandler.TryRun("systemctl", "--user", "disable", "--now", "retrobridge-listener.service");
            File.Delete(SystemdUnit);
            LinkHandler.TryRun("systemctl", "--user", "daemon-reload");
        }
        File.Delete(XdgAutostart);
        return "Autostart disabled.";
    }

    public static string Status()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(WindowsValue) is string command ? "Autostart is on: " + command : "Autostart is off.";
        }
        if (OperatingSystem.IsMacOS())
            return !File.Exists(MacAgent) ? "Autostart is off." :
                "Autostart is on (" + MacAgent + ")" + (LinkHandler.TryRun("/bin/launchctl", "print", $"gui/{GetUid()}/{Label}") ? "; listener loaded." : "; listener not loaded.");
        if (File.Exists(SystemdUnit))
            return "Autostart is on (systemd user service)" + (LinkHandler.TryRun("systemctl", "--user", "is-active", "--quiet", "retrobridge-listener.service") ? "; listener running." : "; listener not running.");
        return File.Exists(XdgAutostart) ? "Autostart is on (" + XdgAutostart + "); it starts at your next desktop login." : "Autostart is off.";
    }

    [SupportedOSPlatform("windows")]
    private static string EnableWindows(string data)
    {
        // conhost --headless runs the console listener without a visible window (Windows 10 21H2+ / 11).
        var command = $"conhost.exe --headless \"{LinkHandler.AppHost}\" listen --background --data \"{data}\"";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(WindowsValue, command);
        return "Autostart enabled; the listener starts at your next sign-in. Start it now with: RetroBridge.Launcher listen";
    }

    private static string EnableMac(string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MacAgent)!);
        static string X(string value) => SecurityElement.Escape(value);
        // KeepAlive restarts only after a crash; permanent stops (unpaired, access removed) exit 0.
        File.WriteAllText(MacAgent, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{X(LinkHandler.AppHost)}</string><string>listen</string><string>--background</string><string>--data</string><string>{X(data)}</string>
              </array>
              <key>EnvironmentVariables</key><dict><key>DOTNET_ROOT</key><string>{X(LinkHandler.DotnetRoot)}</string></dict>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
              <key>ThrottleInterval</key><integer>30</integer>
              <key>ProcessType</key><string>Background</string>
            </dict>
            </plist>

            """);
        LinkHandler.TryRun("/bin/launchctl", "bootout", $"gui/{GetUid()}/{Label}");
        LinkHandler.Run("/bin/launchctl", "bootstrap", $"gui/{GetUid()}", MacAgent);
        return "Autostart enabled and the listener started (" + MacAgent + "). Log: " + Path.Combine(data, "listener.log");
    }

    private static string EnableLinux(string data)
    {
        var exec = $"\"{LinkHandler.AppHost}\" listen --background --data \"{data}\"";
        Directory.CreateDirectory(Path.GetDirectoryName(SystemdUnit)!);
        File.WriteAllText(SystemdUnit, $"""
            [Unit]
            Description=RetroBridge listener (starts games sent from the Jellyfin play page)
            After=network-online.target

            [Service]
            Environment="DOTNET_ROOT={LinkHandler.DotnetRoot}"
            ExecStart={exec}
            Restart=on-failure
            RestartSec=30

            [Install]
            WantedBy=default.target

            """);
        if (LinkHandler.TryRun("systemctl", "--user", "daemon-reload") && LinkHandler.TryRun("systemctl", "--user", "enable", "--now", "retrobridge-listener.service"))
            return "Autostart enabled and the listener started (systemd user service retrobridge-listener). Log: " + Path.Combine(data, "listener.log");
        // No systemd user session (some desktops, containers): fall back to XDG desktop autostart.
        File.Delete(SystemdUnit);
        Directory.CreateDirectory(Path.GetDirectoryName(XdgAutostart)!);
        File.WriteAllText(XdgAutostart, $"""
            [Desktop Entry]
            Type=Application
            Name=RetroBridge Listener
            Exec=env "DOTNET_ROOT={LinkHandler.DotnetRoot}" {exec}
            NoDisplay=true
            X-GNOME-Autostart-enabled=true

            """);
        return "Autostart enabled via " + XdgAutostart + "; the listener starts at your next desktop login. Start it now with: RetroBridge.Launcher listen";
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
