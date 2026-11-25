using System.IO.Compression;

namespace XC2.Core;

public sealed record ResourcePackInfo(
    string Id,
    string Name,
    string FolderPath,
    int FileCount,
    long SizeBytes,
    bool IsActive,
    string Kind // "localization" | "ptbr_archive" | "user_mod"
);

public sealed record ModsOverview(
    string ActiveResourcePack,
    string LocalizationDir,
    ResourcePackInfo[] InstalledPacks,
    ResourcePackInfo[] AvailableTranslations,
    ResourcePackInfo[] UserMods,
    DlcAddonInfo[] Addons
);

public sealed class ModManager
{
    readonly InstallStatus install;

    public ModManager(InstallStatus install) => this.install = install;

    public ModsOverview Inspect()
    {
        var ini = IniSettings.Load(install.IniPath);
        var active = ini.Get("Display", "resource_pack", "");

        var installed = new List<ResourcePackInfo>();
        if (Directory.Exists(install.LocalizationDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(install.LocalizationDir).OrderBy(d => d))
            {
                var id = Path.GetFileName(dir);
                var (cnt, bytes) = MeasureDir(dir);
                installed.Add(new ResourcePackInfo(id, FormatPackName(id), dir, cnt, bytes,
                    id.Equals(active, StringComparison.OrdinalIgnoreCase), "localization"));
            }
        }

        var translations = new List<ResourcePackInfo>();
        var userMods = new List<ResourcePackInfo>();
        var modsRoot = Directory.Exists(Path.Combine(install.ProjectRoot, "mods"))
            ? Path.Combine(install.ProjectRoot, "mods")
            : Path.Combine(install.ProjectRoot, "work", "user_mods");
        if (Directory.Exists(modsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(modsRoot).OrderByDescending(d => d))
            {
                var id = Path.GetFileName(dir);
                var (cnt, bytes) = MeasureDir(dir);
                userMods.Add(new ResourcePackInfo(id, id, dir, cnt, bytes, false, "user_mod"));
            }
        }

        return new ModsOverview(
            ActiveResourcePack: active,
            LocalizationDir: install.LocalizationDir,
            InstalledPacks: installed.ToArray(),
            AvailableTranslations: translations.ToArray(),
            UserMods: userMods.ToArray(),
            Addons: install.Addons
        );
    }

    public void SetActiveResourcePack(string packId)
    {
        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de alterar o pacote de recursos.");

        if (!string.IsNullOrEmpty(packId))
        {
            var target = Path.Combine(install.LocalizationDir, packId);
            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException($"Pacote '{packId}' não encontrado em localization/.");
        }

        var ini = IniSettings.Load(install.IniPath);
        ini.Set("Display", "resource_pack", packId);
        if (packId.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            ini.Set("Display", "language", "17");
        else if (string.IsNullOrEmpty(packId))
            ini.Set("Display", "language", "1");
        ini.SaveAtomic();
        LauncherLog.Write($"mods: resource_pack set to '{packId}'");
    }

    /// <summary>Safely installs a dropped folder or .zip into xenoblade2_data/localization/<name>/romfs without touching hardlinked ARD files.</summary>
    public ResourcePackInfo InstallDroppedPack(string sourcePath)
    {
        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de instalar mods ou traduções.");

        Directory.CreateDirectory(install.LocalizationDir);
        string rawName = Path.GetFileNameWithoutExtension(sourcePath);
        string packId = SanitizePackId(rawName);
        string destRoot = Path.Combine(install.LocalizationDir, packId);
        string destRomfs = Path.Combine(destRoot, "romfs");

        if (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(install.LocalizationDir, $".tmp_{packId}_{Environment.ProcessId}");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            try
            {
                ZipFile.ExtractToDirectory(sourcePath, tempDir, overwriteFiles: true);
                var contentSrc = FindRomfsRoot(tempDir);
                if (Directory.Exists(destRoot)) Directory.Delete(destRoot, true);
                Directory.CreateDirectory(destRomfs);
                CopyDirectorySafe(contentSrc, destRomfs);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
        else if (Directory.Exists(sourcePath))
        {
            var contentSrc = FindRomfsRoot(sourcePath);
            if (Directory.Exists(destRoot)) Directory.Delete(destRoot, true);
            Directory.CreateDirectory(destRomfs);
            CopyDirectorySafe(contentSrc, destRomfs);
        }
        else
        {
            throw new InvalidOperationException("Arraste uma pasta de mod/tradução ou um arquivo .zip.");
        }

        var (cnt, bytes) = MeasureDir(destRoot);
        LauncherLog.Write($"mods: installed pack '{packId}' ({cnt} files, {bytes:N0} B)");
        return new ResourcePackInfo(packId, FormatPackName(packId), destRoot, cnt, bytes, false, "localization");
    }

    static string FindRomfsRoot(string dir)
    {
        var direct = Path.Combine(dir, "romfs");
        if (Directory.Exists(direct)) return direct;
        var subs = Directory.GetDirectories(dir);
        if (subs.Length == 1 && Directory.GetFiles(dir).Length == 0)
        {
            var nested = Path.Combine(subs[0], "romfs");
            return Directory.Exists(nested) ? nested : subs[0];
        }
        return dir;
    }

    static void CopyDirectorySafe(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
        {
            CopyDirectorySafe(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }
    }

    static (int Count, long Bytes) MeasureDir(string dir)
    {
        int cnt = 0; long bytes = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cnt++;
                bytes += fi.Length;
            }
        }
        catch { }
        return (cnt, bytes);
    }

    static string FormatPackName(string id) => id.ToLowerInvariant() switch
    {
        "ptbr" or "pt-br" or "pt_br" => "Tradução Português do Brasil (PT-BR)",
        "pt-br-jp" or "pt_br_jp" or "ptbr-jp" => "Tradução PT-BR (Nomes Originais JP)",
        _ => id.Replace('_', ' ').Replace('-', ' ')
    };

    static string SanitizePackId(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        var s = sb.ToString().Trim('_', '-');
        return string.IsNullOrEmpty(s) ? "pack" : s[..Math.Min(48, s.Length)];
    }
}
