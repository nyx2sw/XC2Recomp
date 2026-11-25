using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace XC2.Core;

public sealed record SteamAccountInfo(
    string SteamRoot,
    string UserId,
    string PersonaName,
    string ShortcutsPath,
    string GridDir,
    bool HasShortcut,
    bool IsSteamRunning
);

/// <summary>
/// Byte-exact binary Steam shortcuts.vdf manager (same safety rules as Triforce):
/// - Never writes while Steam is running (Steam overwrites shortcuts.vdf on exit).
/// - Parses to exact EOF; refuses malformed files without touching them.
/// - Preserves every other shortcut byte-for-byte.
/// - Creates .before-xc2 (first time) and .xc2-backup before every write, then atomic replace.
/// - Applies verified visual_identity_kit artwork into config/grid/<appId>*.png.
/// </summary>
public static class SteamIntegration
{
    public const string ShortcutAppName = "Xenoblade Chronicles 2: PC Edition";
    static readonly string[] KnownTargets = ["xenoblade2.exe", "Xenoblade2Launcher.exe"];

    sealed class Node
    {
        public byte Type;
        public string Name = "";
        public int Start, Value, End;
        public List<Node>? Children;
    }

    public static bool IsSteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    public static string? FindSteamRoot()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
            var p = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p))
            {
                p = p.Replace('/', '\\');
                if (Directory.Exists(p)) return p;
            }
        }
        catch { }

        foreach (var cand in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"F:\Steam"
        })
        {
            if (Directory.Exists(Path.Combine(cand, "userdata"))) return cand;
        }
        return null;
    }

    public static SteamAccountInfo[] DiscoverAccounts(string? overrideSteamRoot = null)
    {
        var root = overrideSteamRoot ?? FindSteamRoot();
        if (root == null) return Array.Empty<SteamAccountInfo>();
        var userData = Path.Combine(root, "userdata");
        if (!Directory.Exists(userData)) return Array.Empty<SteamAccountInfo>();

        bool running = IsSteamRunning();
        var list = new List<SteamAccountInfo>();
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            var id = Path.GetFileName(dir);
            if (!ulong.TryParse(id, out _) || id == "0") continue;
            var cfgDir = Path.Combine(dir, "config");
            var shortcuts = Path.Combine(cfgDir, "shortcuts.vdf");
            var grid = Path.Combine(cfgDir, "grid");
            var persona = ReadPersonaName(Path.Combine(cfgDir, "localconfig.vdf")) ?? $"Conta Steam {id}";
            bool has = false;
            try { if (File.Exists(shortcuts)) has = ContainsOurShortcut(File.ReadAllBytes(shortcuts)); } catch { }
            list.Add(new SteamAccountInfo(root, id, persona, shortcuts, grid, has, running));
        }
        return list.OrderByDescending(a => File.Exists(a.ShortcutsPath) ? File.GetLastWriteTimeUtc(a.ShortcutsPath) : DateTime.MinValue).ToArray();
    }

    public static uint ComputeShortcutAppId(string quotedExe, string appName)
    {
        // Steam non-Steam shortcut AppID: CRC32(exe + appName) | 0x80000000
        var bytes = Encoding.UTF8.GetBytes(quotedExe + appName);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return (~crc) | 0x80000000u;
    }

    public static (uint AppId, int ArtCopied) AddOrUpdateShortcut(
        string shortcutsPath, string gridDir, string targetExePath, string startDir,
        string? iconPath, string? visualKitDir, string launchOptions = "", bool ignoreRunningCheck = false)
    {
        if (!ignoreRunningCheck && IsSteamRunning())
            throw new InvalidOperationException("Feche a Steam primeiro. A Steam sobrescreve shortcuts.vdf ao encerrar.");

        Directory.CreateDirectory(Path.GetDirectoryName(shortcutsPath)!);
        var existing = File.Exists(shortcutsPath) ? File.ReadAllBytes(shortcutsPath) : null;
        var quotedExe = $"\"{targetExePath}\"";
        var quotedStart = $"\"{startDir}\"";
        uint appId = ComputeShortcutAppId(quotedExe, ShortcutAppName);

        var updated = UpsertShortcutVdf(existing, appId, ShortcutAppName, quotedExe, quotedStart, iconPath ?? "", launchOptions);

        // Safety backups before atomic replace
        if (existing != null)
        {
            var firstBkp = shortcutsPath + ".before-xc2";
            if (!File.Exists(firstBkp)) File.WriteAllBytes(firstBkp, existing);
            File.WriteAllBytes(shortcutsPath + ".xc2-backup", existing);
        }

        var tmp = shortcutsPath + $".tmp.{Environment.ProcessId}";
        File.WriteAllBytes(tmp, updated);
        File.Move(tmp, shortcutsPath, overwrite: true);

        int artCopied = 0;
        if (!string.IsNullOrEmpty(visualKitDir) && Directory.Exists(visualKitDir))
        {
            Directory.CreateDirectory(gridDir);
            (string SrcRel, string Suffix)[] assets =
            [
                (@"03_biblioteca\capa_600x900.png", "p.png"),
                (@"03_biblioteca\banner_920x430.png", ".png"),
                (@"03_biblioteca\hero_sem_texto_1920x620.png", "_hero.png"),
                (@"02_titulo\xc2_pc_edition_640.png", "_logo.png"),
                (@"01_atalhos\png\xc2_cristal_256.png", "_icon.png"),
            ];
            foreach (var (rel, suf) in assets)
            {
                var src = Path.Combine(visualKitDir, rel);
                if (File.Exists(src))
                {
                    var dst = Path.Combine(gridDir, $"{appId}{suf}");
                    var dstTmp = dst + $".tmp.{Environment.ProcessId}";
                    File.Copy(src, dstTmp, overwrite: true);
                    File.Move(dstTmp, dst, overwrite: true);
                    artCopied++;
                }
            }
        }

        LauncherLog.Write($"steam: added shortcut appId={appId} art={artCopied} to {shortcutsPath}");
        return (appId, artCopied);
    }

    public static bool RemoveShortcut(string shortcutsPath, bool ignoreRunningCheck = false)
    {
        if (!ignoreRunningCheck && IsSteamRunning())
            throw new InvalidOperationException("Feche a Steam primeiro antes de remover o atalho.");
        if (!File.Exists(shortcutsPath)) return false;

        var existing = File.ReadAllBytes(shortcutsPath);
        if (!ContainsOurShortcut(existing)) return false;

        var updated = RemoveShortcutVdf(existing);
        File.WriteAllBytes(shortcutsPath + ".xc2-backup", existing);
        var tmp = shortcutsPath + $".tmp.{Environment.ProcessId}";
        File.WriteAllBytes(tmp, updated);
        File.Move(tmp, shortcutsPath, overwrite: true);
        LauncherLog.Write($"steam: removed shortcut from {shortcutsPath}");
        return true;
    }

    // ==================== Binary VDF parser & writer ====================

    public static bool ContainsOurShortcut(byte[] vdf)
    {
        if (vdf.Length == 0) return false;
        var entries = ParseShortcutEntries(vdf);
        return entries.Any(e => IsOurEntry(vdf, e));
    }

    public static byte[] UpsertShortcutVdf(
        byte[]? existing, uint appId, string appName, string quotedExe, string quotedStartDir,
        string iconPath, string launchOptions)
    {
        var keptRawEntries = new List<byte[]>();
        if (existing != null && existing.Length > 0)
        {
            var entries = ParseShortcutEntries(existing);
            foreach (var entry in entries)
            {
                if (!IsOurEntry(existing, entry))
                {
                    // Copy raw inner map bytes (everything inside entry between Value and End-1)
                    int bodyLen = entry.End - 1 - entry.Value;
                    var rawBody = new byte[bodyLen];
                    Buffer.BlockCopy(existing, entry.Value, rawBody, 0, bodyLen);
                    keptRawEntries.Add(rawBody);
                }
            }
        }

        // Build our entry's inner map bytes
        using var ourMs = new MemoryStream();
        WriteInt(ourMs, "appid", unchecked((int)appId));
        WriteStr(ourMs, "AppName", appName);
        WriteStr(ourMs, "Exe", quotedExe);
        WriteStr(ourMs, "StartDir", quotedStartDir);
        WriteStr(ourMs, "icon", iconPath);
        WriteStr(ourMs, "ShortcutPath", "");
        WriteStr(ourMs, "LaunchOptions", launchOptions);
        WriteInt(ourMs, "IsHidden", 0);
        WriteInt(ourMs, "AllowDesktopConfig", 1);
        WriteInt(ourMs, "AllowOverlay", 1);
        WriteInt(ourMs, "OpenVR", 0);
        WriteInt(ourMs, "Devkit", 0);
        WriteStr(ourMs, "DevkitGameID", "");
        WriteInt(ourMs, "DevkitOverrideAppID", 0);
        WriteInt(ourMs, "LastPlayTime", 0);
        WriteStr(ourMs, "FlatpakAppID", "");
        // tags submap
        ourMs.WriteByte(0x00);
        WriteCString(ourMs, "tags");
        ourMs.WriteByte(0x08);
        keptRawEntries.Add(ourMs.ToArray());

        return BuildRootVdf(keptRawEntries);
    }

    public static byte[] RemoveShortcutVdf(byte[] existing)
    {
        var entries = ParseShortcutEntries(existing);
        var kept = new List<byte[]>();
        foreach (var entry in entries)
        {
            if (IsOurEntry(existing, entry)) continue;
            int bodyLen = entry.End - 1 - entry.Value;
            var rawBody = new byte[bodyLen];
            Buffer.BlockCopy(existing, entry.Value, rawBody, 0, bodyLen);
            kept.Add(rawBody);
        }
        return BuildRootVdf(kept);
    }

    static byte[] BuildRootVdf(List<byte[]> entryBodies)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00);
        WriteCString(ms, "shortcuts");
        for (int i = 0; i < entryBodies.Count; i++)
        {
            ms.WriteByte(0x00);
            WriteCString(ms, i.ToString());
            ms.Write(entryBodies[i], 0, entryBodies[i].Length);
            ms.WriteByte(0x08);
        }
        ms.WriteByte(0x08);
        ms.WriteByte(0x08);
        return ms.ToArray();
    }

    static List<Node> ParseShortcutEntries(byte[] b)
    {
        int at = 0;
        var top = ReadMap(b, ref at, 0);
        if (at != b.Length)
            throw new InvalidDataException("shortcuts.vdf possui bytes inesperados após o fim; arquivo preservado sem alterações.");
        var sc = top.FirstOrDefault(n => n.Type == 0 && n.Name.Equals("shortcuts", StringComparison.OrdinalIgnoreCase));
        return sc?.Children ?? new List<Node>();
    }

    static List<Node> ReadMap(byte[] b, ref int at, int depth)
    {
        if (depth > 16) throw new InvalidDataException("Mapa VDF aninhado demais.");
        var list = new List<Node>();
        while (true)
        {
            if (at >= b.Length) throw new InvalidDataException("shortcuts.vdf truncado.");
            int start = at;
            byte type = b[at++];
            if (type == 0x08) return list;
            int nul = Array.IndexOf(b, (byte)0, at);
            if (nul < 0) throw new InvalidDataException("Nome de campo VDF sem terminador.");
            var name = Encoding.UTF8.GetString(b, at, nul - at);
            at = nul + 1;
            int valStart = at;
            List<Node>? children = null;
            switch (type)
            {
                case 0x00: children = ReadMap(b, ref at, depth + 1); break;
                case 0x01:
                    nul = Array.IndexOf(b, (byte)0, at);
                    if (nul < 0) throw new InvalidDataException("Texto VDF sem terminador.");
                    at = nul + 1;
                    break;
                case 0x02: case 0x03: case 0x04: case 0x06: at += 4; break;
                case 0x07: case 0x0A: at += 8; break;
                default: throw new InvalidDataException($"Tipo VDF desconhecido 0x{type:X2}.");
            }
            if (at > b.Length) throw new InvalidDataException("Valor VDF fora dos limites.");
            list.Add(new Node { Type = type, Name = name, Start = start, Value = valStart, End = at, Children = children });
        }
    }

    static bool IsOurEntry(byte[] b, Node entry)
    {
        if (entry.Type != 0 || entry.Children == null) return false;
        foreach (var c in entry.Children)
        {
            if (c.Type != 1) continue;
            var val = Encoding.UTF8.GetString(b, c.Value, c.End - 1 - c.Value);
            if (c.Name.Equals("AppName", StringComparison.OrdinalIgnoreCase) &&
                val.Contains("Xenoblade Chronicles 2", StringComparison.OrdinalIgnoreCase))
                return true;
            if (c.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase))
            {
                var clean = val.Trim('"');
                if (KnownTargets.Any(t => clean.EndsWith(t, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    static void WriteStr(Stream s, string key, string val)
    {
        s.WriteByte(0x01);
        WriteCString(s, key);
        WriteCString(s, val);
    }

    static void WriteInt(Stream s, string key, int val)
    {
        s.WriteByte(0x02);
        WriteCString(s, key);
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, val);
        s.Write(buf);
    }

    static void WriteCString(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0);
    }

    static string? ReadPersonaName(string localConfigPath)
    {
        try
        {
            if (!File.Exists(localConfigPath)) return null;
            foreach (var line in File.ReadLines(localConfigPath).Take(800))
            {
                var idx = line.IndexOf("\"PersonaName\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var rest = line[(idx + 13)..].Trim();
                if (rest.StartsWith('"') && rest.EndsWith('"') && rest.Length >= 2)
                    return rest[1..^1];
            }
        }
        catch { }
        return null;
    }

    public static string CreateDesktopShortcut(string launcherExePath, string? iconPath)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desktop, "Xenoblade Chronicles 2 PC Edition.lnk");
        var ico = (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) ? iconPath : launcherExePath;
        var wd = Path.GetDirectoryName(launcherExePath) ?? "";

        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnk.Replace("'", "''")}');" +
                 $"$s.TargetPath='{launcherExePath.Replace("'", "''")}';" +
                 $"$s.WorkingDirectory='{wd.Replace("'", "''")}';" +
                 $"$s.IconLocation='{ico.Replace("'", "''")},0';" +
                 $"$s.Description='Xenoblade Chronicles 2: PC Edition';" +
                 $"$s.Save()";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{ps}\"")
        {
            CreateNoWindow = true, UseShellExecute = false
        });
        p?.WaitForExit(8000);
        return lnk;
    }
}
