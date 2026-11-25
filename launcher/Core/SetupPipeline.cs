using System.Diagnostics;
using System.Text;

namespace XC2.Core;

public sealed record SetupPreflightResult(
    NupInspection Nup,
    string TargetInstallDir,
    long FreeDiskBytes,
    long RequiredBytesWithReserve,
    bool HasEnoughDiskSpace,
    string? CompilerExePath,
    bool HasCompilerExe,
    bool TargetAlreadyHasRomfs,
    bool TargetAlreadyHasAot,
    bool ReadyToRun,
    string? BlockingReason
);

public sealed record SetupRequest(
    string NupPath,
    string TargetInstallDir,
    string ProjectRoot,
    bool ForceRecompileAot = false,
    bool CopyBundledOverlays = true,
    long ReserveBytes = 15L * 1024 * 1024 * 1024 // STORAGE_POLICY.md: keep >= 15 GiB free
);

public sealed record SetupProgress(
    string Stage,
    int StageIndex,
    int StageCount,
    double Percent,
    string Detail,
    string? LogLine = null,
    bool IsDone = false,
    bool IsError = false
);

public sealed record SetupResult(
    bool Success,
    string TargetInstallDir,
    int BaseFilesExtracted,
    int AddonsBuilt,
    bool AotCompiled,
    string DisplayVersion,
    double ElapsedSeconds,
    string Summary
);

/// <summary>First-Run installation pipeline (.nup validation -> extraction -> Add-On .romfs -> control.nacp -> AOT build).</summary>
public interface ISetupPipeline
{
    string StatusName { get; }
    bool IsAvailable { get; }
    SetupPreflightResult Inspect(string nupPath, string targetInstallDir, string projectRoot);
    Task<SetupResult> RunAsync(SetupRequest request, Action<SetupProgress>? onProgress, CancellationToken ct);
}

public sealed class NativeSetupPipeline : ISetupPipeline
{
    static readonly string[] RuntimeDlls =
    [
        "libc++.dll", "libffi-8.dll", "libiconv-2.dll", "libLLVM-23.dll",
        "libunwind.dll", "libxml2-16.dll", "libzstd.dll", "zlib1.dll",
        "snx_dlss.dll", "nvngx_dlss.dll"
    ];

    public string StatusName => "Pipeline Nativo First-Run (.nup SHA-256 + RomFS DLC + Recompilação AOT)";
    public bool IsAvailable => true;

    public SetupPreflightResult Inspect(string nupPath, string targetInstallDir, string projectRoot)
    {
        var searchDirs = GetExtraSearchDirs(targetInstallDir, projectRoot);
        using var archive = NupArchive.Open(nupPath, searchDirs);
        var nup = archive.Inspect();

        long freeDisk = GetFreeDiskBytes(targetInstallDir);
        bool hasRomfs = File.Exists(Path.Combine(targetInstallDir, "xenoblade2_data", "romfs", "bf2.ard"));
        bool hasAot = File.Exists(Path.Combine(targetInstallDir, "xenoblade2_code.dll"));

        long reserve = 15L * 1024 * 1024 * 1024;
        long neededOutput = hasRomfs ? 1024L * 1024 * 1024 : nup.EstimatedOutputBytes;
        long reqTotal = neededOutput + reserve;
        bool diskOk = freeDisk >= reqTotal;

        string? compiler = FindCompilerExe(targetInstallDir, projectRoot);
        bool hasCompiler = !string.IsNullOrEmpty(compiler);

        string? block = null;
        if (!nup.IsXenoblade2Switch2)
            block = $"Title ID incompatível ({nup.BaseTitleIdHex}). Esperado Xenoblade Chronicles 2 Switch 2 Edition ({NupArchive.Xc2Switch2TitleId:x16}).";
        else if (!nup.ZstdAvailable && nup.TotalUncompressedBytes > 0)
            block = "libzstd.dll não encontrada para descompressão do pacote .nup.";
        else if (!diskOk)
            block = $"Espaço insuficiente na unidade de destino (livre: {freeDisk / 1073741824.0:F1} GiB; necessário com reserva de 15 GiB: {reqTotal / 1073741824.0:F1} GiB).";
        else if (!hasCompiler && !hasAot)
            block = "Executável do runtime/recompilador (xenoblade2.exe ou scarletnx.exe) não encontrado para compilação AOT.";

        return new SetupPreflightResult(
            Nup: nup,
            TargetInstallDir: targetInstallDir,
            FreeDiskBytes: freeDisk,
            RequiredBytesWithReserve: reqTotal,
            HasEnoughDiskSpace: diskOk,
            CompilerExePath: compiler,
            HasCompilerExe: hasCompiler,
            TargetAlreadyHasRomfs: hasRomfs,
            TargetAlreadyHasAot: hasAot,
            ReadyToRun: block == null,
            BlockingReason: block
        );
    }

