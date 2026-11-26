using System.Security.Cryptography;

namespace XC2.Core;

public sealed record DlcAddonInfo(
    string FileName,
    string TitleId,
    string DisplayName,
    string Category,
    long SizeBytes,
    bool Present
);

public sealed record InstallStatus(
    string InstallDir,
    string ProjectRoot,
    string ModeLabel, // "Ambiente de Teste (work/test)" | "Produção (games)" | "Personalizado"
    bool HasExe,
    bool HasAotDll,
    bool HasRomfs,
    bool HasDlss,
    bool HasNacp,
    bool HasIni,
    string ExePath,
    string IniPath,
    string RomfsDir,
    string SaveUserDir,
    string LocalizationDir,
    string AddonsDir,
    string ShaderCacheDir,
    string LogPath,
    string FidelityPath,
    string ExeShortHash,
    string AotShortHash,
    DateTime ExeWriteTime,
    int ShaderCacheFiles,
    long ShaderCacheBytes,
    int ShaderCatalogCount,
    bool ShaderCachePrecompiled,
    DlcAddonInfo[] Addons,
    string? VisualKitDir,
    bool IsReadyToPlay,
    string[] AvailableInstallDirs,
    string? DlssVersion = null,
    int HintsCount = 0,
    bool HintsNewerThanAot = false,
    bool HasExefsForAot = false
);

