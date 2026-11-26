using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XC2.Core;

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    bool HasUpdate,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseUrl,
    string PublishedAt,
    string AssetName,
    string AssetUrl,
    long AssetSizeBytes,
    string? ChecksumsUrl,
    DateTime CheckedAt,
    string? Error = null
);

public sealed record UpdateProgress(
    string Stage,
    double Percent,
    string Detail,
    bool IsDone = false,
    bool IsError = false
);

public sealed record UpdateApplyResult(
    bool Success,
    string TargetTag,
    int FilesUpdated,
    int AddedHintsCount,
    int TotalHintsCount,
    bool LauncherReplaced,
    string? NewLauncherPath,
    string Summary
);

/// <summary>
/// Self-updater for XC2Recomp Launcher + ScarletNX runtime, extensions, localization packs, and AOT hints.
/// Queries GitHub Releases (nyx2sw/XC2Recomp), verifies SHA-256 checksums when published,
/// updates runtime binaries atomically, merges xenoblade2.hints without losing locally discovered hints,
/// and never touches user ROMs, DLCs, saves, xenoblade2.ini, nvngx_dlss.dll, or shader_cache.
/// </summary>
public static class LauncherUpdater
{
    public const string DefaultOwner = "nyx2sw";
    public const string DefaultRepo = "XC2Recomp";
    public const string FallbackVersion = "0.2.0";

    static readonly string[] UpdatableRuntimeDlls =
    [
        "libc++.dll", "libffi-8.dll", "libiconv-2.dll", "libLLVM-23.dll",
        "libunwind.dll", "libxml2-16.dll", "libzstd.dll", "zlib1.dll",
        "snx_dlss.dll"
    ];

    static readonly string[] UpdatablePcUiFiles =
    [
        "native_ui.dll", "presence.dll", "presence_names.tsv"
    ];

    static readonly string[] UpdatableBundledPacks = ["pt-br", "pt-br-jp"];

    static readonly string[] UpdatableRootDocs = ["README.md", "LICENSE", "LICENSE.apache", "NOTICE"];

    static readonly HttpClient Http = CreateHttpClient();

    public static string CurrentVersion => ReadAssemblyVersion();