    public async Task<SetupResult> RunAsync(SetupRequest request, Action<SetupProgress>? onProgress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        const int totalStages = 6;
        void Report(string stage, int idx, double pct, string detail, string? log = null)
        {
            if (log != null) LauncherLog.Write($"setup[{idx}/{totalStages}]: {log}");
            onProgress?.Invoke(new SetupProgress(stage, idx, totalStages, Math.Clamp(pct, 0, 100), detail, log));
        }

        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de executar a instalação inicial.");

        // Stage 1: Preflight & TOC SHA-256 verification
        Report("Verificando pacote .nup", 1, 2, "Validando cabeçalho NUP v1 e SHA-256 do TOC...", $"Inspecionando {request.NupPath}");
        var pre = Inspect(request.NupPath, request.TargetInstallDir, request.ProjectRoot);
        if (!pre.ReadyToRun)
            throw new InvalidOperationException(pre.BlockingReason ?? "Pré-verificação do .nup falhou.");

        long freeCheck = GetFreeDiskBytes(request.TargetInstallDir);
        long estNeed = (pre.TargetAlreadyHasRomfs ? 1024L * 1024 * 1024 : pre.Nup.EstimatedOutputBytes) + request.ReserveBytes;
        if (freeCheck < estNeed)
            throw new InvalidOperationException($"Espaço livre insuficiente para manter a reserva de segurança ({request.ReserveBytes / 1073741824.0:F0} GiB).");

        string targetDir = Path.GetFullPath(request.TargetInstallDir);
        string dataDir = Path.Combine(targetDir, "xenoblade2_data");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "save", "user"));

        var searchDirs = GetExtraSearchDirs(targetDir, request.ProjectRoot);
        using var archive = NupArchive.Open(request.NupPath, searchDirs);

        long totalWorkBytes = Math.Max(1, pre.Nup.TotalUncompressedBytes);
        long doneBytes = 0;
        long lastReportTick = Environment.TickCount64;
        double maxReportedPct = 0;

        void ReportMonotonic(string stage, int idx, double pct, string detail, string? log = null)
        {
            double clamped = Math.Clamp(pct, 0, 100);
            if (clamped > maxReportedPct) maxReportedPct = clamped;
            Report(stage, idx, maxReportedPct, detail, log);
        }

        void OnBytes(string stageName, int stageIdx, double startPct, double spanPct, string curItem, long delta)
        {
            doneBytes += delta;
            long now = Environment.TickCount64;
            if (now - lastReportTick >= 150)
            {
                lastReportTick = now;
                double frac = Math.Clamp((double)doneBytes / totalWorkBytes, 0, 1);
                ReportMonotonic(stageName, stageIdx, startPct + frac * spanPct, curItem);
            }
        }

        // Stage 2: Extract Base Game Pkg[0] (exefs + romfs + exefs_host_bypass) + clean-room exefs/subsdk1 stub
        Report("Extraindo jogo base", 2, 5, "Extraindo ExeFS e RomFS com conferência SHA-256...", $"Extraindo Pkg[0] ({pre.Nup.BaseTitleIdHex}) -> {dataDir}");
        var (baseFiles, baseBytes) = await Task.Run(() => archive.ExtractBasePackage(
            dataDir,
            (rel, delta) => OnBytes("Extraindo jogo base", 2, 5, 44, $"Extraindo {rel}", delta),
            ct), ct);

        string bypassSubsdk1 = Path.Combine(dataDir, "exefs_host_bypass", "subsdk1");
        string exefsSubsdk1 = Path.Combine(dataDir, "exefs", "subsdk1");
        if (File.Exists(bypassSubsdk1))
        {
            Report("Extraindo jogo base", 2, 49, "Gerando stub clean-room exefs/subsdk1 (dlss.nss / thunks NVN2)...",
                "Gerando stub clean-room exefs/subsdk1 a partir de exefs_host_bypass/subsdk1...");
            var stub = await Task.Run(() => Subsdk1StubBuilder.BuildFromBypass(bypassSubsdk1, exefsSubsdk1), ct);
            Report("Extraindo jogo base", 2, 50,
                $"{baseFiles:N0} arquivos verificados ({baseBytes / 1073741824.0:F2} GiB) + stub subsdk1 ({stub.ExportedSymbolCount} símbolos).",
                $"Pkg[0] concluído: {baseFiles:N0} arquivos ({baseBytes / 1073741824.0:F2} GiB); subsdk1 stub {stub.SizeBytes} B ({stub.ExportedSymbolCount} símbolos, sha256={stub.Sha256Hex[..16]}...).");
        }
        else
        {
            Report("Extraindo jogo base", 2, 50, $"{baseFiles:N0} arquivos verificados ({baseBytes / 1073741824.0:F2} GiB).",
                $"Pkg[0] concluído: {baseFiles:N0} arquivos ({baseBytes / 1073741824.0:F2} GiB).");
        }

        // Stage 3: Extract & verify control.nacp
        Report("Instalando metadados (control.nacp)", 3, 52, "Extraindo control.nacp (Switch 2 Edition)...", "Lendo control.nacp...");
        string displayVersion = pre.Nup.DisplayVersion;
        try
        {
            var ctrl = archive.ReadControlNacp();
            displayVersion = ctrl.DisplayVersion;
            string nacpOut = Path.Combine(dataDir, "control.nacp");
            string nacpTmp = nacpOut + ".tmp";
            File.WriteAllBytes(nacpTmp, ctrl.RawNacp);
            File.Move(nacpTmp, nacpOut, overwrite: true);
            Report("Instalando metadados (control.nacp)", 3, 54, $"control.nacp v{ctrl.DisplayVersion} instalado ({ctrl.VerificationNote}).",
                $"control.nacp v{ctrl.DisplayVersion} SHA-256={ctrl.Sha256Hex[..16]}...");
        }
        catch (Exception ex)
        {
            Report("Instalando metadados (control.nacp)", 3, 54, $"Aviso: control.nacp opcional não extraído ({ex.Message}).",
                $"Aviso control.nacp: {ex.Message}");
        }

        // Stage 4: Build deterministic Horizon .romfs images for Add-On packages Pkg[1..N]
        int addonsBuilt = 0;
        int addonTotal = Math.Max(0, (int)archive.PackageCount - 1);
        string addonsDir = Path.Combine(dataDir, "addons");
        Directory.CreateDirectory(addonsDir);

        for (int pi = 1; pi < archive.PackageCount; pi++)
        {
            ct.ThrowIfCancellationRequested();
            ulong tid = archive.GetPackageTitleId(pi);
            string tidHex = $"{tid:x16}";
            string outRomfs = Path.Combine(addonsDir, $"{tidHex}.romfs");
            double stagePct = 55 + (addonTotal > 0 ? (double)(pi - 1) / addonTotal * 20.0 : 20.0);

            if (File.Exists(outRomfs) && new FileInfo(outRomfs).Length >= 0x200)
            {
                addonsBuilt++;
                Report("Construindo expansões (.romfs)", 4, stagePct, $"Expansão {tidHex}.romfs já presente (preservada).",
                    $"Addon {pi}/{addonTotal} {tidHex}.romfs já presente.");
                continue;
            }

            ReportMonotonic("Construindo expansões (.romfs)", 4, stagePct, $"Gerando {tidHex}.romfs ({pi}/{addonTotal})...",
                $"Construindo addon {pi}/{addonTotal}: {tidHex}.romfs");
            int localPi = pi;
            var res = await Task.Run(() => archive.BuildAddonRomfs(
                localPi,
                outRomfs,
                delta => OnBytes("Construindo expansões (.romfs)", 4, 55, 20, $"Empacotando {tidHex}.romfs", delta),
                ct), ct);
            addonsBuilt++;
            ReportMonotonic("Construindo expansões (.romfs)", 4, 55 + (double)pi / Math.Max(1, addonTotal) * 20.0,
                $"{tidHex}.romfs pronto ({res.FileCount:N0} arquivos, {res.SizeBytes / 1048576.0:F1} MiB).",
                $"Addon {tidHex}.romfs OK: {res.FileCount} arquivos, {res.SizeBytes / 1048576.0:F1} MiB, sha256={res.Sha256Hex[..16]}...");
        }

        // Stage 5: Provision runtime executable, DLLs, AOT hints, PC UI extensions, default xenoblade2.ini
        Report("Preparando runtime e overlays", 5, 76, "Verificando executável, DLLs, hints AOT, PC UI e xenoblade2.ini...",
            "Provisionando runtime e arquivos auxiliares...");
        ProvisionRuntimeFiles(targetDir, request.ProjectRoot, pre.CompilerExePath, request.CopyBundledOverlays);
        EnsureDefaultIni(Path.Combine(targetDir, "xenoblade2.ini"), dataDir, targetDir);

        // Stage 6: AOT Recompilation (xenoblade2_code.dll)
        string aotPath = Path.Combine(targetDir, "xenoblade2_code.dll");
        bool aotCompiled = false;
        if (request.ForceRecompileAot || !File.Exists(aotPath))
        {
            // Prefer an external runtime compiler (e.g. runtime/xenoblade2.exe) so targetDir/xenoblade2.exe
            // is never locked while running; in-place xenoblade2.exe is also supported as fallback.
            string compilerExe = (!string.IsNullOrEmpty(pre.CompilerExePath) && File.Exists(pre.CompilerExePath))
                ? pre.CompilerExePath
                : (File.Exists(Path.Combine(targetDir, "xenoblade2.exe"))
                    ? Path.Combine(targetDir, "xenoblade2.exe")
                    : throw new FileNotFoundException("Executável para recompilação AOT não encontrado."));

            string exefsDir = Path.Combine(dataDir, "exefs");
            if (!Directory.Exists(exefsDir) || !File.Exists(Path.Combine(exefsDir, "main")))
                throw new FileNotFoundException("Pasta exefs/main não encontrada para recompilação AOT.", exefsDir);

            Report("Recompilando código nativo (AOT)", 6, 80, "Compilando binário Ounce64 → x86-64 AVX2/FMA (xenoblade2_code.dll)...",
                $"Executando AOT: \"{compilerExe}\" -r \"{exefsDir}\" xenoblade2 --outdir \"{targetDir}\" --fp-fast --relax-stack-volatile");

            int aotLineCount = 0;
            await RunAotCompilerAsync(compilerExe, exefsDir, targetDir, line =>
            {
                aotLineCount++;
                double pct = Math.Min(98, 80 + aotLineCount * 0.4);
                Report("Recompilando código nativo (AOT)", 6, pct, line, line);
            }, ct);

            if (!File.Exists(aotPath))
                throw new InvalidOperationException("O recompilador terminou mas xenoblade2_code.dll não foi gerado.");
            aotCompiled = true;
        }
        else
        {
            Report("Recompilando código nativo (AOT)", 6, 95, "xenoblade2_code.dll já presente (recompilação ignorada).",
                "xenoblade2_code.dll já existente.");
        }

        // Verify final state
        var status = GameInstall.Inspect(targetDir, request.ProjectRoot, computeHashes: false);
        sw.Stop();
        string summary = status.IsReadyToPlay
            ? $"Instalação concluída em {sw.Elapsed.TotalSeconds:F1}s: v{displayVersion}, {baseFiles:N0} arquivos base, {addonsBuilt} expansões."
            : $"Extração concluída em {sw.Elapsed.TotalSeconds:F1}s, mas verifique componentes pendentes.";

        onProgress?.Invoke(new SetupProgress("Concluído", totalStages, totalStages, 100, summary, summary, IsDone: true));
        return new SetupResult(status.IsReadyToPlay, targetDir, baseFiles, addonsBuilt, aotCompiled, displayVersion, sw.Elapsed.TotalSeconds, summary);
    }

    static async Task RunAotCompilerAsync(
        string compilerExe,
        string exefsDir,
        string targetDir,
        Action<string>? onLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = compilerExe,
            WorkingDirectory = targetDir,
            Arguments = $"-r \"{exefsDir}\" xenoblade2 --outdir \"{targetDir}\" --fp-fast --relax-stack-volatile",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o recompilador AOT.");
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        async Task Pump(StreamReader sr)
        {
            while (await sr.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    onLine?.Invoke(line.Trim());
            }
        }

        var t1 = Pump(proc.StandardOutput);
        var t2 = Pump(proc.StandardError);
        await Task.WhenAll(t1, t2, proc.WaitForExitAsync(ct));

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Recompilação AOT falhou com código {proc.ExitCode}.");
    }

    static void ProvisionRuntimeFiles(string targetDir, string projectRoot, string? compilerExe, bool copyOverlays)
    {
        var templateDirs = GetExtraSearchDirs(targetDir, projectRoot)
            .Where(d => Directory.Exists(d) && !PathsEqual(d, targetDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string targetExe = Path.Combine(targetDir, "xenoblade2.exe");
        if (!File.Exists(targetExe) && !string.IsNullOrEmpty(compilerExe) && File.Exists(compilerExe))
            CopyAtomic(compilerExe, targetExe);

        foreach (var dll in RuntimeDlls)
        {
            string dst = Path.Combine(targetDir, dll);
            if (File.Exists(dst)) continue;
            foreach (var srcDir in templateDirs)
            {
                string src = Path.Combine(srcDir, dll);
                if (File.Exists(src)) { CopyAtomic(src, dst); break; }
            }
        }

        // Seed pre-discovered indirect branch AOT hints so the first AOT compile reaches full coverage
        string targetHints = Path.Combine(targetDir, "xenoblade2.hints");
        if (!File.Exists(targetHints))
        {
            foreach (var srcDir in templateDirs)
            {
                string src = Path.Combine(srcDir, "xenoblade2.hints");
                if (File.Exists(src)) { CopyAtomic(src, targetHints); break; }
            }
        }

        if (!copyOverlays) return;

        string targetData = Path.Combine(targetDir, "xenoblade2_data");
        foreach (var sub in new[] { "pc_ui", "branding", "licenses", Path.Combine("localization", "pt-br"), Path.Combine("localization", "pt-br-jp") })
        {
            string dstSub = Path.Combine(targetData, sub);
            if (Directory.Exists(dstSub) && Directory.EnumerateFiles(dstSub, "*", SearchOption.AllDirectories).Any())
                continue;
            foreach (var srcDir in templateDirs)
            {
                string[] srcCandidates =
                [
                    Path.Combine(srcDir, "xenoblade2_data", sub),
                    Path.Combine(srcDir, sub)
                ];
                string? found = srcCandidates.FirstOrDefault(s =>
                    Directory.Exists(s) && Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories).Any());
                if (found != null)
                {
                    CopyDirAtomic(found, dstSub);
                    break;
                }
            }
        }
    }

    static void EnsureDefaultIni(string iniPath, string dataDir, string targetDir)
    {
        if (File.Exists(iniPath)) return;
        bool hasPtBr = Directory.Exists(Path.Combine(dataDir, "localization", "pt-br"));
        bool hasDlss = File.Exists(Path.Combine(targetDir, "nvngx_dlss.dll")) &&
                       File.Exists(Path.Combine(targetDir, "snx_dlss.dll"));
        var hw = SystemProbe.Probe(targetDir);
        bool useDlss = hw.IsNvidiaRtx && hasDlss;
        bool highRefresh = hw.MonitorRefreshHz >= 100;
        bool highResGpu = hw.GpuVramGb >= 11.0 && (hw.ScreenWidth >= 2560 || hw.ScreenHeight >= 1440);
        int targetFps = highRefresh ? 120 : 60;

        var sb = new StringBuilder();
        sb.AppendLine("; Xenoblade Chronicles 2 — Switch 2 Edition 3.0.1 (PC Edition)");
        sb.AppendLine("[Display]");
        sb.AppendLine($"resource_pack={(hasPtBr ? "pt-br" : "")}");
        sb.AppendLine("docked=1");
        sb.AppendLine("window_mode=borderless");
        sb.AppendLine("output_resolution=desktop");
        sb.AppendLine($"language={(hasPtBr ? "17" : "1")}");
        sb.AppendLine();
        sb.AppendLine("[Graphics]");
        sb.AppendLine("quality_preset=custom");
        sb.AppendLine($"resolution={(highResGpu ? "2.5" : "1.875")}");
        sb.AppendLine($"upscale={(useDlss ? "dlss_native" : "auto")}");
        sb.AppendLine("dlss_preset=auto");
        sb.AppendLine("upscale_mip_bias=auto");
        sb.AppendLine("upscale_sharpness=0.1");
        sb.AppendLine("anisotropy=16");
        sb.AppendLine("shadows=1");
        sb.AppendLine("shadow_soften=1");
        sb.AppendLine("vsync=1");
        sb.AppendLine("low_latency=1");
        sb.AppendLine("cloud_quality=0");
        sb.AppendLine("tonemap=gt7");
        sb.AppendLine("bloom_mode=physical");
        sb.AppendLine("contact_shadows=1");
        sb.AppendLine("env_shading=1");
        sb.AppendLine("gtao=1");
        sb.AppendLine("water_caustics=1");
        sb.AppendLine("ssgi=1");
        sb.AppendLine("ssr=1");
        sb.AppendLine();
        sb.AppendLine("[Performance]");
        sb.AppendLine($"fps_limit={targetFps}");
        sb.AppendLine("show_fps=0");
        sb.AppendLine();
        sb.AppendLine("[Input]");
        sb.AppendLine("amiibo=0");
        sb.AppendLine();
        sb.AppendLine("[Network]");
        sb.AppendLine("discord_presence=1");
        sb.AppendLine();
        sb.AppendLine("[Debug]");
        sb.AppendLine($"vi_refresh={targetFps}");
        sb.AppendLine("native_sched=10");

        string tmp = iniPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, iniPath, overwrite: true);
    }

    static void CopyAtomic(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + $".tmp.{Environment.ProcessId}";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    static void CopyDirAtomic(string srcDir, string dstDir)
    {
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            CopyAtomic(file, Path.Combine(dstDir, rel));
        }
    }

    static bool PathsEqual(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static string[] GetExtraSearchDirs(string targetDir, string projectRoot)
    {
        var dirs = new List<string>();
        var envRuntime = Environment.GetEnvironmentVariable("XC2_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(envRuntime)) dirs.Add(envRuntime);
        dirs.Add(Path.Combine(projectRoot, "runtime"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "runtime"));
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(targetDir);
        dirs.Add(Path.Combine(projectRoot, "games"));
        dirs.Add(Path.Combine(projectRoot, "work", "test"));
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string? FindCompilerExe(string targetDir, string projectRoot)
    {
        var candidates = new List<string>();
        var envRuntime = Environment.GetEnvironmentVariable("XC2_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(envRuntime))
        {
            candidates.Add(Path.Combine(envRuntime, "xenoblade2.exe"));
            candidates.Add(Path.Combine(envRuntime, "scarletnx.exe"));
        }
        candidates.Add(Path.Combine(projectRoot, "runtime", "xenoblade2.exe"));
        candidates.Add(Path.Combine(projectRoot, "runtime", "scarletnx.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "xenoblade2.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "scarletnx.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "xenoblade2.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "scarletnx.exe"));
        candidates.Add(Path.Combine(targetDir, "xenoblade2.exe"));
        candidates.Add(Path.Combine(projectRoot, "games", "xenoblade2.exe"));
        candidates.Add(Path.Combine(projectRoot, "work", "test", "xenoblade2.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    static long GetFreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
                return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { }
        return 0;
    }
}