public static class GameInstall
{
    static readonly Dictionary<string, (string Name, string Category)> KnownAddons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0100e95004039001.romfs"] = ("Torna — The Golden Country", "Expansão de História"),
        ["0100e95004039002.romfs"] = ("Pacote de Itens Úteis (Expansion Pass)", "Itens & Recompensas"),
        ["0100e95004039003.romfs"] = ("Novas Missões & Desafios", "Missões"),
        ["0100e95004039004.romfs"] = ("Novas Blades Raras & Challenge Battle", "Blades & Desafios"),
        ["0100e95004039005.romfs"] = ("Licença Expansion Pass (A)", "Chave de Conteúdo"),
        ["0100e95004039006.romfs"] = ("Licença Expansion Pass (B)", "Chave de Conteúdo"),
        ["0100e95004039063.romfs"] = ("Vozes em Japonês (Áudio Original)", "Pacote de Idioma"),
    };

    public static string FindProjectRoot(string? hintPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(hintPath)) candidates.Add(hintPath);
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Environment.CurrentDirectory);

        foreach (var start in candidates)
        {
            try
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "games", "xenoblade2_data")) ||
                        Directory.Exists(Path.Combine(dir.FullName, "work", "test", "xenoblade2_data")) ||
                        File.Exists(Path.Combine(dir.FullName, "runtime", "xenoblade2.exe")) ||
                        File.Exists(Path.Combine(dir.FullName, "runtime", "scarletnx.exe")) ||
                        File.Exists(Path.Combine(dir.FullName, "games", "xenoblade2.exe")) ||
                        File.Exists(Path.Combine(dir.FullName, "launcher", "XC2.Launcher.sln")))
                    {
                        return dir.FullName;
                    }
                }
            }
            catch { }
        }
        return Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string[] DiscoverInstallDirs(string projectRoot)
    {
        var list = new List<string>();
        var prodDir = Path.Combine(projectRoot, "games");
        var testDir = Path.Combine(projectRoot, "work", "test");
        if (File.Exists(Path.Combine(prodDir, "xenoblade2.exe"))) list.Add(prodDir);
        if (File.Exists(Path.Combine(testDir, "xenoblade2.exe"))) list.Add(testDir);
        if (list.Count == 0)
        {
            if (Directory.Exists(prodDir)) list.Add(prodDir);
            if (Directory.Exists(testDir)) list.Add(testDir);
        }
        return list.ToArray();
    }

    public static string ResolveInstallDir(LauncherConfig config, string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.InstallPath) &&
            File.Exists(Path.Combine(config.InstallPath, "xenoblade2.exe")))
        {
            return config.InstallPath;
        }
        var found = DiscoverInstallDirs(projectRoot);
        if (found.Length > 0) return found[0];
        return Path.Combine(projectRoot, "games");
    }

    public static InstallStatus Inspect(string installDir, string projectRoot, bool computeHashes = false)
    {
        var exePath = Path.Combine(installDir, "xenoblade2.exe");
        var aotPath = Path.Combine(installDir, "xenoblade2_code.dll");
        var iniPath = Path.Combine(installDir, "xenoblade2.ini");
        var dataDir = Path.Combine(installDir, "xenoblade2_data");
        var romfsDir = Path.Combine(dataDir, "romfs");
        var saveUserDir = Path.Combine(dataDir, "save", "user");
        var locDir = Path.Combine(dataDir, "localization");
        var addonsDir = Path.Combine(dataDir, "addons");
        var shaderDir = Path.Combine(installDir, "shader_cache");
        var logPath = Path.Combine(installDir, "xenoblade2.log");
        var fidPath = Path.Combine(installDir, "fidelity.txt");
        var nacpPath = Path.Combine(dataDir, "control", "control.nacp");

        bool hasExe = File.Exists(exePath);
        bool hasAot = File.Exists(aotPath);
        bool hasRomfs = File.Exists(Path.Combine(romfsDir, "bf2.ard"));
        bool hasDlss = File.Exists(Path.Combine(installDir, "nvngx_dlss.dll")) &&
                       File.Exists(Path.Combine(installDir, "snx_dlss.dll"));
        string? dlssVersion = hasDlss ? DlssDownloader.GetInstalledVersion(installDir) : null;
        bool hasNacp = File.Exists(nacpPath) || File.Exists(Path.Combine(dataDir, "control.nacp"));
        bool hasIni = File.Exists(iniPath);
        bool hasExefs = File.Exists(Path.Combine(dataDir, "exefs", "main"));
        string hintsPath = Path.Combine(installDir, "xenoblade2.hints");
        int hintsCount = 0;
        bool hintsNewerThanAot = false;
        if (File.Exists(hintsPath))
        {
            try
            {
                hintsCount = File.ReadLines(hintsPath).Count(l => !string.IsNullOrWhiteSpace(l));
                if (hasAot)
                {
                    var hintsTime = File.GetLastWriteTimeUtc(hintsPath);
                    var aotTime = File.GetLastWriteTimeUtc(aotPath);
                    hintsNewerThanAot = hintsTime > aotTime.AddSeconds(15);
                }
            }
            catch { }
        }

        string exeHash = "", aotHash = "";
        DateTime exeTime = hasExe ? File.GetLastWriteTime(exePath) : DateTime.MinValue;
        if (computeHashes)
        {
            if (hasExe) exeHash = QuickSha256(exePath)[..12];
            if (hasAot) aotHash = QuickSha256(aotPath)[..12];
        }

        int scFiles = 0;
        long scBytes = 0;
        int catalogCount = 0;
        bool precompiled = false;
        if (Directory.Exists(shaderDir))
        {
            try
            {
                foreach (var fi in new DirectoryInfo(shaderDir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    scFiles++;
                    scBytes += fi.Length;
                }
                var catPath = Path.Combine(shaderDir, "xc2_shaders.snxs");
                if (File.Exists(catPath))
                {
                    using var fs = File.OpenRead(catPath);
                    Span<byte> hdr = stackalloc byte[16];
                    if (fs.Read(hdr) == 16 && hdr[0] == 'S' && hdr[1] == 'N' && hdr[2] == 'X' && hdr[3] == 'S')
                        catalogCount = BitConverter.ToInt32(hdr.Slice(8, 4));
                }
                precompiled = File.Exists(Path.Combine(shaderDir, "vk_precompile.stamp")) &&
                              File.Exists(Path.Combine(shaderDir, "vk_pipelines.bin"));
            }
            catch { }
        }

        var addons = new List<DlcAddonInfo>();
        if (Directory.Exists(addonsDir))
        {
            foreach (var file in Directory.EnumerateFiles(addonsDir, "*.romfs").OrderBy(f => f))
            {
                var fi = new FileInfo(file);
                var name = fi.Name;
                var titleId = Path.GetFileNameWithoutExtension(name);
                if (!KnownAddons.TryGetValue(name, out var meta))
                    meta = ($"Addon {titleId}", "DLC");
                addons.Add(new DlcAddonInfo(name, titleId, meta.Name, meta.Category, fi.Length, true));
            }
        }

        string modeLabel;
        var normInstall = Path.GetFullPath(installDir).TrimEnd('\\');
        var normTest = Path.GetFullPath(Path.Combine(projectRoot, "work", "test")).TrimEnd('\\');
        var normProd = Path.GetFullPath(Path.Combine(projectRoot, "games")).TrimEnd('\\');
        if (normInstall.Equals(normTest, StringComparison.OrdinalIgnoreCase))
            modeLabel = "Switch 2 Edition 3.0.1 · Teste Único (work/test)";
        else if (normInstall.Equals(normProd, StringComparison.OrdinalIgnoreCase))
            modeLabel = "Switch 2 Edition 3.0.1 · Produção (games)";
        else
            modeLabel = "Instalação Personalizada";

        string? visualKit = null;
        string[] kitCandidates =
        [
            Path.Combine(projectRoot, "assets", "visual_kit"),
            Path.Combine(projectRoot, "work", "visual_identity_kit", "v2_native", "kit")
        ];
        foreach (var kc in kitCandidates)
        {
            if (Directory.Exists(kc)) { visualKit = kc; break; }
        }

        return new InstallStatus(
            InstallDir: installDir,
            ProjectRoot: projectRoot,
            ModeLabel: modeLabel,
            HasExe: hasExe,
            HasAotDll: hasAot,
            HasRomfs: hasRomfs,
            HasDlss: hasDlss,
            HasNacp: hasNacp,
            HasIni: hasIni,
            ExePath: exePath,
            IniPath: iniPath,
            RomfsDir: romfsDir,
            SaveUserDir: saveUserDir,
            LocalizationDir: locDir,
            AddonsDir: addonsDir,
            ShaderCacheDir: shaderDir,
            LogPath: logPath,
            FidelityPath: fidPath,
            ExeShortHash: exeHash,
            AotShortHash: aotHash,
            ExeWriteTime: exeTime,
            ShaderCacheFiles: scFiles,
            ShaderCacheBytes: scBytes,
            ShaderCatalogCount: catalogCount,
            ShaderCachePrecompiled: precompiled,
            Addons: addons.ToArray(),
            VisualKitDir: visualKit,
            IsReadyToPlay: hasExe && hasAot && hasRomfs,
            AvailableInstallDirs: DiscoverInstallDirs(projectRoot),
            DlssVersion: dlssVersion,
            HintsCount: hintsCount,
            HintsNewerThanAot: hintsNewerThanAot,
            HasExefsForAot: hasExefs
        );
    }

    public static string QuickSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
