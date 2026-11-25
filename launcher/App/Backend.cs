using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XC2.Core;
using XC2.Launcher.Host;

namespace XC2.Launcher;

public sealed class Backend
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly WinHost host;
    readonly Content content;
    readonly string projectRoot;
    readonly LauncherConfig config;
    readonly GameRunner runner = new();
    readonly ISetupPipeline setupPipeline = new NativeSetupPipeline();
    CancellationTokenSource? setupCts;
    bool setupRunning;
    SetupPreflightResult? lastSetupPreflight;

    InstallStatus install;
    HardwareInfo hardware;
    GameAudio audio;
    bool playing;
    string activeLaunchProfile = "";
    DateTime playingSince;

    public Backend(WinHost host, Content content, string projectRoot, LauncherConfig config)
    {
        this.host = host;
        this.content = content;
        this.projectRoot = projectRoot;
        this.config = config;

        var installDir = GameInstall.ResolveInstallDir(config, projectRoot);
        install = GameInstall.Inspect(installDir, projectRoot, computeHashes: false);
        hardware = SystemProbe.Probe(install.InstallDir);
        audio = new GameAudio(install.RomfsDir, LauncherPaths.MediaCacheDir);

        BindContentProviders();
        runner.Exited += OnGameExited;
    }

    void BindContentProviders()
    {
        content.VisualKitDir = install.VisualKitDir;
        content.BgmProvider = id => audio.GetBgmOgg(id);
        content.SfxProvider = id => audio.GetSfxWav(id);
        content.PackThumbProvider = ResolvePackThumbnail;
    }

    (byte[] Data, string Mime)? ResolvePackThumbnail(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId) || packId.Contains("..") || packId.Contains('/') || packId.Contains('\\'))
            return null;
        var dir = Path.Combine(install.LocalizationDir, packId);
        if (!Directory.Exists(dir)) return null;
        foreach (var name in new[] { "preview.png", "preview.jpg", "thumb.png", "thumb.jpg", "cover.png", "cover.jpg", "banner.png", "banner.jpg" })
        {
            var fp = Path.Combine(dir, name);
            if (File.Exists(fp))
            {
                var mime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                return (File.ReadAllBytes(fp), mime);
            }
        }
        return null;
    }

    public void OnMessage(string rawJson, string[] droppedFiles)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(rawJson); }
        catch { return; }
        if (msg == null) return;

        var type = (string?)msg["t"] ?? "";
        switch (type)
        {
            case "ready":
                Task.Run(HandleReady);
                return;
            case "win":
                switch ((string?)msg["a"])
                {
                    case "min": host.Minimize(); break;
                    case "max": host.ToggleMaximize(); break;
                    case "close": host.Close(); break;
                }
                return;
            case "drop":
                if (droppedFiles.Length > 0)
                    Task.Run(() => Guard("drop", () => HandleDrop(droppedFiles[0], (string?)msg["context"] ?? "")));
                return;
            case "cmd":
                var cmd = (string?)msg["c"] ?? "";
                Task.Run(() => Guard(cmd, () => DispatchCommand(cmd, msg)));
                return;
        }
    }

    void Guard(string name, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            LauncherLog.Write($"cmd '{name}' failed: {ex.Message}");
            Toast(ex.Message, "error", isError: true);
        }
    }

    void HandleReady()
    {
        audio.WarmupAsync(config.MusicTrack);
        playing = GameRunner.IsGameRunning();
        PushFullState(includeHashes: false);
        // Compute binary SHA-256 hashes asynchronously so initial render is instant
        Task.Run(() =>
        {
            install = GameInstall.Inspect(install.InstallDir, projectRoot, computeHashes: true);
            PushState("install", install);
        });
    }

    void DispatchCommand(string cmd, JsonNode m)
    {
        switch (cmd)
        {
            case "refresh":
                install = GameInstall.Inspect(install.InstallDir, projectRoot, computeHashes: true);
                hardware = SystemProbe.Probe(install.InstallDir);
                PushFullState(includeHashes: true);
                Toast("Estado atualizado.", "cursor");
                break;

            case "preset.set":
                var prevPresetId = config.Preset;
                var presetId = (string?)m["id"] ?? "120_1440_dlaa";
                var pickedPreset = LaunchPresets.Find(presetId);
                config.Preset = pickedPreset.Id;
                if (pickedPreset.TargetFps > 0) config.CustomXc2Fps = pickedPreset.TargetFps;
                config.Save();
                if (install.IsReadyToPlay || File.Exists(install.IniPath))
                    GameRunner.SyncPresetToIni(install, config, hardware, prevPresetId);
                PushState("config", config);
                PushIni();
                PushState("launchPlan", GameRunner.BuildPlan(install, config, hardware));
                break;

            case "ini.refresh":
                PushIni();
                break;

            case "play":
                LaunchGame();
                break;

            case "game.focus":
                if (!runner.BringGameToFront())
                    Toast("Não foi possível localizar a janela do jogo.", "error", isError: true);
                break;

            case "theme.set":
                var accent = (string?)m["accent"] ?? "aegis";
                config.AccentTheme = accent is "aegis" or "pyra" or "mythra" or "torna" ? accent : "aegis";
                config.Save();
                PushState("config", config);
                break;

            case "sound.cycle":
                config.SoundLevel = config.SoundLevel switch
                {
                    "off" => "low",
                    "low" => "med",
                    "med" => "high",
                    _ => "off"
                };
                config.Save();
                PushState("config", config);
                break;

            case "sound.set":
                config.SoundLevel = (string?)m["level"] ?? "low";
                config.Save();
                PushState("config", config);
                break;

            case "music.toggle":
                if (config.SoundLevel == "off")
                {
                    config.SoundLevel = "low";
                    config.MusicEnabled = true;
                }
                else
                {
                    config.MusicEnabled = !config.MusicEnabled;
                }
                config.Save();
                PushState("config", config);
                break;

            case "music.track":
                var trackId = (string?)m["id"] ?? "m01";
                config.MusicTrack = trackId;
                config.Save();
                audio.WarmupAsync(trackId);
                PushState("config", config);
                break;

            case "saves.list":
                PushSaves();
                break;

            case "saves.backup":
                var label = (string?)m["label"];
                var smBkp = new SaveManager(install.SaveUserDir, projectRoot);
                var created = smBkp.CreateBackup(label);
                PushSaves();
                Toast($"Cópia de segurança criada: {created.Label}");
                break;

            case "saves.restore":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de restaurar um save.");
                var path = (string?)m["path"] ?? "";
                var smRes = new SaveManager(install.SaveUserDir, projectRoot);
                var restored = smRes.RestoreFromFile(path);
                PushSaves();
                Toast($"Save restaurado: {restored.LandmarkName}. Uma cópia do anterior foi guardada.");
                break;

            case "saves.import.pickFile":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de importar um save.");
                var pickedSav = host.PickFile("Selecionar save de Xenoblade Chronicles 2", "Save XC2 (*.sav)", "*.sav");
                if (pickedSav != null)
                {
                    var smImp = new SaveManager(install.SaveUserDir, projectRoot);
                    var imported = smImp.RestoreFromFile(pickedSav);
                    PushSaves();
                    Toast($"Save importado: {imported.LandmarkName}.");
                }
                break;

            case "saves.import.pickFolder":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de importar um save.");
                var pickedDir = host.PickFolder("Selecionar pasta com o save do jogo");
                if (pickedDir != null)
                {
                    var smDir = new SaveManager(install.SaveUserDir, projectRoot);
                    var imported = smDir.RestoreFromFile(pickedDir);
                    PushSaves();
                    Toast($"Save importado: {imported.LandmarkName}.");
                }
                break;

            case "saves.export":
                var outDir = host.PickFolder("Escolher pasta de destino para exportar o save atual");
                if (outDir != null)
                {
                    var smExp = new SaveManager(install.SaveUserDir, projectRoot);
                    smExp.ExportActiveToFolder(outDir);
                    Toast("Save exportado com sucesso.");
                }
                break;

            case "mods.list":
                PushMods();
                break;

            case "mods.activate":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de trocar pacotes de tradução ou mods.");
                var packId = (string?)m["id"] ?? "";
                new ModManager(install).SetActiveResourcePack(packId);
                PushMods();
                PushIni();
                PushSaves();
                Toast(string.IsNullOrEmpty(packId) ? "Recursos originais do jogo restaurados." : "Pacote ativado com sucesso.");
                break;

            case "mods.install.pickZip":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de instalar mods.");
                var zip = host.PickFile("Selecionar pacote de tradução ou mod (.zip)", "Arquivo ZIP (*.zip)", "*.zip");
                if (zip != null)
                {
                    var installed = new ModManager(install).InstallDroppedPack(zip);
                    PushMods();
                    Toast($"'{installed.Name}' instalado com sucesso.");
                }
                break;

            case "mods.install.pickFolder":
                if (playing || GameRunner.IsGameRunning())
                    throw new InvalidOperationException("Feche o jogo antes de instalar mods.");
                var modDir = host.PickFolder("Selecionar pasta do mod ou tradução");
                if (modDir != null)
                {
                    var installed = new ModManager(install).InstallDroppedPack(modDir);
                    PushMods();
                    Toast($"'{installed.Name}' instalado com sucesso.");
                }
                break;

            case "settings.save":
                SaveSettings(m);
                break;

            case "settings.resetRecommended":
                ResetRecommendedSettings();
                break;

            case "shaders.rebuild":
                RebuildShaderCache();
                break;

            case "steam.list":
                PushSteam();
                break;

            case "steam.add":
                HandleSteamAdd((string?)m["userId"] ?? "", (bool?)m["launchViaLauncher"] ?? true);
                break;

            case "steam.remove":
                HandleSteamRemove((string?)m["userId"] ?? "");
                break;

            case "shortcut.desktop":
                var exe = Environment.ProcessPath ?? Path.Combine(projectRoot, "Xenoblade2Launcher.exe");
                var ico = install.VisualKitDir != null
                    ? Path.Combine(install.VisualKitDir, "01_atalhos", "xc2_pc_cristal.ico")
                    : null;
                var lnk = SteamIntegration.CreateDesktopShortcut(exe, ico);
                Toast("Atalho criado na Área de Trabalho.");
                break;

            case "system.logs":
                PushLogs();
                break;

            case "system.switchInstall":
                var targetDir = (string?)m["path"] ?? "";
                SwitchInstallDir(targetDir);
                break;

            case "system.pickInstall":
                var customDir = host.PickFolder("Selecionar pasta de instalação contendo xenoblade2.exe");
                if (customDir != null) SwitchInstallDir(customDir);
                break;

            case "open.folder":
                OpenFolder((string?)m["what"] ?? "install");
                break;

            case "open.log":
                OpenFileInShell(install.LogPath);
                break;

            case "open.ini":
                OpenFileInShell(install.IniPath);
                break;

            case "setup.pickNup":
                HandleSetupPickNup();
                break;

            case "setup.inspect":
                HandleSetupInspect((string?)m["nupPath"] ?? "", (string?)m["targetDir"]);
                break;

            case "setup.start":
                HandleSetupStart(
                    (string?)m["nupPath"] ?? "",
                    (string?)m["targetDir"],
                    (bool?)m["forceRecompileAot"] ?? false
                );
                break;

            case "setup.cancel":
                HandleSetupCancel();
                break;

            case "onboarding.complete":
                config.OnboardingCompleted = true;
                config.OnboardingVersion = 1;
                config.Save();
                PushState("config", config);
                break;
        }
    }

    void LaunchGame()
    {
        if (!install.IsReadyToPlay)
            throw new InvalidOperationException("Instalação incompleta: verifique xenoblade2.exe, xenoblade2_code.dll e romfs.");

        GameRunner.SyncPresetToIni(install, config, hardware);
        var plan = GameRunner.BuildPlan(install, config, hardware);
        activeLaunchProfile = plan.ProfileSummary;
        playingSince = DateTime.Now;
        runner.Launch(plan);
        playing = true;

        PushIni();
        PushState("playing", new
        {
            running = true,
            profile = activeLaunchProfile,
            startedAt = playingSince.ToString("HH:mm:ss"),
            args = plan.Arguments
        });
        Send(new JsonObject { ["t"] = "go", ["s"] = "playing" });

        if (config.CloseAfterLaunch)
            host.Close();
        else if (config.MinimizeOnLaunch)
            host.Minimize();
    }

    void OnGameExited(int exitCode, TimeSpan elapsed)
    {
        playing = false;
        PushState("playing", new { running = false, profile = activeLaunchProfile, lastExitCode = exitCode });
        PushIni();
        PushSaves();
        PushLogs();
        Send(new JsonObject { ["t"] = "go", ["s"] = "home" });
        host.Activate();
        if (exitCode == 0)
            Toast($"Sessão encerrada ({elapsed:hh\\:mm\\:ss}). Progresso atualizado.");
        else
            Toast($"O jogo encerrou com código {exitCode}. Confira os logs em Configurações › Sistema.", isError: true);
    }

    void SaveSettings(JsonNode m)
    {
        var oldPreset = config.Preset;
        var oldCustomFps = config.CustomXc2Fps;

        if (m["launcher"] is JsonObject lObj)
        {
            if (lObj["preset"] != null) config.Preset = LaunchPresets.Find(lObj["preset"]!.ToString()).Id;
            if (lObj["customXc2Fps"] != null && int.TryParse(lObj["customXc2Fps"]!.ToString(), out var fps))
                config.CustomXc2Fps = fps == 0 ? 0 : Math.Clamp(fps, 30, 240);
            if (lObj["custom4kSelectiveScale"] != null)
                config.Custom4kSelectiveScale = (bool?)lObj["custom4kSelectiveScale"] ?? false;
            if (lObj["minimizeOnLaunch"] != null)
                config.MinimizeOnLaunch = (bool?)lObj["minimizeOnLaunch"] ?? false;
            if (lObj["closeAfterLaunch"] != null)
                config.CloseAfterLaunch = (bool?)lObj["closeAfterLaunch"] ?? false;
            if (lObj["accentTheme"] != null)
                config.AccentTheme = lObj["accentTheme"]!.ToString();
            if (lObj["soundLevel"] != null)
                config.SoundLevel = lObj["soundLevel"]!.ToString();
            if (lObj["musicEnabled"] != null)
                config.MusicEnabled = (bool?)lObj["musicEnabled"] ?? true;
            if (lObj["musicTrack"] != null)
                config.MusicTrack = lObj["musicTrack"]!.ToString();
            config.Save();
        }

        var ini = IniSettings.Load(install.IniPath);
        if (m["ini"] is JsonObject iniObj)
        {
            foreach (var kv in iniObj)
            {
                var dot = kv.Key.IndexOf('.');
                if (dot <= 0) continue;
                var sec = kv.Key[..dot];
                var key = kv.Key[(dot + 1)..];
                var val = kv.Value?.ToString() ?? "";
                ini.Set(sec, key, val);
            }
        }

        // Sync preset or FPS changes directly into xenoblade2.ini so they apply live without restarting.
        if (config.Preset != oldPreset && config.Preset != "custom")
        {
            var preset = LaunchPresets.Find(config.Preset);
            int fps = preset.TargetFps > 0 ? preset.TargetFps : 60;
            config.CustomXc2Fps = fps;
            config.Save();
            GameRunner.ApplyPresetToIni(ini, install, config, hardware, oldPreset);
        }
        else if (config.Preset == "custom" || config.CustomXc2Fps != oldCustomFps)
        {
            int fps = config.CustomXc2Fps;
            ini.Set("Performance", "fps_limit", fps == 0 ? "0" : fps.ToString());
            ini.Set("Debug", "vi_refresh", (fps == 0 ? 240 : fps > 60 ? fps : 60).ToString());
        }
        ini.SaveAtomic();

        PushState("config", config);
        PushIni();
        PushState("launchPlan", GameRunner.BuildPlan(install, config, hardware));
        bool live = playing || GameRunner.IsGameRunning();
        Toast(live ? "Configurações aplicadas ao vivo no jogo." : "Configurações salvas.");
    }

    void ResetRecommendedSettings()
    {
        var ini = IniSettings.Load(install.IniPath);
        ini.Set("Display", "docked", "1");
        ini.Set("Display", "window_mode", "borderless");
        ini.Set("Display", "output_resolution", "desktop");
        ini.Set("Graphics", "vsync", "1");
        ini.Set("Graphics", "anisotropy", "16");
        ini.Set("Graphics", "cloud_quality", "0");
        ini.Set("Graphics", "shadows", "1");
        ini.Set("Graphics", "upscale_sharpness", "0.1");
        ini.Set("Graphics", "upscale_mip_bias", "auto");
        ini.Set("Graphics", "dlss_preset", "auto");
        ini.Set("Graphics", "tonemap", "gt7");
        ini.Set("Graphics", "bloom_mode", "physical");
        ini.Set("Graphics", "contact_shadows", "1");
        ini.Set("Graphics", "env_shading", "1");
        ini.Set("Graphics", "gtao", "1");
        ini.Set("Graphics", "water_caustics", "1");
        ini.Set("Graphics", "ssgi", "1");
        ini.Set("Graphics", "ssr", "1");
        ini.Set("Performance", "show_fps", "0");
        ini.Set("Input", "amiibo", "0");
        ini.Set("Network", "discord_presence", "1");
        ini.SaveAtomic();

        config.Preset = hardware.MonitorRefreshHz >= 100 ? "120_1440_dlaa" : "60_1440_dlaa";
        config.CustomXc2Fps = hardware.MonitorRefreshHz >= 100 ? 120 : 60;
        config.Save();
        GameRunner.SyncPresetToIni(install, config, hardware);

        PushState("config", config);
        PushIni();
        PushState("launchPlan", GameRunner.BuildPlan(install, config, hardware));
        bool live = playing || GameRunner.IsGameRunning();
        Toast(live ? "Padrões recomendados aplicados ao vivo no jogo." : "Padrões recomendados restaurados.");
    }

    void RebuildShaderCache()
    {
        if (GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de recompilar o cache de shaders.");
        var stamp = Path.Combine(install.ShaderCacheDir, "vk_precompile.stamp");
        if (File.Exists(stamp)) File.Delete(stamp);
        install = GameInstall.Inspect(install.InstallDir, projectRoot, computeHashes: false);
        PushState("install", install);
        Toast("O cache de shaders será reconstruído na próxima abertura do jogo.");
    }

    void HandleSteamAdd(string userId, bool launchViaLauncher)
    {
        var accounts = SteamIntegration.DiscoverAccounts();
        var acc = accounts.FirstOrDefault(a => a.UserId == userId) ?? accounts.FirstOrDefault();
        if (acc == null)
            throw new InvalidOperationException("Nenhuma conta Steam local encontrada neste PC.");

        string targetExe = launchViaLauncher
            ? (Environment.ProcessPath ?? Path.Combine(projectRoot, "Xenoblade2Launcher.exe"))
            : install.ExePath;
        string startDir = Path.GetDirectoryName(targetExe) ?? install.InstallDir;
        string? ico = install.VisualKitDir != null
            ? Path.Combine(install.VisualKitDir, "01_atalhos", "xc2_pc_cristal.ico")
            : null;
        string launchOpts = launchViaLauncher ? "" : GameRunner.BuildPlan(install, config, hardware).Arguments;

        var (_, artCount) = SteamIntegration.AddOrUpdateShortcut(
            acc.ShortcutsPath, acc.GridDir, targetExe, startDir, ico, install.VisualKitDir, launchOpts);

        PushSteam();
        Toast($"Adicionado à Steam ({acc.PersonaName}) com {artCount} artes oficiais.");
    }

    void HandleSteamRemove(string userId)
    {
        var accounts = SteamIntegration.DiscoverAccounts();
        var acc = accounts.FirstOrDefault(a => a.UserId == userId) ?? accounts.FirstOrDefault();
        if (acc == null) throw new InvalidOperationException("Conta Steam não encontrada.");

        bool removed = SteamIntegration.RemoveShortcut(acc.ShortcutsPath);
        PushSteam();
        Toast(removed ? $"Removido da biblioteca Steam ({acc.PersonaName})." : "O atalho não estava presente na Steam.");
    }

    void SwitchInstallDir(string dir)
    {
        if (playing || GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de trocar a pasta de instalação.");
        if (!File.Exists(Path.Combine(dir, "xenoblade2.exe")))
            throw new FileNotFoundException("A pasta escolhida não contém o executável do jogo.", dir);

        config.InstallPath = dir;
        config.Save();
        install = GameInstall.Inspect(dir, projectRoot, computeHashes: true);
        hardware = SystemProbe.Probe(install.InstallDir);
        audio = new GameAudio(install.RomfsDir, LauncherPaths.MediaCacheDir);
        BindContentProviders();
        audio.WarmupAsync(config.MusicTrack);

        PushFullState(includeHashes: true);
        Toast("Pasta de instalação atualizada.");
    }

    void HandleDrop(string filePath, string context)
    {
        if (filePath.EndsWith(".nup", StringComparison.OrdinalIgnoreCase))
        {
            HandleSetupInspect(filePath, null);
            if (!string.Equals(context, "onboarding", StringComparison.OrdinalIgnoreCase))
                Send(new JsonObject { ["t"] = "go", ["s"] = "system" });
            return;
        }
        if (playing || GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de importar saves ou instalar mods.");
        bool looksLikeSave =
            filePath.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(filePath) && File.Exists(Path.Combine(filePath, "bf2savefile.sav")));

        if (looksLikeSave || context == "saves")
        {
            var imported = new SaveManager(install.SaveUserDir, projectRoot).RestoreFromFile(filePath);
            PushSaves();
            Toast($"Save importado: {imported.LandmarkName}.");
            return;
        }

        var pack = new ModManager(install).InstallDroppedPack(filePath);
        PushMods();
        Toast($"'{pack.Name}' instalado com sucesso.");
    }

    void HandleSetupPickNup()
    {
        if (setupRunning)
            throw new InvalidOperationException("Uma instalação já está em andamento.");
        var picked = host.PickFile("Selecionar pacote Xenoblade Chronicles 2 Switch 2 Edition (.nup)", "Pacote NUP (*.nup)", "*.nup");
        if (picked != null)
            HandleSetupInspect(picked, null);
    }

    void HandleSetupInspect(string nupPath, string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(nupPath))
            throw new ArgumentException("Selecione um arquivo .nup válido.");
        string resolvedTarget = string.IsNullOrWhiteSpace(targetDir)
            ? Path.Combine(projectRoot, "games")
            : targetDir;
        var pre = setupPipeline.Inspect(nupPath, resolvedTarget, projectRoot);
        lastSetupPreflight = pre;
        PushState("setup", new
        {
            running = setupRunning,
            preflight = pre,
            statusName = setupPipeline.StatusName,
            isAvailable = setupPipeline.IsAvailable
        });
        Toast(pre.ReadyToRun
            ? $"Pacote validado: {pre.Nup.GameTitle} v{pre.Nup.DisplayVersion} ({pre.Nup.PackageCount} pacotes)."
            : (pre.BlockingReason ?? "Pacote verificado com pendências."),
            isError: !pre.ReadyToRun);
    }

    void HandleSetupStart(string nupPath, string? targetDir, bool forceRecompileAot)
    {
        if (setupRunning)
            throw new InvalidOperationException("A instalação inicial já está em andamento.");
        if (playing || GameRunner.IsGameRunning())
            throw new InvalidOperationException("Feche o jogo antes de iniciar a instalação.");
        if (string.IsNullOrWhiteSpace(nupPath))
            throw new ArgumentException("Selecione um arquivo .nup antes de iniciar.");

        string resolvedTarget = string.IsNullOrWhiteSpace(targetDir)
            ? Path.Combine(projectRoot, "games")
            : targetDir;

        setupRunning = true;
        setupCts?.Dispose();
        setupCts = new CancellationTokenSource();
        var ct = setupCts.Token;

        PushState("setup", new
        {
            running = true,
            preflight = lastSetupPreflight,
            statusName = setupPipeline.StatusName,
            isAvailable = setupPipeline.IsAvailable
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var req = new SetupRequest(nupPath, resolvedTarget, projectRoot, forceRecompileAot);
                var result = await setupPipeline.RunAsync(req, prog =>
                {
                    Send(new JsonObject
                    {
                        ["t"] = "setupProgress",
                        ["stage"] = prog.Stage,
                        ["stageIndex"] = prog.StageIndex,
                        ["stageCount"] = prog.StageCount,
                        ["percent"] = Math.Round(prog.Percent, 1),
                        ["detail"] = prog.Detail,
                        ["logLine"] = prog.LogLine,
                        ["isDone"] = prog.IsDone,
                        ["isError"] = prog.IsError
                    });
                }, ct);

                setupRunning = false;
                if (File.Exists(Path.Combine(resolvedTarget, "xenoblade2.exe")))
                {
                    config.InstallPath = resolvedTarget;
                    config.Save();
                    install = GameInstall.Inspect(resolvedTarget, projectRoot, computeHashes: true);
                    hardware = SystemProbe.Probe(install.InstallDir);
                    audio = new GameAudio(install.RomfsDir, LauncherPaths.MediaCacheDir);
                    BindContentProviders();
                    audio.WarmupAsync(config.MusicTrack);
                }
                else
                {
                    install = GameInstall.Inspect(install.InstallDir, projectRoot, computeHashes: true);
                }
                PushFullState(includeHashes: true);
                Toast(result.Summary);
            }
            catch (OperationCanceledException)
            {
                setupRunning = false;
                Send(new JsonObject
                {
                    ["t"] = "setupProgress",
                    ["stage"] = "Cancelado",
                    ["stageIndex"] = 0,
                    ["stageCount"] = 6,
                    ["percent"] = 0,
                    ["detail"] = "Instalação cancelada pelo usuário.",
                    ["isDone"] = true,
                    ["isError"] = true
                });
                PushState("setup", new { running = false, preflight = lastSetupPreflight, statusName = setupPipeline.StatusName, isAvailable = setupPipeline.IsAvailable });
                Toast("Instalação cancelada.", isError: true);
            }
            catch (Exception ex)
            {
                setupRunning = false;
                LauncherLog.Write($"setup failed: {ex.Message}");
                Send(new JsonObject
                {
                    ["t"] = "setupProgress",
                    ["stage"] = "Erro",
                    ["stageIndex"] = 0,
                    ["stageCount"] = 6,
                    ["percent"] = 0,
                    ["detail"] = ex.Message,
                    ["isDone"] = true,
                    ["isError"] = true
                });
                PushState("setup", new { running = false, preflight = lastSetupPreflight, statusName = setupPipeline.StatusName, isAvailable = setupPipeline.IsAvailable });
                Toast($"Erro na instalação: {ex.Message}", isError: true);
            }
        });
    }

    void HandleSetupCancel()
    {
        if (setupRunning && setupCts != null && !setupCts.IsCancellationRequested)
        {
            setupCts.Cancel();
            Toast("Cancelando instalação...");
        }
    }

    void OpenFolder(string what)
    {
        string target = what switch
        {
            "install" => install.InstallDir,
            "saves" => install.SaveUserDir,
            "backups" => LauncherPaths.SaveBackupsDir,
            "localization" => install.LocalizationDir,
            "user_mods" => Directory.Exists(Path.Combine(projectRoot, "work", "user_mods"))
                ? Path.Combine(projectRoot, "work", "user_mods")
                : Path.Combine(projectRoot, "mods"),
            "root" => projectRoot,
            "shaders" => install.ShaderCacheDir,
            "data" => LauncherPaths.DataDir,
            _ => install.InstallDir
        };
        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    void OpenFileInShell(string path)
    {
        if (!File.Exists(path))
        {
            Toast("Arquivo ainda não existe nesta instalação.", "error", isError: true);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ==================== State Push Helpers ====================

    bool IsJpNamesActive() =>
        IniSettings.Load(install.IniPath).Get("Display", "resource_pack", "").Equals("pt-br-jp", StringComparison.OrdinalIgnoreCase);

    void PushFullState(bool includeHashes)
    {
        var bgms = audio.ListBgmTracks();
        var saves = new SaveManager(install.SaveUserDir, projectRoot).Inspect(IsJpNamesActive());
        var mods = new ModManager(install).Inspect();
        var ini = IniSettings.Load(install.IniPath).ToFlatMap();
        var steam = SteamIntegration.DiscoverAccounts();
        var plan = GameRunner.BuildPlan(install, config, hardware);
        var recommendation = GameRunner.RecommendPreset(hardware, install);

        var payload = new
        {
            t = "state",
            config,
            install,
            hardware,
            recommendation,
            presets = LaunchPresets.All,
            launchPlan = plan,
            bgmTracks = bgms,
            saves,
            mods,
            ini,
            steam,
            setupPipeline = new { setupPipeline.StatusName, setupPipeline.IsAvailable },
            setup = new { running = setupRunning, preflight = lastSetupPreflight, statusName = setupPipeline.StatusName, isAvailable = setupPipeline.IsAvailable },
            playing = new { running = playing, profile = activeLaunchProfile, startedAt = playingSince.ToString("HH:mm:ss") },
            logs = new
            {
                gameLog = GameRunner.ReadLogTail(install.LogPath, 90),
                fidelity = GameRunner.ReadLogTail(install.FidelityPath, 60),
                launcherLog = GameRunner.ReadLogTail(LauncherLog.LogPath, 60)
            }
        };
        host.Post(JsonSerializer.Serialize(payload, JsonOpts));
    }

    void PushSaves() => PushState("saves", new SaveManager(install.SaveUserDir, projectRoot).Inspect(IsJpNamesActive()));
    void PushMods() => PushState("mods", new ModManager(install).Inspect());
    void PushIni() => PushState("ini", IniSettings.Load(install.IniPath).ToFlatMap());
    void PushSteam() => PushState("steam", SteamIntegration.DiscoverAccounts());
    void PushLogs() => PushState("logs", new
    {
        gameLog = GameRunner.ReadLogTail(install.LogPath, 90),
        fidelity = GameRunner.ReadLogTail(install.FidelityPath, 60),
        launcherLog = GameRunner.ReadLogTail(LauncherLog.LogPath, 60)
    });

    void PushState(string key, object value)
    {
        var dict = new Dictionary<string, object> { ["t"] = "state", [key] = value };
        host.Post(JsonSerializer.Serialize(dict, JsonOpts));
    }

    void Send(JsonNode n) => host.Post(n.ToJsonString());

    void Toast(string text, string? sfx = null, bool isError = false)
    {
        var o = new JsonObject { ["t"] = "toast", ["text"] = text, ["error"] = isError };
        Send(o);
    }
}
