using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Vellichor.Dat;

/// <summary>
/// Finds a retail FINAL FANTASY XI installation (the ROM DAT archives) on the user's own machine and
/// persists the chosen path. NO game data ships with this project — this only *locates* the install the
/// user already owns (README posture). Pure C# (no Godot dependency) so the client and the DAT viewer
/// share one detection + settings mechanism.
///
/// Resolution order: 1) explicitly configured path (settings file), 2) OS auto-detect, 3) an optional
/// dev fallback (e.g. a repo-local <c>corpus/</c> symlink). If none resolve, the caller should prompt.
/// </summary>
public static class InstallLocator
{
    /// A directory is a usable install root when the base ROM index is present. Retail ships the base
    /// tables at the root (VTABLE.DAT) with files under ROM/&lt;dir&gt;/&lt;file&gt;.DAT; some layouts put
    /// them inside ROM/. Accept either, and fall back to the presence of ROM/0/0.DAT.
    public static bool IsValidInstall(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "VTABLE.DAT"))
            || File.Exists(Path.Combine(dir, "ROM", "VTABLE.DAT"))
            || File.Exists(Path.Combine(dir, "ROM", "0", "0.DAT"));
    }

    /// Per-user settings file: &lt;AppData&gt;/Vellichor/settings.ini (plain key=value, no dependencies).
    public static string SettingsPath
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "Vellichor", "settings.ini");
        }
    }

    private const string InstallKey = "InstallPath";

    /// The install path saved by a previous run / the settings UI, or null if unset/invalid.
    public static string? LoadConfiguredPath()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            foreach (var raw in File.ReadAllLines(SettingsPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var i = line.IndexOf('=');
                if (line[..i].Trim().Equals(InstallKey, StringComparison.OrdinalIgnoreCase))
                    return line[(i + 1)..].Trim();
            }
        }
        catch { /* unreadable settings -> treat as unset */ }
        return null;
    }

    /// Persist the chosen install path (creates the settings dir). Silently no-ops on IO failure.
    public static void SaveConfiguredPath(string dir)
    {
        try
        {
            var lines = new List<string>();
            if (File.Exists(SettingsPath))
                lines.AddRange(File.ReadAllLines(SettingsPath)
                    .Where(l => !l.TrimStart().StartsWith(InstallKey + "=", StringComparison.OrdinalIgnoreCase)));
            lines.Insert(0, $"{InstallKey}={dir}");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath, lines);
        }
        catch { /* best effort */ }
    }

    /// Resolve the install: configured path, then auto-detect, then an optional dev fallback. Null = prompt.
    public static string? Resolve(string? devFallback = null)
    {
        var configured = LoadConfiguredPath();
        if (IsValidInstall(configured)) return configured;
        var detected = Detect();
        if (IsValidInstall(detected)) return detected;
        return IsValidInstall(devFallback) ? devFallback : null;
    }

    /// Best-effort OS auto-detect; returns the first valid install root found, or null.
    public static string? Detect() => CandidatePaths().FirstOrDefault(IsValidInstall);

    private static IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in new[]
            {
                @"C:\Program Files (x86)\PlayOnline\SquareEnix\FINAL FANTASY XI",
                @"C:\Program Files\PlayOnline\SquareEnix\FINAL FANTASY XI",
                @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XI",
                @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XI",
                @"C:\PlayOnline\SquareEnix\FINAL FANTASY XI",
            }) yield return p;
            foreach (var r in RegistryCandidates()) yield return r;
        }
        else
        {
            // FFXI is Windows-only, so on macOS/Linux it's typically a mounted Windows volume.
            string[] relatives =
            {
                "Program Files (x86)/PlayOnline/SquareEnix/FINAL FANTASY XI",
                "Program Files/PlayOnline/SquareEnix/FINAL FANTASY XI",
                "PlayOnline/SquareEnix/FINAL FANTASY XI",
            };
            foreach (var vol in MountedVolumes())
                foreach (var rel in relatives)
                    yield return Path.Combine(vol, rel);
        }
    }

    private static IEnumerable<string> MountedVolumes()
    {
        var roots = new List<string> { "/Volumes", "/mnt" };
        string media = "/media/" + Environment.UserName;
        if (Directory.Exists(media)) roots.Add(media);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> vols;
            try { vols = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var v in vols) yield return v;
        }
    }

    /// FFXI records its ROM folder in the PlayOnline registry key. Query it with `reg.exe` so the library
    /// stays cross-platform (no Microsoft.Win32.Registry package / net8.0-windows TFM needed).
    private static IEnumerable<string> RegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] keys =
        {
            @"HKLM\SOFTWARE\WOW6432Node\PlayOnlineUS\InstallFolder",
            @"HKLM\SOFTWARE\WOW6432Node\PlayOnline\InstallFolder",
            @"HKLM\SOFTWARE\WOW6432Node\PlayOnlineEU\InstallFolder",
            @"HKLM\SOFTWARE\PlayOnlineUS\InstallFolder",
            @"HKLM\SOFTWARE\PlayOnline\InstallFolder",
        };
        foreach (var key in keys)
        {
            string? outp = null;
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("reg", $"query \"{key}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (proc is null) continue;
                outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
            }
            catch { continue; }
            if (string.IsNullOrEmpty(outp)) continue;
            // Values 0001/1000 hold POL / FFXI folders; yield each REG_SZ value's data + a SquareEnix subpath.
            foreach (var line in outp.Split('\n'))
            {
                int sz = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
                if (sz < 0) continue;
                string data = line[(sz + 6)..].Trim();
                if (data.Length < 2) continue;
                yield return data;
                yield return Path.Combine(data, "SquareEnix", "FINAL FANTASY XI");
            }
        }
    }
}