    static string ReadAssemblyVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(LauncherUpdater).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                var clean = (plus >= 0 ? info[..plus] : info).Trim().TrimStart('v', 'V');
                if (!string.IsNullOrWhiteSpace(clean)) return clean;
            }
            var v = asm.GetName().Version;
            if (v != null && (v.Major > 0 || v.Minor > 0 || v.Build > 0))
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
        catch { }
        return FallbackVersion;
    }

    static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XC2Recomp-Launcher/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string LatestReleaseApiUrl(string owner = DefaultOwner, string repo = DefaultRepo)
    {
        var envOverride = Environment.GetEnvironmentVariable("XC2_UPDATE_API_URL");
        if (!string.IsNullOrWhiteSpace(envOverride)) return envOverride.Trim();
        return $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
    }

    public static string ReleasesPageUrl(string owner = DefaultOwner, string repo = DefaultRepo) =>
        $"https://github.com/{owner}/{repo}/releases";

    public static int CompareVersions(string? a, string? b)
    {
        var pa = ParseSemVer(a);
        var pb = ParseSemVer(b);
        int cmp = pa.Major.CompareTo(pb.Major);
        if (cmp != 0) return cmp;
        cmp = pa.Minor.CompareTo(pb.Minor);
        if (cmp != 0) return cmp;
        cmp = pa.Patch.CompareTo(pb.Patch);
        if (cmp != 0) return cmp;
        // Release (no prerelease) > prerelease of same version
        bool aPre = !string.IsNullOrEmpty(pa.Pre);
        bool bPre = !string.IsNullOrEmpty(pb.Pre);
        if (aPre != bPre) return aPre ? -1 : 1;
        return string.Compare(pa.Pre, pb.Pre, StringComparison.OrdinalIgnoreCase);
    }

    static (int Major, int Minor, int Patch, string Pre) ParseSemVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0, 0, "");
        string s = raw.Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string pre = "";
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int maj = parts.Length > 0 && int.TryParse(parts[0], out var m0) ? m0 : 0;
        int min = parts.Length > 1 && int.TryParse(parts[1], out var m1) ? m1 : 0;
        int pat = parts.Length > 2 && int.TryParse(parts[2], out var m2) ? m2 : 0;
        return (maj, min, pat, pre);
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken ct,
        string? currentVersionOverride = null,
        string? apiUrlOverride = null)
    {
        string curVer = string.IsNullOrWhiteSpace(currentVersionOverride) ? CurrentVersion : currentVersionOverride.Trim().TrimStart('v', 'V');
        string url = string.IsNullOrWhiteSpace(apiUrlOverride) ? LatestReleaseApiUrl() : apiUrlOverride;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string msg = (int)resp.StatusCode == 403
                    ? "Limite temporário da API do GitHub atingido. Tente novamente mais tarde."
                    : (int)resp.StatusCode == 404
                        ? "Nenhuma versão publicada encontrada no GitHub."
                        : $"Servidor retornou HTTP {(int)resp.StatusCode}.";
                return new UpdateCheckResult(curVer, curVer, $"v{curVer}", false, "", "", ReleasesPageUrl(), "", "", "", 0, null, DateTime.UtcNow, msg);
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseReleaseJson(json, curVer);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LauncherLog.Write($"updater: check failed: {ex.Message}");
            return new UpdateCheckResult(curVer, curVer, $"v{curVer}", false, "", "", ReleasesPageUrl(), "", "", "", 0, null, DateTime.UtcNow,
                $"Sem conexão com o GitHub ({ex.Message}).");
        }
    }

    public static UpdateCheckResult ParseReleaseJson(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "") : "";
        string latestVer = tag.Trim().TrimStart('v', 'V');
        string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
        string body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "";
        string htmlUrl = root.TryGetProperty("html_url", out var uEl) ? (uEl.GetString() ?? ReleasesPageUrl()) : ReleasesPageUrl();
        string published = root.TryGetProperty("published_at", out var pEl) ? (pEl.GetString() ?? "") : "";

        string assetName = "", assetUrl = "";
        long assetSize = 0;
        string? checksumsUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string fallbackName = "", fallbackUrl = "";
            long fallbackSize = 0;

            foreach (var a in assets.EnumerateArray())
            {
                string an = a.TryGetProperty("name", out var anEl) ? (anEl.GetString() ?? "") : "";
                string au = a.TryGetProperty("browser_download_url", out var auEl) ? (auEl.GetString() ?? "") : "";
                long sz = a.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var l) ? l : 0;

                if (an.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                    an.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl ??= au;
                }
                else if (an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (an.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
                        an.Contains("XC2Recomp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(assetUrl))
                        {
                            assetName = an;
                            assetUrl = au;
                            assetSize = sz;
                        }
                    }
                    else if (string.IsNullOrEmpty(fallbackUrl))
                    {
                        fallbackName = an;
                        fallbackUrl = au;
                        fallbackSize = sz;
                    }
                }
            }

            if (string.IsNullOrEmpty(assetUrl) && !string.IsNullOrEmpty(fallbackUrl))
            {
                assetName = fallbackName;
                assetUrl = fallbackUrl;
                assetSize = fallbackSize;
            }
        }

        bool hasUpdate = !string.IsNullOrWhiteSpace(latestVer) &&
                         !string.IsNullOrWhiteSpace(assetUrl) &&
                         CompareVersions(latestVer, currentVersion) > 0;

        return new UpdateCheckResult(
            CurrentVersion: currentVersion,
            LatestVersion: string.IsNullOrWhiteSpace(latestVer) ? currentVersion : latestVer,
            LatestTag: string.IsNullOrWhiteSpace(tag) ? $"v{currentVersion}" : tag,
            HasUpdate: hasUpdate,
            ReleaseName: name,
            ReleaseNotes: body,
            ReleaseUrl: htmlUrl,
            PublishedAt: published,
            AssetName: assetName,
            AssetUrl: assetUrl,
            AssetSizeBytes: assetSize,
            ChecksumsUrl: checksumsUrl,
            CheckedAt: DateTime.UtcNow
        );
    }

    public static string? ParseExpectedSha256(string checksumsText, string assetFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumsText)) return null;
        string target = Path.GetFileName(assetFileName);
        foreach (var rawLine in checksumsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length < 64 || line.StartsWith('#')) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            string hex = parts[0].Trim();
            if (hex.Length != 64 || !IsHex(hex)) continue;
            if (parts.Length == 1)
                return hex.ToLowerInvariant();
            string fname = Path.GetFileName(parts[^1].TrimStart('*'));
            if (fname.Equals(target, StringComparison.OrdinalIgnoreCase))
                return hex.ToLowerInvariant();
        }
        return null;
    }

    static bool IsHex(string s) => s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    public static async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult release,
        string projectRoot,
        string installDir,
        string? runningLauncherExePath,
        Action<UpdateProgress>? onProgress,
        CancellationToken ct)
    {
        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de instalar a atualização.");
        if (string.IsNullOrWhiteSpace(release.AssetUrl))
            throw new InvalidOperationException("A versão encontrada não possui pacote .zip para Windows x64.");

        string updatesRoot = Path.Combine(LauncherPaths.DataDir, "updates");
        string safeTag = SanitizeTag(release.LatestTag);
        string zipPath = Path.Combine(updatesRoot, $"{safeTag}_{Environment.ProcessId}.zip.tmp");
        string stageDir = Path.Combine(updatesRoot, $"stage_{safeTag}_{Environment.ProcessId}");
        Directory.CreateDirectory(updatesRoot);

        try
        {
            // 1. Optional SHA256SUMS.txt fetch
            string? expectedSha256 = null;
            if (!string.IsNullOrWhiteSpace(release.ChecksumsUrl))
            {
                onProgress?.Invoke(new UpdateProgress("Verificando manifesto", 3,
                    "Baixando SHA256SUMS.txt da release..."));
                try
                {
                    using var sumResp = await Http.GetAsync(release.ChecksumsUrl, ct);
                    if (sumResp.IsSuccessStatusCode)
                    {
                        string sumTxt = await sumResp.Content.ReadAsStringAsync(ct);
                        expectedSha256 = ParseExpectedSha256(sumTxt, release.AssetName);
                    }
                }
                catch (Exception ex)
                {
                    LauncherLog.Write($"updater: warning reading SHA256SUMS.txt: {ex.Message}");
                }
            }

            // 2. Download zip with streaming progress
            onProgress?.Invoke(new UpdateProgress("Baixando atualização", 5,
                $"Conectando para baixar {release.AssetName}..."));

            using (var req = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl))
            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? release.AssetSizeBytes;

                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                var buf = new byte[131072];
                long received = 0;
                long lastTick = Environment.TickCount64;
                int read;
                while ((read = await net.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    received += read;
                    long now = Environment.TickCount64;
                    if (now - lastTick >= 120)
                    {
                        lastTick = now;
                        double pct = total > 0 ? 5.0 + (double)received / total * 65.0 : 40.0;
                        string detail = total > 0
                            ? $"Baixando {release.LatestTag}… {received / 1048576.0:F1} / {total / 1048576.0:F1} MiB"
                            : $"Baixando {release.LatestTag}… {received / 1048576.0:F1} MiB";
                        onProgress?.Invoke(new UpdateProgress("Baixando atualização", pct, detail));
                    }
                }
            }

            // 3. Verify SHA-256 checksum
            onProgress?.Invoke(new UpdateProgress("Validando integridade", 74,
                expectedSha256 != null ? "Conferindo assinatura SHA-256 do pacote..." : "Verificando integridade do pacote ZIP..."));

            string actualSha = await Task.Run(() => GameInstall.QuickSha256(zipPath), ct);
            if (expectedSha256 != null && !actualSha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Falha de integridade SHA-256 no pacote baixado (esperado {expectedSha256[..12]}…, obtido {actualSha[..12]}…).");
            }

            // 4. Safe extraction (Zip-Slip guarded)
            onProgress?.Invoke(new UpdateProgress("Extraindo arquivos", 80,
                "Extraindo nova versão em área temporária..."));

            await Task.Run(() => ExtractZipSafe(zipPath, stageDir, ct), ct);

            // 5. Apply runtime + launcher update atomically
            onProgress?.Invoke(new UpdateProgress("Aplicando atualização", 90,
                "Atualizando runtime, traduções, hints e launcher (saves e arquivos do jogo preservados)..."));

            var res = await Task.Run(() => ApplyStagedDirectory(
                stageDir, projectRoot, installDir, runningLauncherExePath, release.LatestTag, allowSelfReplace: true), ct);

            LauncherLog.Write($"updater: applied {release.LatestTag} ({res.FilesUpdated} files, +{res.AddedHintsCount} hints, launcherReplaced={res.LauncherReplaced}, sha={actualSha[..12]})");
            onProgress?.Invoke(new UpdateProgress("Atualização aplicada", 100, res.Summary, IsDone: true));
            return res;
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDir(stageDir);
        }
    }

    public static void ExtractZipSafe(string zipPath, string destDir, CancellationToken ct = default)
    {
        if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);
        string fullDest = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
                continue;

            string outPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!outPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Entrada ZIP suspeita bloqueada (path traversal): {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            entry.ExtractToFile(outPath, overwrite: true);
        }
    }

    /// <summary>
    /// Applies an extracted release tree to both the portable runtime/ folder (when present) and the active game installDir,
    /// and optionally swaps Xenoblade2Launcher.exe in place via Windows safe exe-rename.
    /// Never overwrites user romfs, addons, saves, exefs, xenoblade2.ini, nvngx_dlss.dll, or shader_cache.
    /// </summary>
    public static UpdateApplyResult ApplyStagedDirectory(
        string extractedRoot,
        string projectRoot,
        string installDir,
        string? runningLauncherExePath,
        string targetTag,
        bool allowSelfReplace = true)
    {
        string pkgRoot = ResolvePackageRoot(extractedRoot);
        string srcRuntime = Path.Combine(pkgRoot, "runtime");
        string srcLauncher = Path.Combine(pkgRoot, "Xenoblade2Launcher.exe");

        if (!Directory.Exists(srcRuntime) && !File.Exists(srcLauncher))
            throw new InvalidDataException("O pacote baixado não contém Xenoblade2Launcher.exe nem pasta runtime/.");

        int filesUpdated = 0;
        int addedHints = 0;
        int totalHints = 0;

        string normProject = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
        string normInstall = Path.GetFullPath(installDir).TrimEnd('\\', '/');
        string dstPortableRuntime = Path.Combine(normProject, "runtime");
        bool isDevWorkspace = File.Exists(Path.Combine(normProject, "launcher", "XC2.Launcher.sln"));

        // 1. Update portable runtime/ folder (if present or in portable user layout)
        if (Directory.Exists(srcRuntime) && (Directory.Exists(dstPortableRuntime) || !isDevWorkspace))
        {
            var (n, _, _) = SyncRuntimeIntoDir(srcRuntime, dstPortableRuntime, isGameInstallDir: false);
            filesUpdated += n;
        }

        // 2. Update root documentation/licenses (portable layout only, never clobber dev repo root)
        if (!isDevWorkspace)
        {
            foreach (var doc in UpdatableRootDocs)
            {
                string s = Path.Combine(pkgRoot, doc);
                if (File.Exists(s))
                {
                    CopyAtomic(s, Path.Combine(normProject, doc));
                    filesUpdated++;
                }
            }
        }

        // 3. Update active game install directory (e.g. games/) while strictly preserving user data
        if (Directory.Exists(srcRuntime) && Directory.Exists(normInstall) &&
            !normInstall.Equals(dstPortableRuntime, StringComparison.OrdinalIgnoreCase))
        {
            var (n, added, total) = SyncRuntimeIntoDir(srcRuntime, normInstall, isGameInstallDir: true);
            filesUpdated += n;
            addedHints = added;
            totalHints = total;
        }
        else if (Directory.Exists(srcRuntime))
        {
            string hintsPath = Path.Combine(dstPortableRuntime, "xenoblade2.hints");
            if (File.Exists(hintsPath))
                totalHints = File.ReadLines(hintsPath).Count(l => !string.IsNullOrWhiteSpace(l));
        }

        // 4. Replace Xenoblade2Launcher.exe safely (Windows allows renaming a running .exe in the same directory)
        bool launcherReplaced = false;
        string? newLauncherPath = null;
        if (allowSelfReplace && File.Exists(srcLauncher) && !string.IsNullOrWhiteSpace(runningLauncherExePath))
        {
            string targetExe = Path.GetFullPath(runningLauncherExePath);
            if (Path.GetFileName(targetExe).Equals("Xenoblade2Launcher.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(targetExe))
            {
                ReplaceRunningExecutable(srcLauncher, targetExe);
                launcherReplaced = true;
                newLauncherPath = targetExe;
                filesUpdated++;
            }
        }

        string hintNote = addedHints > 0
            ? $" (+{addedHints} novos hints AOT — recomenda-se recompilar o código AOT em Configurações › Sistema)"
            : "";
        string summary = $"Atualização {targetTag} aplicada ({filesUpdated} arquivos atualizados){hintNote}.";

        return new UpdateApplyResult(
            Success: true,
            TargetTag: targetTag,
            FilesUpdated: filesUpdated,
            AddedHintsCount: addedHints,
            TotalHintsCount: totalHints,
            LauncherReplaced: launcherReplaced,
            NewLauncherPath: newLauncherPath,
            Summary: summary
        );
    }

    static (int FilesUpdated, int AddedHints, int TotalHints) SyncRuntimeIntoDir(
        string srcRuntime,
        string targetDir,
        bool isGameInstallDir)
    {
        int updated = 0;
        Directory.CreateDirectory(targetDir);

        // Executable
        string srcExe = Path.Combine(srcRuntime, "xenoblade2.exe");
        if (File.Exists(srcExe))
        {
            CopyAtomic(srcExe, Path.Combine(targetDir, "xenoblade2.exe"));
            updated++;
        }

        // Open-source / runtime DLLs (never touches nvngx_dlss.dll)
        foreach (var dll in UpdatableRuntimeDlls)
        {
            string s = Path.Combine(srcRuntime, dll);
            if (File.Exists(s))
            {
                CopyAtomic(s, Path.Combine(targetDir, dll));
                updated++;
            }
        }

        // Hints: union merge preserving local user hints + adding new release hints
        int addedHints = 0, totalHints = 0;
        string srcHints = Path.Combine(srcRuntime, "xenoblade2.hints");
        if (File.Exists(srcHints))
        {
            string dstHints = Path.Combine(targetDir, "xenoblade2.hints");
            (totalHints, addedHints) = MergeHintsFile(dstHints, srcHints, dstHints);
            if (addedHints > 0 || !File.Exists(dstHints)) updated++;
        }

        // Data overlays base: runtime/<sub...> for portable runtime/, xenoblade2_data/<sub...> for game installDir
        string dstDataBase = isGameInstallDir ? Path.Combine(targetDir, "xenoblade2_data") : targetDir;

        // PC UI native extensions (only DLLs + TSV; never overwrites user theme.bin / theme.png)
        string srcPcUi = Path.Combine(srcRuntime, "pc_ui");
        if (Directory.Exists(srcPcUi))
        {
            string dstPcUi = Path.Combine(dstDataBase, "pc_ui");
            foreach (var f in UpdatablePcUiFiles)
            {
                string s = Path.Combine(srcPcUi, f);
                if (File.Exists(s))
                {
                    CopyAtomic(s, Path.Combine(dstPcUi, f));
                    updated++;
                }
            }
        }

        // Bundled localization packs (pt-br, pt-br-jp only; never deletes or alters custom user mods)
        foreach (var pack in UpdatableBundledPacks)
        {
            string srcPack = Path.Combine(srcRuntime, "localization", pack);
            if (!Directory.Exists(srcPack)) continue;
            string dstPack = Path.Combine(dstDataBase, "localization", pack);
            foreach (var file in Directory.EnumerateFiles(srcPack, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcPack, file);
                CopyAtomic(file, Path.Combine(dstPack, rel));
                updated++;
            }
        }

        // Licenses
        string srcLic = Path.Combine(srcRuntime, "licenses");
        if (Directory.Exists(srcLic))
        {
            string dstLic = Path.Combine(dstDataBase, "licenses");
            foreach (var file in Directory.EnumerateFiles(srcLic, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcLic, file);
                CopyAtomic(file, Path.Combine(dstLic, rel));
                updated++;
            }
        }

        return (updated, addedHints, totalHints);
    }

    public static (int TotalCount, int AddedCount) MergeHintsFile(string existingPath, string incomingPath, string destPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        if (File.Exists(existingPath))
        {
            foreach (var raw in File.ReadLines(existingPath))
            {
                var line = raw.Trim();
                if (line.Length > 0 && seen.Add(line))
                    ordered.Add(line);
            }
        }

        int before = ordered.Count;
        if (File.Exists(incomingPath))
        {
            foreach (var raw in File.ReadLines(incomingPath))
            {
                var line = raw.Trim();
                if (line.Length > 0 && seen.Add(line))
                    ordered.Add(line);
            }
        }

        int added = ordered.Count - before;
        if (added > 0 || !File.Exists(destPath) || !Path.GetFullPath(existingPath).Equals(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
            string tmp = destPath + $".tmp.{Environment.ProcessId}";
            File.WriteAllLines(tmp, ordered, new UTF8Encoding(false));
            File.Move(tmp, destPath, overwrite: true);
        }

        return (ordered.Count, added);
    }

    static string ResolvePackageRoot(string extractedRoot)
    {
        if (File.Exists(Path.Combine(extractedRoot, "Xenoblade2Launcher.exe")) ||
            Directory.Exists(Path.Combine(extractedRoot, "runtime")))
            return extractedRoot;

        // Handle zip with a single top-level wrapper folder
        var subDirs = Directory.GetDirectories(extractedRoot);
        if (subDirs.Length == 1 && Directory.GetFiles(extractedRoot).Length == 0)
            return subDirs[0];

        return extractedRoot;
    }

    static void ReplaceRunningExecutable(string stagedNewExe, string targetExe)
    {
        string dir = Path.GetDirectoryName(targetExe)!;
        string stagedBeside = Path.Combine(dir, $"Xenoblade2Launcher.exe.new.{Environment.ProcessId}");
        string backupOld = targetExe + ".old";

        File.Copy(stagedNewExe, stagedBeside, overwrite: true);
        TryDeleteFile(backupOld);

        try
        {
            File.Move(targetExe, backupOld, overwrite: true);
            File.Move(stagedBeside, targetExe, overwrite: true);
        }
        catch
        {
            // Rollback if moving the new binary into place failed
            try
            {
                if (!File.Exists(targetExe) && File.Exists(backupOld))
                    File.Move(backupOld, targetExe, overwrite: true);
            }
            catch { }
            TryDeleteFile(stagedBeside);
            throw;
        }
    }

    public static void CleanupPreviousBinary(string? currentExePath)
    {
        if (string.IsNullOrWhiteSpace(currentExePath)) return;
        TryDeleteFile(currentExePath + ".old");
    }

    public static void WaitForPreviousProcess(int pid, int timeoutMs = 6000)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(timeoutMs);
        }
        catch { }
    }

    static void CopyAtomic(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + $".tmp.{Environment.ProcessId}";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    static string SanitizeTag(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (char c in tag)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "latest";
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
