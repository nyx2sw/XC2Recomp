using System.Security.Cryptography;
using System.Text.Json;

namespace XC2.Core;

public sealed record SaveSnapshotEntry(
    string Id,
    string Label,
    string SourceCategory, // "backup" | "vault" | "manual" | "emulator"
    string FilePath,
    ParsedSaveInfo Info,
    bool IsActiveMatch
);

public sealed record EmulatorSaveCandidate(
    string EmulatorName,
    string DirPath,
    string FilePath,
    ParsedSaveInfo Info,
    bool HasTorna
);

public sealed record SaveOverview(
    ParsedSaveInfo? ActiveMain,
    ParsedSaveInfo? ActiveTorna,
    bool HasBf2Options,
    bool HasIraOptions,
    string ActiveSaveDir,
    string BackupsDir,
    SaveSnapshotEntry[] Snapshots,
    string[] DetectedEmulatorDirs,
    EmulatorSaveCandidate[] EmulatorSaves
);

public sealed class SaveManager
{
    readonly string activeSaveDir;
    readonly string projectRoot;
    readonly string backupsDir;

    public SaveManager(string activeSaveDir, string projectRoot, string? backupsDir = null)
    {
        this.activeSaveDir = activeSaveDir;
        this.projectRoot = projectRoot;
        this.backupsDir = backupsDir ?? LauncherPaths.SaveBackupsDir;
        Directory.CreateDirectory(this.backupsDir);
    }

    public SaveOverview Inspect(bool jpNames = false)
    {
        var mainPath = Path.Combine(activeSaveDir, "bf2savefile.sav");
        var tornaPath = Path.Combine(activeSaveDir, "irasavefile.sav");
        var activeMain = File.Exists(mainPath) ? SaveParser.ParseFile(mainPath, jpNames: jpNames) : null;
        var activeTorna = File.Exists(tornaPath) ? SaveParser.ParseFile(tornaPath, jpNames: jpNames) : null;
        var activeSha = activeMain?.Sha256 ?? "";

        var snaps = new List<SaveSnapshotEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Launcher-managed backups
        if (Directory.Exists(backupsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(backupsDir).OrderByDescending(d => d))
            {
                var sav = Path.Combine(dir, "bf2savefile.sav");
                if (!File.Exists(sav) || !seenPaths.Add(Path.GetFullPath(sav))) continue;
                var label = ReadManifestLabel(dir) ?? Path.GetFileName(dir);
                var info = SaveParser.ParseFile(sav, jpNames: jpNames);
                snaps.Add(new SaveSnapshotEntry("bkp:" + Path.GetFileName(dir), label, "backup", sav, info,
                    !string.IsNullOrEmpty(activeSha) && info.Sha256 == activeSha));
            }
        }

        // 2. Optional local snapshots placed in <projectRoot>/saves
        var localSavesRoot = Path.Combine(projectRoot, "saves");
        if (Directory.Exists(localSavesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(localSavesRoot).OrderByDescending(d => d))
            {
                var sav = Path.Combine(dir, "bf2savefile.sav");
                if (!File.Exists(sav) || !seenPaths.Add(Path.GetFullPath(sav))) continue;
                var label = ReadManifestLabel(dir) ?? Path.GetFileName(dir);
                var info = SaveParser.ParseFile(sav, jpNames: jpNames);
                snaps.Add(new SaveSnapshotEntry("local:" + Path.GetFileName(dir), label, "manual", sav, info,
                    !string.IsNullOrEmpty(activeSha) && info.Sha256 == activeSha));
            }
        }

        var emuDirs = DetectEmulatorSaveDirs();
        var emuSaves = new List<EmulatorSaveCandidate>();
        foreach (var dir in emuDirs)
        {
            try
            {
                var sav = Path.Combine(dir, "bf2savefile.sav");
                if (!File.Exists(sav)) continue;
                var info = SaveParser.ParseFile(sav, jpNames: jpNames);
                if (!info.IsValid) continue;
                bool hasTorna = File.Exists(Path.Combine(dir, "irasavefile.sav"));
                emuSaves.Add(new EmulatorSaveCandidate(InferEmulatorName(dir), dir, sav, info, hasTorna));
            }
            catch { }
        }

        return new SaveOverview(
            ActiveMain: activeMain,
            ActiveTorna: activeTorna,
            HasBf2Options: File.Exists(Path.Combine(activeSaveDir, "bf2option.sav")),
            HasIraOptions: File.Exists(Path.Combine(activeSaveDir, "iraoption.sav")),
            ActiveSaveDir: activeSaveDir,
            BackupsDir: backupsDir,
            Snapshots: snaps.ToArray(),
            DetectedEmulatorDirs: emuDirs,
            EmulatorSaves: emuSaves.ToArray()
        );
    }

