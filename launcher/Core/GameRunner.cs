using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XC2.Core;

public sealed record LaunchPlan(
    string ExePath,
    string WorkingDir,
    string Arguments,
    Dictionary<string, string> EnvVars,
    string ProfileSummary
);

public sealed record HardwareRecommendation(
    string RecommendedPresetId,
    string RecommendedPresetName,
    string UpscalerLabel,
    string TierLabel,
    string Reason,
    string? DiskWarning
);

public sealed class GameRunner
{
    Process? activeProc;
    readonly object gate = new();

    public event Action<int, TimeSpan>? Exited;

    public static HardwareRecommendation RecommendPreset(HardwareInfo hw, InstallStatus install)
    {
        bool rtxDlss = hw.IsNvidiaRtx && install.HasDlss;
        string upscalerLabel = rtxDlss
            ? "NVIDIA DLSS 4.5 / DLAA"
            : hw.GpuVendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
                ? "AMD FSR 3.1 / Intel XeSS"
                : "AMD FSR 3.1 (automático)";

        bool highRefresh = hw.MonitorRefreshHz >= 100;
        bool is4k = hw.ScreenWidth >= 3440 || hw.ScreenHeight >= 2160;
        bool is1440p = !is4k && (hw.ScreenWidth >= 2560 || hw.ScreenHeight >= 1440);
        string gpuShort = string.IsNullOrWhiteSpace(hw.GpuName)
            ? "sua placa de vídeo"
            : hw.GpuName.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();

        string presetId;
        string tierLabel;
        string reason;

        if (hw.GpuVramGb > 0 && hw.GpuVramGb <= 6.5)
        {
            presetId = "60_1080_dlaa";
            tierLabel = "Entrada / 60 FPS";
            reason = $"Para {gpuShort} ({hw.GpuVramGb:0.#} GB VRAM), 1080p a 60 FPS entrega ótima nitidez e estabilidade com {upscalerLabel}.";
        }
        else if (hw.GpuVramGb >= 11.0)
        {
            tierLabel = "Alto Desempenho";
            if (is4k)
            {
                presetId = highRefresh ? "120_4k_dlss_qual" : "60_4k_dlaa";
                reason = highRefresh
                    ? $"Para {gpuShort} em monitor 4K {hw.MonitorRefreshHz} Hz, 4K Qualidade a 120 FPS combina nitidez máxima com alta fluidez."
                    : $"Para {gpuShort} em tela 4K {hw.MonitorRefreshHz} Hz, 4K Nativo a 60 FPS entrega o máximo de detalhe visual.";
            }
            else if (is1440p)
            {
                presetId = highRefresh ? "120_1440_dlaa" : "60_1440_dlaa";
                reason = highRefresh
                    ? $"Para {gpuShort} em monitor 1440p {hw.MonitorRefreshHz} Hz, 1440p com {upscalerLabel} a 120 FPS aproveita toda a fluidez da tela."
                    : $"Para {gpuShort} em monitor 1440p {hw.MonitorRefreshHz} Hz, 1440p nativo trava a 60 FPS constantes.";
            }
            else
            {
                presetId = highRefresh ? "120_1080_dlaa" : "60_1080_dlaa";
                reason = highRefresh
                    ? $"Para {gpuShort} em monitor {hw.ScreenWidth}×{hw.ScreenHeight} {hw.MonitorRefreshHz} Hz, 1080p a 120 FPS garante fluidez máxima."
                    : $"Para {gpuShort} em monitor {hw.ScreenWidth}×{hw.ScreenHeight}, 1080p nativo com {upscalerLabel} mantém 60 FPS sólidos.";
            }
        }
        else
        {
            tierLabel = "Equilibrado";
            if (is4k || is1440p)
            {
                presetId = "60_1440_dlaa";
                reason = $"Para {gpuShort} no seu monitor {hw.ScreenWidth}×{hw.ScreenHeight}, 1440p a 60 FPS equilibra alta definição e estabilidade.";
            }
            else if (highRefresh)
            {
                presetId = "120_1080_dlaa";
                reason = $"Para {gpuShort} em monitor 1080p {hw.MonitorRefreshHz} Hz, 1080p a 120 FPS aproveita a taxa de atualização com folga.";
            }
            else
            {
                presetId = "60_1080_dlaa";
                reason = $"Para {gpuShort} em monitor 1080p {hw.MonitorRefreshHz} Hz, 1080p com {upscalerLabel} a 60 FPS oferece a melhor consistência.";
            }
        }

        string? diskWarn = null;
        if (!hw.DiskBudgetOk)
        {
            diskWarn = $"Espaço livre baixo (C: {hw.FreeCDriveGb:0.#} GiB · {hw.InstallDriveLetter} {hw.FreeInstallDriveGb:0.#} GiB). Recomendamos manter ao menos 20 GiB livres em C: e 15 GiB na unidade do jogo.";
        }

        var preset = LaunchPresets.Find(presetId);
        return new HardwareRecommendation(
            RecommendedPresetId: preset.Id,
            RecommendedPresetName: preset.Name,
            UpscalerLabel: upscalerLabel,
            TierLabel: tierLabel,
            Reason: reason,
            DiskWarning: diskWarn
        );
    }

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("xenoblade2").Length > 0; }
        catch { return false; }
    }

    public static string ResolveUpscale(string upscale, InstallStatus install, HardwareInfo hw)
    {
        if (upscale.StartsWith("dlss", StringComparison.OrdinalIgnoreCase) && (!install.HasDlss || !hw.IsNvidiaRtx))
            return upscale.Replace("dlss", "fsr3", StringComparison.OrdinalIgnoreCase);
        return upscale;
    }

    public static void ApplyPresetToIni(
        IniSettings ini,
        InstallStatus install,
        LauncherConfig config,
        HardwareInfo hw,
        string? previousPresetId = null)
    {
        var preset = LaunchPresets.Find(config.Preset);
        if (preset.Id == "custom")
        {
            int fps = config.CustomXc2Fps;
            if (fps == 0)
            {
                ini.Set("Performance", "fps_limit", "0");
                ini.Set("Debug", "vi_refresh", "240");
            }
            else
            {
                int clamped = Math.Clamp(fps, 30, 240);
                ini.Set("Performance", "fps_limit", clamped.ToString());
                ini.Set("Debug", "vi_refresh", (clamped > 60 ? clamped : 60).ToString());
            }
        }
        else if (preset.Id == "original")
        {
            // Authentic Nintendo Switch 2 Edition 3.0.1 console settings:
            // docked 1x native scale, game's own unpinned NVN2 DLSS/DRS, 60 FPS gameplay / 30 FPS cutscenes,
            // and default console filtering/cloud/pass settings without PC overrides.
            ini.Set("Display", "docked", "1");
            ini.Set("Graphics", "resolution", "1");
            ini.Set("Graphics", "upscale", "auto");
            ini.Set("Graphics", "dlss_preset", "auto");
            ini.Set("Graphics", "upscale_mip_bias", "0");
            ini.Set("Graphics", "anisotropy", "0");
            ini.Set("Graphics", "cloud_quality", "0");
            ini.Set("Graphics", "image_follow", "0");
            ini.Set("Graphics", "shadows", "1");
            ini.Set("Graphics", "vsync", "1");
            ini.Set("Graphics", "tonemap", "native");
            ini.Set("Graphics", "bloom_mode", "native");
            ini.Set("Graphics", "contact_shadows", "0");
            ini.Set("Graphics", "env_shading", "0");
            ini.Set("Graphics", "gtao", "0");
            ini.Set("Graphics", "water_caustics", "0");
            ini.Set("Graphics", "ssgi", "0");
            ini.Set("Graphics", "ssr", "0");
            ini.Set("Performance", "fps_limit", "game");
            ini.Set("Debug", "vi_refresh", "60");
        }
        else
        {
            string upscale = ResolveUpscale(preset.Upscale, install, hw);
            ini.Set("Graphics", "resolution", preset.ResolutionScale);
            ini.Set("Graphics", "upscale", upscale);
            if (string.Equals(previousPresetId, "original", StringComparison.OrdinalIgnoreCase) &&
                ini.Get("Graphics", "upscale_mip_bias", "auto") == "0")
            {
                ini.Set("Graphics", "upscale_mip_bias", "auto");
            }
            int fps = preset.TargetFps > 0 ? preset.TargetFps : 60;
            ini.Set("Performance", "fps_limit", fps.ToString());
            ini.Set("Debug", "vi_refresh", (fps > 60 ? fps : 60).ToString());
        }
    }

    public static void SyncPresetToIni(
        InstallStatus install,
        LauncherConfig config,
        HardwareInfo hw,
        string? previousPresetId = null)
    {
        var ini = IniSettings.Load(install.IniPath);
        ApplyPresetToIni(ini, install, config, hw, previousPresetId);
        ini.SaveAtomic();
    }

    public static LaunchPlan BuildPlan(InstallStatus install, LauncherConfig config, HardwareInfo hw)
    {
        var preset = LaunchPresets.Find(config.Preset);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string summary;

        if (preset.Id == "custom")
        {
            int fps = config.CustomXc2Fps;
            if (config.Custom4kSelectiveScale)
            {
                env["SNX_VK_EFFECT_SCALE_SIZES"] = "1024x576:2,512x288:2";
                env["SNX_VK_NATIVE_SIZES"] = "3840x2160,3072x1728";
            }
            var ini = IniSettings.Load(install.IniPath);
            var up = ini.Get("Graphics", "upscale", "dlss_native");
            var res = ini.Get("Graphics", "resolution", "1");
            string fpsTag = fps == 0 ? " · Sem limite" : fps > 0 ? $" · {fps} FPS" : "";
            summary = $"Personalizado · scale {res} · upscale {up}{fpsTag}";
        }
        else if (preset.Id == "original")
        {
            summary = preset.ShortTag;
        }
        else
        {
            if (preset.Selective4kScale)
            {
                env["SNX_VK_EFFECT_SCALE_SIZES"] = "1024x576:2,512x288:2";
                env["SNX_VK_NATIVE_SIZES"] = "3840x2160,3072x1728";
            }
            else if (preset.ResolutionScale is "2.5" or "3.75")
            {
                // Keep 3840x2160 and 3072x1728 UI/present targets at crisp native size instead of 5K/8K
                env["SNX_VK_NATIVE_SIZES"] = "3840x2160,3072x1728";
            }
            summary = preset.Name;
        }

        // Visual & FPS settings are persisted to xenoblade2.ini (SyncPresetToIni) instead of CLI/env
        // overrides so every graphic/display/FPS setting can be hot-reloaded live in-game without restarting.
        return new LaunchPlan(
            ExePath: install.ExePath,
            WorkingDir: install.InstallDir,
            Arguments: "",
            EnvVars: env,
            ProfileSummary: summary
        );
    }

    public int Launch(LaunchPlan plan)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Xenoblade Chronicles 2 já está em execução.");
        if (!File.Exists(plan.ExePath))
            throw new FileNotFoundException("Executável xenoblade2.exe não encontrado.", plan.ExePath);

        var psi = new ProcessStartInfo
        {
            FileName = plan.ExePath,
            WorkingDirectory = plan.WorkingDir,
            Arguments = plan.Arguments,
            UseShellExecute = false
        };
        foreach (var kv in plan.EnvVars)
            psi.EnvironmentVariables[kv.Key] = kv.Value;

        var startedAt = DateTime.UtcNow;
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar xenoblade2.exe.");
        proc.EnableRaisingEvents = true;
        lock (gate) activeProc = proc;

        LauncherLog.Write($"run: started pid={proc.Id} args=\"{plan.Arguments}\" env=[{string.Join(", ", plan.EnvVars.Select(k => $"{k.Key}={k.Value}"))}]");

        proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            var elapsed = DateTime.UtcNow - startedAt;
            LauncherLog.Write($"run: exited pid={proc.Id} code={code} elapsed={elapsed:hh\\:mm\\:ss}");
            lock (gate) { if (activeProc == proc) activeProc = null; }
            Exited?.Invoke(code, elapsed);
        };
        return proc.Id;
    }

    public bool BringGameToFront()
    {
        try
        {
            var procs = Process.GetProcessesByName("xenoblade2");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static string ReadLogTail(string path, int maxLines = 120)
    {
        if (!File.Exists(path)) return "(Arquivo ainda não gerado)";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new Queue<string>(maxLines + 1);
            while (sr.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines) lines.Dequeue();
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex) { return $"(Erro ao ler log: {ex.Message})"; }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}