    public SaveSnapshotEntry CreateBackup(string? label = null)
    {
        var mainPath = Path.Combine(activeSaveDir, "bf2savefile.sav");
        if (!File.Exists(mainPath))
            throw new FileNotFoundException("Nenhum bf2savefile.sav ativo encontrado para backup.");

        var info = SaveParser.ParseFile(mainPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safeTag = SanitizeName(string.IsNullOrWhiteSpace(label) ? info.LandmarkName : label);
        var destDir = Path.Combine(backupsDir, $"{stamp}_{safeTag}");
        Directory.CreateDirectory(destDir);

        foreach (var name in new[] { "bf2savefile.sav", "bf2option.sav", "iraoption.sav", "irasavefile.sav" })
        {
            var src = Path.Combine(activeSaveDir, name);
            if (File.Exists(src))
                CopyAtomic(src, Path.Combine(destDir, name));
        }

        var manifest = new
        {
            label = string.IsNullOrWhiteSpace(label) ? $"{info.LandmarkName} · {info.PlayTimeFormatted}" : label.Trim(),
            createdAt = DateTime.Now.ToString("O"),
            sha256 = info.Sha256,
            region = info.RegionName,
            landmark = info.LandmarkName,
            leader = info.LeaderName,
            blade = info.ActiveBladeName,
            level = info.LeaderLevel,
            playTime = info.PlayTimeFormatted,
            gold = info.Gold
        };
        var mfPath = Path.Combine(destDir, "manifest.json");
        File.WriteAllText(mfPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var backedSav = Path.Combine(destDir, "bf2savefile.sav");
        var verified = SaveParser.ParseFile(backedSav);
        if (verified.Sha256 != info.Sha256)
            throw new IOException("Falha na verificação SHA-256 do backup criado.");

        LauncherLog.Write($"saves: backup created at {destDir} (sha={info.Sha256[..12]})");
        return new SaveSnapshotEntry("bkp:" + Path.GetFileName(destDir), manifest.label, "backup", backedSav, verified, true);
    }

    public ParsedSaveInfo RestoreFromFile(string sourceSavPath)
    {
        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o Xenoblade Chronicles 2 antes de trocar ou restaurar saves.");

        if (Directory.Exists(sourceSavPath))
        {
            var candidate = Path.Combine(sourceSavPath, "bf2savefile.sav");
            if (!File.Exists(candidate))
                throw new FileNotFoundException("A pasta selecionada não contém bf2savefile.sav.");
            sourceSavPath = candidate;
        }

        if (!File.Exists(sourceSavPath))
            throw new FileNotFoundException("Arquivo de save não encontrado.", sourceSavPath);

        var incoming = SaveParser.ParseFile(sourceSavPath);
        if (!incoming.IsValid)
            throw new InvalidDataException("O arquivo selecionado não é um save válido de Xenoblade Chronicles 2 (magic/tamanho incompatível).");

        // Always create a safety backup of the active save first!
        var activePath = Path.Combine(activeSaveDir, "bf2savefile.sav");
        if (File.Exists(activePath))
        {
            var activeSha = GameInstall.QuickSha256(activePath);
            if (!activeSha.Equals(incoming.Sha256, StringComparison.OrdinalIgnoreCase))
                CreateBackup("Auto-backup antes da troca");
        }

        Directory.CreateDirectory(activeSaveDir);
        CopyAtomic(sourceSavPath, activePath);

        // Verify SHA-256 after atomic replace
        var installed = SaveParser.ParseFile(activePath);
        if (!installed.Sha256.Equals(incoming.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Erro de integridade SHA-256 após gravar o save ativo.");

        LauncherLog.Write($"saves: restored {sourceSavPath} -> {activePath} (sha={installed.Sha256[..12]})");
        return installed;
    }

    public string ExportActiveToFolder(string destinationFolder)
    {
        var mainPath = Path.Combine(activeSaveDir, "bf2savefile.sav");
        if (!File.Exists(mainPath))
            throw new FileNotFoundException("Nenhum save ativo encontrado para exportar.");

        Directory.CreateDirectory(destinationFolder);
        int copied = 0;
        foreach (var name in new[] { "bf2savefile.sav", "bf2option.sav", "iraoption.sav", "irasavefile.sav" })
        {
            var src = Path.Combine(activeSaveDir, name);
            if (File.Exists(src))
            {
                CopyAtomic(src, Path.Combine(destinationFolder, name));
                copied++;
            }
        }
        LauncherLog.Write($"saves: exported {copied} files to {destinationFolder}");
        return destinationFolder;
    }

    public static string[] DetectEmulatorSaveDirs()
    {
        var found = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] roots =
        [
            Path.Combine(appData, "Ryujinx", "bis", "user", "save"),
            Path.Combine(appData, "yuzu", "nand", "user", "save"),
            Path.Combine(appData, "sudachi", "nand", "user", "save"),
            Path.Combine(appData, "citron", "nand", "user", "save"),
            Path.Combine(appData, "eden", "nand", "user", "save"),
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var sav in Directory.EnumerateFiles(root, "bf2savefile.sav", SearchOption.AllDirectories))
                {
                    var dir = Path.GetDirectoryName(sav);
                    if (dir != null && !found.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        found.Add(dir);
                }
            }
            catch { }
        }
        return found.ToArray();
    }

    public static string InferEmulatorName(string dirPath)
    {
        var low = (dirPath ?? "").ToLowerInvariant();
        if (low.Contains("ryujinx")) return "Ryujinx";
        if (low.Contains("eden")) return "Eden";
        if (low.Contains("sudachi")) return "Sudachi";
        if (low.Contains("citron")) return "Citron";
        if (low.Contains("yuzu")) return "Yuzu";
        return "Emulador";
    }

    static string? ReadManifestLabel(string dir)
    {
        try
        {
            var mf = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mf)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(mf));
            if (doc.RootElement.TryGetProperty("label", out var el)) return el.GetString();
        }
        catch { }
        return null;
    }

    static void CopyAtomic(string src, string dst)
    {
        var tmp = dst + $".tmp.{Environment.ProcessId}";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    static string SanitizeName(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        var res = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(res) ? "backup" : res[..Math.Min(32, res.Length)];
    }
}
