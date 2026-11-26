using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using XC2.Core;

int passed = 0;
int failed = 0;

void Check(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void Assert(bool cond, string msg)
{
    if (!cond) throw new Exception(msg);
}

var projectRoot = GameInstall.FindProjectRoot();
var testInstallDir = Path.Combine(projectRoot, "work", "test");
var tempRoot = Path.Combine(Path.GetTempPath(), $"xc2_launcher_tests_{Environment.ProcessId}");
Directory.CreateDirectory(tempRoot);

try
{
    Check("PresenceNames loads bilingual tables", () =>
    {
        var pn = PresenceNames.Current;
        Assert(pn.Landmark(78, pt: true) == "Teatro Olethro", $"Expected Teatro Olethro, got {pn.Landmark(78)}");
        Assert(pn.Region(78, pt: true) == "Reino de Uraya", $"Expected Reino de Uraya, got {pn.Region(78)}");
        Assert(pn.Landmark(36, pt: true) == "Navio Antigo, Convés", $"Expected Navio Antigo, Convés, got {pn.Landmark(36)}");
        Assert(pn.Driver(1) == "Rex", "Driver 1 should be Rex");
        Assert(pn.Blade(1001) == "Pyra", "Blade 1001 should be Pyra");
        Assert(pn.Blade(1002) == "Mythra", "Blade 1002 should be Mythra");
        Assert(pn.Landmark(78, pt: true, jpNames: true) == "Teatro Kalam", $"Expected Teatro Kalam, got {pn.Landmark(78, true, true)}");
        Assert(pn.Region(78, pt: true, jpNames: true) == "Reino de Invidia", $"Expected Reino de Invidia, got {pn.Region(78, true, true)}");
        Assert(pn.Blade(1001, pt: true, jpNames: true) == "Homura", "Blade 1001 JP should be Homura");
        Assert(pn.Blade(1002, pt: true, jpNames: true) == "Hikari", "Blade 1002 JP should be Hikari");
        Assert(pn.Driver(6, pt: true, jpNames: true) == "Meleph", "Driver 6 JP should be Meleph");
    });

    Check("SaveParser parses real saves when present", () =>
    {
        var liveSave = Path.Combine(testInstallDir, "xenoblade2_data", "save", "user", "bf2savefile.sav");
        if (File.Exists(liveSave))
        {
            var info = SaveParser.ParseFile(liveSave);
            Assert(info.IsValid, "live user save should be valid");
            Assert(!string.IsNullOrWhiteSpace(info.LandmarkName), "live landmark parsed");
            Assert(info.LeaderLevel > 0, $"live level parsed ({info.LeaderLevel})");
        }
    });

    Check("IniSettings preserves comments, sections, and writes atomically", () =>
    {
        var iniPath = Path.Combine(tempRoot, "test.ini");
        File.WriteAllText(iniPath,
            "; header comment\r\n[Display]\r\n; pack comment\r\nresource_pack=\r\nfullscreen=0\r\n\r\n[Graphics]\r\nresolution=1\r\nupscale=off\r\n");

        var ini = IniSettings.Load(iniPath);
        Assert(ini.Get("Graphics", "upscale") == "off", "read upscale");
        ini.Set("Graphics", "upscale", "dlss_native");
        ini.Set("Display", "resource_pack", "ptbr");
        ini.Set("Display", "new_key", "42");
        ini.SaveAtomic();

        var raw = File.ReadAllText(iniPath);
        Assert(raw.Contains("; header comment"), "header comment preserved");
        Assert(raw.Contains("; pack comment"), "inline comment preserved");

        var reloaded = IniSettings.Load(iniPath);
        Assert(reloaded.Get("Graphics", "upscale") == "dlss_native", "upscale updated");
        Assert(reloaded.Get("Display", "resource_pack") == "ptbr", "resource_pack updated");
        Assert(reloaded.Get("Display", "new_key") == "42", "new_key inserted in Display");
    });

    Check("GameAudio transmuxes m01.nop to valid OggOpus and decodes system.pck PTADPCM to WAV", () =>
    {
        var romfs = Path.Combine(testInstallDir, "xenoblade2_data", "romfs");
        if (!File.Exists(Path.Combine(romfs, "stream", "bgm", "m01.nop"))) return;
        var cache = Path.Combine(tempRoot, "audio_cache");
        var audio = new GameAudio(romfs, cache);

        var tracks = audio.ListBgmTracks();
        Assert(tracks.Any(t => t.Id == "m01" && t.Available && t.DurationSec > 160), "m01.nop available");
        Assert(tracks.Any(t => t.Id == "m90" && t.Available && t.LoopStartSec > 10), "m90.nop loop parsed");

        var ogg = audio.GetBgmOgg("m01");
        Assert(ogg != null && ogg.Value.Data.Length > 1_000_000, "m01 ogg generated");
        Assert(Encoding.ASCII.GetString(ogg!.Value.Data, 0, 4) == "OggS", "OggS magic");
        Assert(Encoding.ASCII.GetString(ogg.Value.Data, 28, 8) == "OpusHead", "OpusHead header");

        foreach (var sfxId in GameAudio.SfxEvents.Keys)
        {
            var wav = audio.GetSfxWav(sfxId);
            Assert(wav != null && wav.Value.Data.Length > 2000, $"SFX {sfxId} decoded");
            Assert(Encoding.ASCII.GetString(wav!.Value.Data, 0, 4) == "RIFF", $"SFX {sfxId} RIFF");
            Assert(Encoding.ASCII.GetString(wav.Value.Data, 8, 4) == "WAVE", $"SFX {sfxId} WAVE");
        }
    });

    Check("GameArtwork extracts official banners, logos, icons, Steam art, and Core Crystal ICO directly from bf2.ard", () =>
    {
        var romfs = Path.Combine(projectRoot, "games", "xenoblade2_data", "romfs");
        if (!File.Exists(Path.Combine(romfs, "bf2.ard"))) return;

        var cache = Path.Combine(tempRoot, "art_cache");
        var art = new GameArtwork(romfs, cache);
        Assert(art.IsAvailable, "bf2.arh + bf2.ard available");

        (int W, int H) AssertValidPng((byte[] Data, string Mime)? res, string label)
        {
            Assert(res != null && res.Value.Mime == "image/png" && res.Value.Data.Length > 256, $"{label} PNG generated");
            byte[] d = res!.Value.Data;
            Assert(d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47, $"{label} PNG signature");
            int w = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(16, 4));
            int h = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(20, 4));
            Assert(w > 0 && h > 0, $"{label} dimensions {w}x{h}");
            return (w, h);
        }

        var (kw, kh) = AssertValidPng(art.GetArtPng("keyart_bg"), "keyart_bg");
        Assert(kw == 1920 && kh > 1000, $"keyart_bg size {kw}x{kh}");

        var (lw, lh) = AssertValidPng(art.GetArtPng("logo_official"), "logo_official");
        Assert(lw > 600 && lh < lw * 0.55, $"logo_official ribbon trimmed ({lw}x{lh})");

        AssertValidPng(art.GetArtPng("logo_torna"), "logo_torna");
        AssertValidPng(art.GetArtPng("torna_bg"), "torna_bg");
        AssertValidPng(art.GetArtPng("menu_bg"), "menu_bg");
        AssertValidPng(art.GetArtPng("save_rex"), "save_rex");
        AssertValidPng(art.GetArtPng("dlc_blades"), "dlc_blades");

        var (cw, ch) = AssertValidPng(art.GetArtPng("crystal"), "crystal");
        Assert(cw == 256 && ch == 256, $"crystal 256x256, got {cw}x{ch}");

        var (covW, covH) = AssertValidPng(art.GetArtPng("cover"), "cover");
        Assert(covW == 600 && covH == 900, $"cover 600x900, got {covW}x{covH}");

        var (banW, banH) = AssertValidPng(art.GetArtPng("banner"), "banner");
        Assert(banW == 920 && banH == 430, $"banner 920x430, got {banW}x{banH}");

        var (herW, herH) = AssertValidPng(art.GetArtPng("hero"), "hero");
        Assert(herW == 1920 && herH == 620, $"hero 1920x620, got {herW}x{herH}");

        foreach (var iconName in new[] { "cristal", "salvar", "opcoes", "bolsa", "tela", "livros" })
        {
            var (iw, ih) = AssertValidPng(art.GetIconPng(iconName), $"icon/{iconName}");
            Assert(iw == 128 && ih == 128, $"icon/{iconName} 128x128, got {iw}x{ih}");
        }

        var icoPath = art.EnsureCrystalIcon();
        Assert(!string.IsNullOrEmpty(icoPath) && File.Exists(icoPath), "crystal ICO generated");
        byte[] icoBytes = File.ReadAllBytes(icoPath!);
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(0, 2)) == 0 &&
               BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(2, 2)) == 1 &&
               BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(4, 2)) == 7,
               "ICO header valid with 7 PNG sizes");

        var steamDir = art.EnsureSteamArtDir();
        Assert(!string.IsNullOrEmpty(steamDir) &&
               File.Exists(Path.Combine(steamDir!, @"03_biblioteca\capa_600x900.png")) &&
               File.Exists(Path.Combine(steamDir!, @"03_biblioteca\banner_920x430.png")),
               "Steam grid art materialized in cache");
    });

    Check("SteamIntegration VDF round-trip preserves foreign shortcuts byte-for-byte", () =>
    {
        // Build a synthetic shortcuts.vdf with 1 foreign game
        var scPath = Path.Combine(tempRoot, "shortcuts.vdf");
        var gridDir = Path.Combine(tempRoot, "grid");
        var foreignVdf = SteamIntegration.UpsertShortcutVdf(
            null, 0x81234567u, "Foreign Indie Game", "\"C:\\Games\\Indie.exe\"", "\"C:\\Games\"", "", "--flag");
        // Rename AppName inside so it's clearly non-XC2
        File.WriteAllBytes(scPath, foreignVdf);
        Assert(!SteamIntegration.ContainsOurShortcut(foreignVdf), "foreign vdf shouldn't match XC2");

        var kitDir = Path.Combine(tempRoot, "fake_kit");
        foreach (var rel in new[]
        {
            @"03_biblioteca\capa_600x900.png",
            @"03_biblioteca\banner_920x430.png",
            @"03_biblioteca\hero_sem_texto_1920x620.png",
            @"02_titulo\xc2_pc_edition_640.png",
            @"01_atalhos\png\xc2_cristal_256.png"
        })
        {
            var fp = Path.Combine(kitDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            File.WriteAllBytes(fp, [0x89, 0x50, 0x4E, 0x47]);
        }
        var (appId, artCount) = SteamIntegration.AddOrUpdateShortcut(
            scPath, gridDir, @"C:\Games\XC2PC\Xenoblade2Launcher.exe",
            @"C:\Games\XC2PC", null, kitDir, "", ignoreRunningCheck: true);

        Assert((appId & 0x80000000u) != 0, "high bit set on non-Steam appId");
        Assert(artCount == 5, $"Expected 5 kit artworks copied, got {artCount}");
        Assert(File.Exists(scPath + ".before-xc2"), "before-xc2 backup created");

        var afterAdd = File.ReadAllBytes(scPath);
        Assert(SteamIntegration.ContainsOurShortcut(afterAdd), "contains XC2 shortcut");
        Assert(Encoding.UTF8.GetString(afterAdd).Contains("Foreign Indie Game"), "foreign shortcut preserved");

        bool removed = SteamIntegration.RemoveShortcut(scPath, ignoreRunningCheck: true);
        Assert(removed, "remove succeeded");
        var afterRemove = File.ReadAllBytes(scPath);
        Assert(!SteamIntegration.ContainsOurShortcut(afterRemove), "XC2 removed");
        Assert(afterRemove.SequenceEqual(foreignVdf), "byte-exact match with original foreign shortcuts.vdf!");
    });

    Check("SaveManager backup and atomic SHA-256 restore in isolated temp dir", () =>
    {
        var realSave = Path.Combine(testInstallDir, "xenoblade2_data", "save", "user", "bf2savefile.sav");
        if (!File.Exists(realSave)) return;

        var fakeSaveDir = Path.Combine(tempRoot, "fake_save");
        var fakeBkpDir = Path.Combine(tempRoot, "fake_backups");
        Directory.CreateDirectory(fakeSaveDir);
        File.Copy(realSave, Path.Combine(fakeSaveDir, "bf2savefile.sav"));

        var expectedLive = SaveParser.ParseFile(realSave);
        var sm = new SaveManager(fakeSaveDir, projectRoot, fakeBkpDir);
        var bkp = sm.CreateBackup("Teste Manual");
        Assert(File.Exists(bkp.FilePath), "backup file exists");
        Assert(bkp.Info.LandmarkName == expectedLive.LandmarkName, $"backup parsed ({bkp.Info.LandmarkName})");
    });

    Check("GameRunner.SyncPresetToIni & BuildPlan enable live hot-reload without CLI/env locks", () =>
    {
        var fakeDir = Path.Combine(tempRoot, "fake_install");
        Directory.CreateDirectory(fakeDir);
        var fakeIni = Path.Combine(fakeDir, "xenoblade2.ini");
        File.WriteAllText(fakeIni, "[Graphics]\r\nresolution=1\r\nupscale=off\r\n[Performance]\r\nfps_limit=60\r\n[Debug]\r\nvi_refresh=60\r\n");

        var realInst = GameInstall.Inspect(testInstallDir, projectRoot, computeHashes: false);
        var fakeInst = realInst with { InstallDir = fakeDir, IniPath = fakeIni, ExePath = Path.Combine(fakeDir, "xenoblade2.exe") };
        var hw = SystemProbe.Probe(testInstallDir);
        var cfg = new LauncherConfig { Preset = "120_1440_dlaa", CustomXc2Fps = 120 };

        GameRunner.SyncPresetToIni(fakeInst, cfg, hw);
        var ini = IniSettings.Load(fakeIni);
        Assert(ini.Get("Graphics", "resolution") == "2.5", $"resolution={ini.Get("Graphics", "resolution")}");
        Assert(ini.Get("Graphics", "upscale") == (hw.IsNvidiaRtx && fakeInst.HasDlss ? "dlss_native" : "fsr3_native"), $"upscale={ini.Get("Graphics", "upscale")}");
        Assert(ini.Get("Performance", "fps_limit") == "120", $"fps_limit={ini.Get("Performance", "fps_limit")}");
        Assert(ini.Get("Debug", "vi_refresh") == "120", $"vi_refresh={ini.Get("Debug", "vi_refresh")}");

        var plan = GameRunner.BuildPlan(fakeInst, cfg, hw);
        Assert(string.IsNullOrEmpty(plan.Arguments), $"Expected no CLI override args, got '{plan.Arguments}'");
        Assert(!plan.EnvVars.ContainsKey("SNX_UPSCALE"), "SNX_UPSCALE must not lock live upscale switching");
        Assert(!plan.EnvVars.ContainsKey("SNX_XC2_FPS"), "SNX_XC2_FPS must not lock live FPS switching");
    });

    Check("Original (Switch 2) preset applies exact console settings and restores cleanly when switching back", () =>
    {
        var fakeDir = Path.Combine(tempRoot, "fake_original");
        Directory.CreateDirectory(fakeDir);
        var fakeIni = Path.Combine(fakeDir, "xenoblade2.ini");
        File.WriteAllText(fakeIni,
            "[Display]\r\nresource_pack=pt-br\r\ndocked=0\r\nwindow_mode=borderless\r\n" +
            "[Graphics]\r\nresolution=2.5\r\nupscale=dlss_native\r\ndlss_preset=m\r\nupscale_mip_bias=auto\r\nanisotropy=16\r\ncloud_quality=2\r\nimage_follow=1\r\nshadows=0\r\nvsync=0\r\ntonemap=gt7\r\nbloom_mode=physical\r\ncontact_shadows=1\r\nenv_shading=1\r\ngtao=1\r\nwater_caustics=1\r\nssgi=1\r\nssr=1\r\n" +
            "[Performance]\r\nfps_limit=120\r\nshow_fps=1\r\n" +
            "[Debug]\r\nvi_refresh=120\r\n");

        var realInst = GameInstall.Inspect(testInstallDir, projectRoot, computeHashes: false);
        var fakeInst = realInst with { InstallDir = fakeDir, IniPath = fakeIni, ExePath = Path.Combine(fakeDir, "xenoblade2.exe") };
        var hw = SystemProbe.Probe(testInstallDir);

        var origPreset = LaunchPresets.Find("original");
        Assert(origPreset.Id == "original" && origPreset.Name == "Original" && origPreset.Badge == "Switch 2",
            $"Unexpected original preset metadata: {origPreset}");

        var cfg = new LauncherConfig { Preset = "original", CustomXc2Fps = 60 };
        GameRunner.SyncPresetToIni(fakeInst, cfg, hw, "120_1440_dlaa");

        var ini = IniSettings.Load(fakeIni);
        Assert(ini.Get("Display", "docked") == "1", "docked=1");
        Assert(ini.Get("Display", "resource_pack") == "pt-br", "resource_pack preserved");
        Assert(ini.Get("Display", "window_mode") == "borderless", "window_mode preserved");
        Assert(ini.Get("Graphics", "resolution") == "1", "resolution=1");
        Assert(ini.Get("Graphics", "upscale") == "auto", "upscale=auto");
        Assert(ini.Get("Graphics", "dlss_preset") == "auto", "dlss_preset=auto");
        Assert(ini.Get("Graphics", "upscale_mip_bias") == "0", "upscale_mip_bias=0");
        Assert(ini.Get("Graphics", "anisotropy") == "0", "anisotropy=0");
        Assert(ini.Get("Graphics", "cloud_quality") == "0", "cloud_quality=0");
        Assert(ini.Get("Graphics", "image_follow") == "0", "image_follow=0");
        Assert(ini.Get("Graphics", "shadows") == "1", "shadows=1");
        Assert(ini.Get("Graphics", "vsync") == "1", "vsync=1");
        Assert(ini.Get("Graphics", "tonemap") == "native", "tonemap=native");
        Assert(ini.Get("Graphics", "bloom_mode") == "native", "bloom_mode=native");
        Assert(ini.Get("Graphics", "contact_shadows") == "0", "contact_shadows=0");
        Assert(ini.Get("Graphics", "env_shading") == "0", "env_shading=0");
        Assert(ini.Get("Graphics", "gtao") == "0", "gtao=0");
        Assert(ini.Get("Graphics", "water_caustics") == "0", "water_caustics=0");
        Assert(ini.Get("Graphics", "ssgi") == "0", "ssgi=0");
        Assert(ini.Get("Graphics", "ssr") == "0", "ssr=0");
        Assert(ini.Get("Performance", "fps_limit") == "game", "fps_limit=game");
        Assert(ini.Get("Performance", "show_fps") == "1", "show_fps preserved");
        Assert(ini.Get("Debug", "vi_refresh") == "60", "vi_refresh=60");

        var plan = GameRunner.BuildPlan(fakeInst, cfg, hw);
        Assert(plan.ProfileSummary == "Original · Switch 2", $"summary={plan.ProfileSummary}");
        Assert(string.IsNullOrEmpty(plan.Arguments), $"args='{plan.Arguments}'");
        Assert(plan.EnvVars.Count == 0, $"Expected 0 env overrides in Original mode, got {plan.EnvVars.Count}");

        // Switching back to a scaled PC preset restores upscale_mip_bias=auto
        cfg.Preset = "120_1440_dlaa";
        cfg.CustomXc2Fps = 120;
        GameRunner.SyncPresetToIni(fakeInst, cfg, hw, "original");
        var backIni = IniSettings.Load(fakeIni);
        Assert(backIni.Get("Graphics", "resolution") == "2.5", "restored resolution=2.5");
        Assert(backIni.Get("Graphics", "upscale_mip_bias") == "auto", "restored upscale_mip_bias=auto");
        Assert(backIni.Get("Performance", "fps_limit") == "120", "restored fps_limit=120");
        Assert(backIni.Get("Debug", "vi_refresh") == "120", "restored vi_refresh=120");
    });

    Check("GameInstall & SystemProbe inspect environment cleanly", () =>
    {
        if (File.Exists(Path.Combine(testInstallDir, "xenoblade2.exe")))
        {
            var inst = GameInstall.Inspect(testInstallDir, projectRoot, computeHashes: false);
            Assert(inst.IsReadyToPlay, "work/test should be ready to play");
            Assert(inst.Addons.Length == 7, $"Expected 7 DLC addons, got {inst.Addons.Length}");
        }

        var hw = SystemProbe.Probe(tempRoot);
        Assert(!string.IsNullOrWhiteSpace(hw.GpuName), "GPU detected");
        Assert(hw.MonitorRefreshHz >= 60, $"Hz={hw.MonitorRefreshHz}");
        Console.WriteLine($"       HW: {hw.GpuName} ({hw.GpuVramGb} GB) · {hw.ScreenWidth}x{hw.ScreenHeight}@{hw.MonitorRefreshHz}Hz · RAM {hw.TotalRamGb} GB · C:{hw.FreeCDriveGb} GB {hw.InstallDriveLetter}{hw.FreeInstallDriveGb} GB");
    });

    Check("Subsdk1StubBuilder generates valid clean-room NSO0 (and byte-exact match on real subsdk1 when present)", () =>
    {
        // 1. Synthetic round-trip
        byte[] syntheticBid = new byte[32];
        for (int i = 0; i < 32; i++) syntheticBid[i] = (byte)(i + 1);
        string[] syms = ["_Z23dlssdetailUseMiddleWarev", "GetNGXResultAsString", "NVSDK_NGX_VULKAN_Init", "NVSDK_NGX_VULKAN_EvaluateFeature_C"];
        byte[] stubBytes = Subsdk1StubBuilder.BuildStubBytes(syntheticBid, syms);
        Assert(stubBytes.Length == 0x4100, $"Expected 16640 bytes, got {stubBytes.Length}");
        var (parsedBid, parsedSyms) = Subsdk1StubBuilder.ReadNsoBuildIdAndExports(stubBytes, "synthetic");
        Assert(parsedBid.SequenceEqual(syntheticBid), "BuildId round-trip mismatch");
        Assert(parsedSyms.SequenceEqual(syms), $"Exports round-trip mismatch: {string.Join(",", parsedSyms)}");

        // 2. Real bypass subsdk1 byte-for-byte parity check (when present locally)
        var realBypass = Path.Combine(projectRoot, "games", "xenoblade2_data", "exefs_host_bypass", "subsdk1");
        var realStub = Path.Combine(projectRoot, "games", "xenoblade2_data", "exefs", "subsdk1");
        if (File.Exists(realBypass))
        {
            var outStub = Path.Combine(tempRoot, "subsdk1_rebuilt");
            var res = Subsdk1StubBuilder.BuildFromBypass(realBypass, outStub);
            Assert(res.SizeBytes == 16640, $"Expected 16640 B, got {res.SizeBytes}");
            Assert(res.ExportedSymbolCount == 33, $"Expected 33 symbols, got {res.ExportedSymbolCount}");
            if (File.Exists(realStub))
            {
                string expectedSha = GameInstall.QuickSha256(realStub);
                Assert(res.Sha256Hex == expectedSha, $"Byte-exact parity mismatch: {res.Sha256Hex} != {expectedSha}");
                Console.WriteLine($"       Subsdk1StubBuilder byte-exact SHA-256 match: {res.Sha256Hex[..16]}... ({res.ExportedSymbolCount} symbols)");
            }
        }
    });

    Check("GameInstall.FindProjectRoot is portable and discovers runtime/ layout cleanly", () =>
    {
        var freshRoot = Path.Combine(tempRoot, "portable_release");
        var runtimeDir = Path.Combine(freshRoot, "runtime");
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "xenoblade2.exe"), "stub");

        var found = GameInstall.FindProjectRoot(freshRoot);
        Assert(found.Equals(freshRoot, StringComparison.OrdinalIgnoreCase), $"Expected {freshRoot}, got {found}");
        Assert(!found.Contains(@"F:\XENOBLADE", StringComparison.OrdinalIgnoreCase), "Must never fall back to hardcoded dev path");
    });

    Check("NupArchive & NativeSetupPipeline verify TOC SHA-256, extract Pkg[0]+NACP+host_bypass, and build Horizon .romfs", () =>
    {
        var nupPath = Path.Combine(tempRoot, "synthetic_xc2.nup");
        var outDir = Path.Combine(tempRoot, "nup_out");

        // Build a synthetic valid NUP v1 container with Pkg[0] (XC2 SW2) + Pkg[1] (DLC addon)
        byte[] mainBytes = Encoding.UTF8.GetBytes("NSO0_SYNTHETIC_MAIN_BINARY");
        byte[] subsdk1Bytes = Encoding.UTF8.GetBytes("AMPERE_DLSS_HOST_BYPASS");
        byte[] romFileBytes = Encoding.UTF8.GetBytes("Alrest RomFS test payload 3.0.1");
        byte[] dlcFileBytes = Encoding.UTF8.GetBytes("Torna Golden Country addon file!");
        byte[] nacpBytes = new byte[NupArchive.NacpSize];
        Encoding.UTF8.GetBytes("Xenoblade Chronicles 2").CopyTo(nacpBytes, 0);
        Encoding.UTF8.GetBytes("3.0.1").CopyTo(nacpBytes, 0x3060);

        // TLV metadata for Pkg[0]: tag 2 = nacpBytes, tag 4 = "3.0.1"
        byte[] verBytes = Encoding.UTF8.GetBytes("3.0.1");
        byte[] metaBuf = new byte[8 + nacpBytes.Length + 8 + verBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(metaBuf.AsSpan(0, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(metaBuf.AsSpan(4, 4), (uint)nacpBytes.Length);
        nacpBytes.CopyTo(metaBuf, 8);
        int vOff = 8 + nacpBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(metaBuf.AsSpan(vOff, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(metaBuf.AsSpan(vOff + 4, 4), (uint)verBytes.Length);
        verBytes.CopyTo(metaBuf, vOff + 8);

        // Layout blobs starting at 0x80
        ulong offMeta = 0x80;
        ulong offMain = offMeta + (ulong)metaBuf.Length;
        ulong offSub1 = offMain + (ulong)mainBytes.Length;
        ulong offRom = offSub1 + (ulong)subsdk1Bytes.Length;
        ulong offDlc = offRom + (ulong)romFileBytes.Length;
        ulong tocOff = offDlc + (ulong)dlcFileBytes.Length;

        // Directories:
        // 0: pkg0 root -> children: [dir 2 (exefs), dir 3 (romfs)]
        // 1: pkg1 root -> children: [dir 4 (romfs)]
        // 2: exefs     -> children: [file 0 (main), file 1 (subsdk1)]
        // 3: romfs     -> children: [file 2 (hello.txt)]
        // 4: romfs(p1) -> children: [file 3 (torna.bin)]
        const int nPkg = 2, nDir = 5, nFile = 4;
        var strList = new MemoryStream();
        (uint off, uint len) AddStr(string s)
        {
            uint o = (uint)strList.Position;
            byte[] b = Encoding.UTF8.GetBytes(s);
            strList.Write(b);
            return (o, (uint)b.Length);
        }

        var sEmpty = AddStr("");
        var sExefs = AddStr("exefs");
        var sRomfs = AddStr("romfs");
        var sMain = AddStr("main");
        var sSub1 = AddStr("subsdk1");
        var sHello = AddStr("hello.txt");
        var sTorna = AddStr("torna.bin");
        byte[] strtab = strList.ToArray();

        uint[] childArr = [2u, 3u, 4u, nDir + 0u, nDir + 1u, nDir + 2u, nDir + 3u];
        int pkgSecOff = 0x30;
        int dirSecOff = pkgSecOff + nPkg * 0x40;
        int fileSecOff = dirSecOff + nDir * 24;
        int childSecOff = fileSecOff + nFile * 80;
        int strSecOff = childSecOff + childArr.Length * 4;
        byte[] toc = new byte[strSecOff + strtab.Length];

        Encoding.ASCII.GetBytes("TOC\x01").CopyTo(toc, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(4, 4), nPkg);
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(8, 8), nDir);
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(16, 8), nFile);
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(24, 8), (ulong)strSecOff);
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(32, 8), (ulong)strtab.Length);

        // Pkg[0]
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(pkgSecOff, 8), NupArchive.Xc2Switch2TitleId);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(pkgSecOff + 8, 4), 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(pkgSecOff + 0x20, 8), offMeta);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(pkgSecOff + 0x28, 4), (uint)metaBuf.Length);
        // Pkg[1]
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(pkgSecOff + 0x40, 8), 0x0100E95004039001UL);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(pkgSecOff + 0x40 + 8, 4), 1u);

        void WriteDir(int idx, (uint o, uint l) nm, uint fc, uint cc)
        {
            int o = dirSecOff + idx * 24;
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o, 4), nm.o);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o + 4, 4), nm.l);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o + 8, 4), fc);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o + 12, 4), cc);
        }
        WriteDir(0, sEmpty, 0, 2);
        WriteDir(1, sEmpty, 2, 1);
        WriteDir(2, sExefs, 3, 2);
        WriteDir(3, sRomfs, 5, 1);
        WriteDir(4, sRomfs, 6, 1);

        void WriteFile(int idx, (uint o, uint l) nm, byte[] payload, ulong boff)
        {
            int o = fileSecOff + idx * 80;
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o, 4), nm.o);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o + 4, 4), nm.l);
            BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(o + 8, 8), (ulong)payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(o + 16, 8), boff);
            BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(o + 24, 8), (ulong)payload.Length);
            SHA256.HashData(payload).CopyTo(toc, o + 32);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(o + 64, 4), 0u);
        }
        WriteFile(0, sMain, mainBytes, offMain);
        WriteFile(1, sSub1, subsdk1Bytes, offSub1);
        WriteFile(2, sHello, romFileBytes, offRom);
        WriteFile(3, sTorna, dlcFileBytes, offDlc);

        for (int i = 0; i < childArr.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(childSecOff + i * 4, 4), childArr[i]);
        strtab.CopyTo(toc, strSecOff);

        byte[] hdr = new byte[0x80];
        Encoding.ASCII.GetBytes("NUP\x01").CopyTo(hdr, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x08, 8), tocOff);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x10, 8), (ulong)toc.Length);
        SHA256.HashData(toc).CopyTo(hdr, 0x18);

        using (var fs = File.Create(nupPath))
        {
            fs.Write(hdr);
            fs.Write(metaBuf);
            fs.Write(mainBytes);
            fs.Write(subsdk1Bytes);
            fs.Write(romFileBytes);
            fs.Write(dlcFileBytes);
            fs.Write(toc);
        }

        // Open, inspect, extract, and build RomFS
        using (var arc = NupArchive.Open(nupPath))
        {
            var insp = arc.Inspect();
            Assert(insp.IsXenoblade2Switch2, "Title ID matched XC2 SW2");
            Assert(insp.DisplayVersion == "3.0.1", $"Expected 3.0.1, got {insp.DisplayVersion}");
            Assert(insp.PackageCount == 2 && insp.TotalFileCount == 4, $"Pkg={insp.PackageCount}, Files={insp.TotalFileCount}");

            var dataDir = Path.Combine(outDir, "xenoblade2_data");
            var (files, _) = arc.ExtractBasePackage(dataDir);
            Assert(files == 3, $"Expected 3 base files, got {files}");
            Assert(File.ReadAllBytes(Path.Combine(dataDir, "exefs", "main")).SequenceEqual(mainBytes), "exefs/main exact");
            Assert(File.ReadAllBytes(Path.Combine(dataDir, "exefs_host_bypass", "subsdk1")).SequenceEqual(subsdk1Bytes), "subsdk1 routed to exefs_host_bypass");
            Assert(!File.Exists(Path.Combine(dataDir, "exefs", "subsdk1")), "subsdk1 not placed in exefs");
            Assert(File.ReadAllBytes(Path.Combine(dataDir, "romfs", "hello.txt")).SequenceEqual(romFileBytes), "romfs/hello.txt exact");

            var romfsPath = Path.Combine(dataDir, "addons", "0100e95004039001.romfs");
            var built = arc.BuildAddonRomfs(1, romfsPath);
            Assert(built.FileCount == 1 && built.DirCount == 1, $"RomFS counts {built.FileCount}/{built.DirCount}");
            var romBytes = File.ReadAllBytes(romfsPath);
            Assert(BinaryPrimitives.ReadUInt64LittleEndian(romBytes.AsSpan(0, 8)) == 0x50UL, "RomFS header size == 0x50");
            ulong dataOff = BinaryPrimitives.ReadUInt64LittleEndian(romBytes.AsSpan(72, 8));
            Assert(romBytes.AsSpan((int)dataOff, dlcFileBytes.Length).SequenceEqual(dlcFileBytes), "RomFS payload exact");
        }

        // Preflight check via NativeSetupPipeline
        var pipeline = new NativeSetupPipeline();
        var pre = pipeline.Inspect(nupPath, outDir, projectRoot);
        Assert(pipeline.IsAvailable, "NativeSetupPipeline is available");
        Assert(pre.Nup.IsXenoblade2Switch2 && pre.Nup.DisplayVersion == "3.0.1", "Preflight inspected NUP");

        // Corrupt 1 byte in TOC and assert SHA-256 rejection
        var badPath = Path.Combine(tempRoot, "corrupt.nup");
        byte[] rawNup = File.ReadAllBytes(nupPath);
        rawNup[^1] ^= 0xFF;
        File.WriteAllBytes(badPath, rawNup);
        bool rejected = false;
        try { using var _ = NupArchive.Open(badPath); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Corrupted TOC SHA-256 must be rejected");
    });

    Check("GameInstall.DiscoverInstallDirs prefers Production (games) before work/test", () =>
    {
        var fakeRoot = Path.Combine(tempRoot, "fake_root");
        var gamesDir = Path.Combine(fakeRoot, "games");
        var testDir = Path.Combine(fakeRoot, "work", "test");
        Directory.CreateDirectory(gamesDir);
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(gamesDir, "xenoblade2.exe"), "prod");
        File.WriteAllText(Path.Combine(testDir, "xenoblade2.exe"), "test");

        var dirs = GameInstall.DiscoverInstallDirs(fakeRoot);
        Assert(dirs.Length == 2, $"Expected 2 dirs, got {dirs.Length}");
        Assert(dirs[0].Equals(gamesDir, StringComparison.OrdinalIgnoreCase), $"Expected games first, got {dirs[0]}");
        Assert(dirs[1].Equals(testDir, StringComparison.OrdinalIgnoreCase), $"Expected work/test second, got {dirs[1]}");

        var realProdPath = Path.Combine(projectRoot, "games");
        if (File.Exists(Path.Combine(realProdPath, "xenoblade2.exe")))
        {
            var realProd = GameInstall.Inspect(realProdPath, projectRoot, computeHashes: false);
            Assert(realProd.IsReadyToPlay, "Production games/ should be ready to play after promotion");
            Assert(realProd.HasNacp, $"Production games/ checks: nacp={realProd.HasNacp}");
        }

        var realNup = Environment.GetEnvironmentVariable("XC2_TEST_NUP");
        if (!string.IsNullOrWhiteSpace(realNup) && File.Exists(realNup))
        {
            var pre = new NativeSetupPipeline().Inspect(realNup, Path.Combine(projectRoot, "games"), projectRoot);
            Assert(pre.Nup.IsXenoblade2Switch2, "Real NUP Title ID verified");
            Assert(pre.Nup.DisplayVersion == "3.0.1", $"Real NUP version={pre.Nup.DisplayVersion}");
            Assert(pre.Nup.PackageCount == 8, $"Real NUP packages={pre.Nup.PackageCount}");
            Assert(pre.Nup.ZstdAvailable, "libzstd.dll loaded for real NUP");
            Assert(pre.ReadyToRun, $"Real NUP ReadyToRun=false: {pre.BlockingReason}");
            Console.WriteLine($"       Real NUP OK: {pre.Nup.GameTitle} v{pre.Nup.DisplayVersion} · {pre.Nup.PackageCount} pkgs · {pre.Nup.TotalFileCount:N0} files · Zstd={Path.GetFileName(pre.Nup.ZstdPath)}");
        }
    });

    Check("GameRunner.RecommendPreset & LauncherConfig onboarding fields cover multi-vendor hardware cleanly", () =>
    {
        var baseInst = GameInstall.Inspect(testInstallDir, projectRoot, computeHashes: false);

        var rtx1440 = new HardwareInfo("NVIDIA GeForce RTX 5070", "NVIDIA", true, 12.0, 2560, 1440, 144, 32, 100, 60, "F:", true);
        var rec1 = GameRunner.RecommendPreset(rtx1440, baseInst);
        Assert(rec1.RecommendedPresetId == "120_1440_dlaa", $"Expected 120_1440_dlaa, got {rec1.RecommendedPresetId}");
        Assert(rec1.UpscalerLabel.Contains("DLSS"), $"Expected DLSS label, got {rec1.UpscalerLabel}");
        Assert(rec1.DiskWarning == null, "Healthy disk should not warn");

        var rtx4k = new HardwareInfo("NVIDIA GeForce RTX 4090", "NVIDIA", true, 24.0, 3840, 2160, 120, 64, 120, 80, "F:", true);
        var rec2 = GameRunner.RecommendPreset(rtx4k, baseInst);
        Assert(rec2.RecommendedPresetId == "120_4k_dlss_qual", $"Expected 120_4k_dlss_qual, got {rec2.RecommendedPresetId}");

        var rx580 = new HardwareInfo("AMD Radeon RX 580", "AMD", false, 4.0, 1920, 1080, 60, 16, 12, 8, "F:", false);
        var rec3 = GameRunner.RecommendPreset(rx580, baseInst with { HasDlss = false });
        Assert(rec3.RecommendedPresetId == "60_1080_dlaa", $"Expected 60_1080_dlaa, got {rec3.RecommendedPresetId}");
        Assert(rec3.UpscalerLabel.Contains("FSR 3.1"), $"Expected FSR 3.1 label, got {rec3.UpscalerLabel}");
        Assert(rec3.DiskWarning != null, "Low disk should populate warning");

        var mid1080 = new HardwareInfo("Intel Arc A750", "Intel", false, 8.0, 1920, 1080, 144, 16, 80, 40, "F:", true);
        var rec4 = GameRunner.RecommendPreset(mid1080, baseInst with { HasDlss = false });
        Assert(rec4.RecommendedPresetId == "120_1080_dlaa", $"Expected 120_1080_dlaa, got {rec4.RecommendedPresetId}");
        Assert(rec4.UpscalerLabel.Contains("XeSS"), $"Expected XeSS label, got {rec4.UpscalerLabel}");

        // Config onboarding persistence round-trip
        var cfgPath = Path.Combine(tempRoot, "onboarding_config.json");
        var fresh = LauncherConfig.Load(cfgPath);
        Assert(!fresh.OnboardingCompleted && fresh.OnboardingVersion == 0, "Fresh config defaults to onboarding incomplete");
        fresh.OnboardingCompleted = true;
        fresh.OnboardingVersion = 1;
        fresh.Save(cfgPath);
        var reloaded = LauncherConfig.Load(cfgPath);
        Assert(reloaded.OnboardingCompleted && reloaded.OnboardingVersion == 1, "Onboarding completion persisted");

        Assert(SaveManager.InferEmulatorName(@"C:\Users\X\AppData\Roaming\Ryujinx\bis\user\save") == "Ryujinx", "Infer Ryujinx");
        Assert(SaveManager.InferEmulatorName(@"C:\Users\X\AppData\Roaming\eden\nand\user\save") == "Eden", "Infer Eden");
    });

    Check("DlssDownloader verifies official NVIDIA Authenticode signature and rejects unsigned DLLs", () =>
    {
        var fakeDll = Path.Combine(tempRoot, "fake_nvngx_dlss.dll");
        File.WriteAllBytes(fakeDll, new byte[6_000_000]);
        bool rejected = false;
        try { DlssDownloader.VerifyNvidiaDll(fakeDll); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unsigned/corrupt DLL must be rejected");

        var realDll = Path.Combine(projectRoot, "games", "nvngx_dlss.dll");
        if (File.Exists(realDll))
        {
            var (ver, signer, sz) = DlssDownloader.VerifyNvidiaDll(realDll);
            Assert(signer.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase), $"Unexpected signer: {signer}");
            Assert(!string.IsNullOrWhiteSpace(ver) && sz > 20_000_000, $"Unexpected DLSS metadata: v{ver}, {sz}");
            Console.WriteLine($"       Verified local NVIDIA DLSS v{ver} ({sz / 1048576.0:F1} MiB) · {signer[..Math.Min(42, signer.Length)]}");
        }
    });

    Check("ShaderCatalogBuilder builds SNXS v1 directly from romfs + addons with exact count parity", () =>
    {
        var realData = Path.Combine(projectRoot, "games", "xenoblade2_data");
        if (!File.Exists(Path.Combine(realData, "romfs", "bf2.ard"))) return;

        var outSnxs = Path.Combine(tempRoot, "test_shaders.snxs");
        var res = ShaderCatalogBuilder.Build(realData, outSnxs, null, CancellationToken.None);
        Assert(File.Exists(outSnxs), "SNXS file created");
        Assert(res.UniqueShaders > 9000, $"Expected >9000 unique shaders, got {res.UniqueShaders}");

        var header = File.ReadAllBytes(outSnxs).AsSpan(0, 24);
        Assert(Encoding.ASCII.GetString(header.Slice(0, 4)) == "SNXS", "SNXS magic");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) == 1u, "SNXS v1");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)) == (uint)res.UniqueShaders, "SNXS count header matches");

        var existingSnxs = Path.Combine(projectRoot, "games", "shader_cache", "xc2_shaders.snxs");
        if (File.Exists(existingSnxs))
        {
            using var fs = File.OpenRead(existingSnxs);
            Span<byte> eh = stackalloc byte[16];
            fs.ReadExactly(eh);
            int expectedCount = BinaryPrimitives.ReadInt32LittleEndian(eh.Slice(8, 4));
            Assert(res.UniqueShaders == expectedCount,
                $"Shader count parity mismatch: C# built {res.UniqueShaders}, reference has {expectedCount}");
        }
        Console.WriteLine($"       ShaderCatalogBuilder C# parity OK: {res.UniqueShaders:N0} shaders ({res.SizeBytes / 1048576.0:F1} MiB) in {res.ElapsedSeconds:F1}s");
    });

    Check("Launcher restores a complete original-menu overlay after a legacy update", () =>
    {
        var root = Path.Combine(tempRoot, "legacy-menu-update");
        var runtime = Path.Combine(root, "runtime");
        var games = Path.Combine(root, "games");
        var installed = Path.Combine(games, "xenoblade2_data", "pc_ui");
        Directory.CreateDirectory(installed);
        var files = new[] { "original_menu/base_archive.txt", "original_menu/romfs/bf2.arh", "original_menu/romfs/bf2.ard.append" };
        string Portable(string file) => Path.Combine(runtime, "pc_ui", file);
        Stream OpenResource(string file) => new MemoryStream(Encoding.UTF8.GetBytes(file));
        Directory.CreateDirectory(Path.GetDirectoryName(Portable(files[0]))!);
        File.WriteAllText(Path.Combine(runtime, "xenoblade2.exe"), "UPDATED_RUNTIME");
        File.WriteAllText(Path.Combine(games, "xenoblade2.exe"), "UPDATED_RUNTIME");
        File.WriteAllText(Portable(files[0]), "PARTIAL_OLD_OVERLAY");

        bool failed = false;
        try
        {
            LauncherUpdater.SyncBundledRuntimeIfOutdated(root, games, file => file == files[2] ? null : OpenResource(file));
        }
        catch (InvalidDataException) { failed = true; }
        Assert(failed && File.ReadAllText(Portable(files[0])) == "PARTIAL_OLD_OVERLAY" &&
               !File.Exists(Portable(files[1])) && !File.Exists(Path.Combine(installed, files[0])),
               "A missing resource must leave the partial old overlay and active install untouched");

        var (copied, _) = LauncherUpdater.SyncBundledRuntimeIfOutdated(root, games, OpenResource);
        Assert(copied == 3, "The first start after a legacy update must install the whole overlay");
        foreach (var file in files)
            Assert(File.ReadAllText(Portable(file)) == file && File.ReadAllText(Path.Combine(installed, file)) == file,
                   $"Both locations must contain the exact bundled {file}");
        var (copiedAgain, _) = LauncherUpdater.SyncBundledRuntimeIfOutdated(root, games,
            _ => throw new InvalidOperationException("Complete portable overlay must take precedence"));
        Assert(copiedAgain == 0, "The next start must be a no-op");
    });

    Check("LauncherUpdater compares SemVer, parses GitHub release JSON/SHA256SUMS, blocks Zip-Slip, merges hints, and preserves user data", () =>
    {
        Assert(LauncherUpdater.CompareVersions("v0.2.0", "0.1.0") > 0, "0.2.0 > 0.1.0");
        Assert(LauncherUpdater.CompareVersions("v0.10.0", "0.2.0") > 0, "0.10.0 > 0.2.0");
        Assert(LauncherUpdater.CompareVersions("v0.2.0", "0.2.0") == 0, "v0.2.0 == 0.2.0");
        Assert(LauncherUpdater.CompareVersions("0.2.0-beta.1", "0.2.0") < 0, "prerelease < release");

        const string sampleRelease = """
        {
          "tag_name": "v0.2.0",
          "name": "XC2Recomp v0.2.0",
          "body": "- Fix AMD black screen\n- Fix Tiger Tiger 24 FPS\n- Fix background gamepad",
          "html_url": "https://github.com/nyx2sw/XC2Recomp/releases/tag/v0.2.0",
          "published_at": "2026-11-25T23:55:00Z",
          "assets": [
            { "name": "SHA256SUMS.txt", "size": 96, "browser_download_url": "https://example.invalid/SHA256SUMS.txt" },
            { "name": "XC2Recomp-v0.2.0-win-x64.zip", "size": 124000000, "browser_download_url": "https://example.invalid/XC2Recomp-v0.2.0-win-x64.zip" }
          ]
        }
        """;
        var chk = LauncherUpdater.ParseReleaseJson(sampleRelease, "0.1.0");
        Assert(chk.HasUpdate && chk.LatestTag == "v0.2.0", "ParseReleaseJson detects newer version");
        Assert(chk.AssetName == "XC2Recomp-v0.2.0-win-x64.zip", "Selects win-x64 zip asset");
        Assert(chk.ChecksumsUrl == "https://example.invalid/SHA256SUMS.txt", "Selects SHA256SUMS.txt");
        var chkSame = LauncherUpdater.ParseReleaseJson(sampleRelease, "0.2.0");
        Assert(!chkSame.HasUpdate, "Same version reports no update");

        string fakeHash = new string('a', 64);
        Assert(LauncherUpdater.ParseExpectedSha256($"{fakeHash}  XC2Recomp-v0.2.0-win-x64.zip\n", "XC2Recomp-v0.2.0-win-x64.zip") == fakeHash,
            "SHA256SUMS parser extracts matching hash");
        Assert(LauncherUpdater.ParseExpectedSha256($"{fakeHash}  other.zip\n", chk.AssetName) == null,
            "SHA256SUMS must identify this zip");
        Assert(LauncherUpdater.ParseExpectedSha256(fakeHash, chk.AssetName) == null &&
               LauncherUpdater.ParseExpectedSha256(fakeHash, chk.AssetName, allowBareHash: true) == fakeHash,
            "Bare SHA-256 is only valid for a checksum asset selected for this zip");

        bool checksumRequired = false;
        try
        {
            LauncherUpdater.DownloadAndApplyAsync(chk with { ChecksumsUrl = null }, tempRoot, tempRoot, null, null,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException) { checksumRequired = true; }
        Assert(checksumRequired, "Updating without a checksum manifest must fail before downloading the zip");

        // Zip-Slip guard test
        var badZip = Path.Combine(tempRoot, "zipslip.zip");
        using (var za = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            var e = za.CreateEntry("../escape.txt");
            using var sw = new StreamWriter(e.Open());
            sw.Write("pwn");
        }
        bool zipSlipBlocked = false;
        try { LauncherUpdater.ExtractZipSafe(badZip, Path.Combine(tempRoot, "zipslip_out")); }
        catch (InvalidDataException) { zipSlipBlocked = true; }
        Assert(zipSlipBlocked, "Zip-Slip traversal entry must be rejected");

        // End-to-end staged update apply preserving user ROM/save/INI/DLSS/custom mods & merging hints
        var fakeRoot = Path.Combine(tempRoot, "upd_user");
        var fakeGames = Path.Combine(fakeRoot, "games");
        var fakeRuntime = Path.Combine(fakeRoot, "runtime");
        var fakeData = Path.Combine(fakeGames, "xenoblade2_data");
        Directory.CreateDirectory(Path.Combine(fakeData, "romfs"));
        Directory.CreateDirectory(Path.Combine(fakeData, "save", "user"));
        Directory.CreateDirectory(Path.Combine(fakeData, "pc_ui"));
        Directory.CreateDirectory(Path.Combine(fakeData, "localization", "pt-br", "romfs"));
        Directory.CreateDirectory(Path.Combine(fakeData, "localization", "custom_mod"));
        Directory.CreateDirectory(fakeRuntime);

        File.WriteAllText(Path.Combine(fakeRoot, "Xenoblade2Launcher.exe"), "OLD_LAUNCHER");
        File.WriteAllText(Path.Combine(fakeRuntime, "xenoblade2.exe"), "OLD_RT_EXE");
        File.WriteAllText(Path.Combine(fakeGames, "xenoblade2.exe"), "OLD_GAME_EXE");
        File.WriteAllText(Path.Combine(fakeGames, "nvngx_dlss.dll"), "USER_NVIDIA_DLSS");
        File.WriteAllText(Path.Combine(fakeGames, "xenoblade2.ini"), "fps_limit=120\n");
        File.WriteAllText(Path.Combine(fakeGames, "xenoblade2.hints"), "8000af4\n8750000\n");
        File.WriteAllText(Path.Combine(fakeData, "romfs", "bf2.ard"), "USER_ROM_15GB");
        File.WriteAllText(Path.Combine(fakeData, "save", "user", "bf2savefile.sav"), "USER_SAVE_DATA");
        File.WriteAllText(Path.Combine(fakeData, "pc_ui", "theme.bin"), "USER_THEME");
        File.WriteAllText(Path.Combine(fakeData, "pc_ui", "native_ui.dll"), "OLD_UI_DLL");
        File.WriteAllText(Path.Combine(fakeData, "localization", "pt-br", "romfs", "bf2.arh"), "OLD_PTBR");
        File.WriteAllText(Path.Combine(fakeData, "localization", "custom_mod", "keep.txt"), "CUSTOM_MOD");

        // Build staged update package
        var staged = Path.Combine(tempRoot, "upd_pkg");
        Directory.CreateDirectory(Path.Combine(staged, "runtime", "pc_ui"));
        Directory.CreateDirectory(Path.Combine(staged, "runtime", "localization", "pt-br", "romfs"));
        File.WriteAllText(Path.Combine(staged, "Xenoblade2Launcher.exe"), "NEW_LAUNCHER_V020");
        File.WriteAllText(Path.Combine(staged, "runtime", "xenoblade2.exe"), "NEW_SNX_EXE_V020");
        File.WriteAllText(Path.Combine(staged, "runtime", "snx_dlss.dll"), "NEW_SNX_DLSS");
        File.WriteAllText(Path.Combine(staged, "runtime", "xenoblade2.hints"), "8000af4\n8756b00\n8758a00\n");
        File.WriteAllText(Path.Combine(staged, "runtime", "pc_ui", "native_ui.dll"), "NEW_UI_DLL");
        File.WriteAllText(Path.Combine(staged, "runtime", "localization", "pt-br", "romfs", "bf2.arh"), "NEW_PTBR");

        var applyRes = LauncherUpdater.ApplyStagedDirectory(
            staged, fakeRoot, fakeGames, Path.Combine(fakeRoot, "Xenoblade2Launcher.exe"), "v0.2.0", allowSelfReplace: true);

        Assert(applyRes.Success && applyRes.LauncherReplaced, "Update succeeded and replaced launcher");
        Assert(applyRes.AddedHintsCount == 2 && applyRes.TotalHintsCount == 4, $"Hints union merged: +{applyRes.AddedHintsCount}, total={applyRes.TotalHintsCount}");
        Assert(File.ReadAllText(Path.Combine(fakeRoot, "Xenoblade2Launcher.exe")) == "NEW_LAUNCHER_V020", "Launcher exe swapped");
        Assert(File.ReadAllText(Path.Combine(fakeRoot, "Xenoblade2Launcher.exe.old")) == "OLD_LAUNCHER", "Old launcher moved to .old");
        Assert(File.ReadAllText(Path.Combine(fakeGames, "xenoblade2.exe")) == "NEW_SNX_EXE_V020", "Game runtime exe updated");
        Assert(File.ReadAllText(Path.Combine(fakeRuntime, "xenoblade2.exe")) == "NEW_SNX_EXE_V020", "Portable runtime exe updated");
        Assert(File.ReadAllText(Path.Combine(fakeData, "pc_ui", "native_ui.dll")) == "NEW_UI_DLL", "native_ui.dll updated");
        Assert(File.ReadAllText(Path.Combine(fakeData, "localization", "pt-br", "romfs", "bf2.arh")) == "NEW_PTBR", "PT-BR pack updated");

        // Strictly preserved user data assertions
        Assert(File.ReadAllText(Path.Combine(fakeGames, "nvngx_dlss.dll")) == "USER_NVIDIA_DLSS", "nvngx_dlss.dll preserved");
        Assert(File.ReadAllText(Path.Combine(fakeGames, "xenoblade2.ini")) == "fps_limit=120\n", "xenoblade2.ini preserved");
        Assert(File.ReadAllText(Path.Combine(fakeData, "romfs", "bf2.ard")) == "USER_ROM_15GB", "bf2.ard preserved");
        Assert(File.ReadAllText(Path.Combine(fakeData, "save", "user", "bf2savefile.sav")) == "USER_SAVE_DATA", "save preserved");
        Assert(File.ReadAllText(Path.Combine(fakeData, "pc_ui", "theme.bin")) == "USER_THEME", "theme.bin preserved");
        Assert(File.ReadAllText(Path.Combine(fakeData, "localization", "custom_mod", "keep.txt")) == "CUSTOM_MOD", "custom mod preserved");

        LauncherUpdater.CleanupPreviousBinary(Path.Combine(fakeRoot, "Xenoblade2Launcher.exe"));
        Assert(!File.Exists(Path.Combine(fakeRoot, "Xenoblade2Launcher.exe.old")), ".old cleaned up on startup");

        // SyncBundledRuntimeIfOutdated: simulates user manually extracting a new ZIP over an old v0.1.0 games/ dir
        File.WriteAllText(Path.Combine(fakeGames, "xenoblade2.exe"), "STALE_V010_EXE");
        File.WriteAllText(Path.Combine(fakeData, "pc_ui", "native_ui.dll"), "STALE_V010_UI");
        var (syncN1, syncH1) = LauncherUpdater.SyncBundledRuntimeIfOutdated(fakeRoot, fakeGames);
        Assert(syncN1 == 2 && syncH1 == 0, $"SyncBundledRuntimeIfOutdated should update exactly 2 stale files, got {syncN1} (+{syncH1} hints)");
        Assert(File.ReadAllText(Path.Combine(fakeGames, "xenoblade2.exe")) == "NEW_SNX_EXE_V020", "Stale games/xenoblade2.exe auto-upgraded from runtime/");
        Assert(File.ReadAllText(Path.Combine(fakeData, "pc_ui", "native_ui.dll")) == "NEW_UI_DLL", "Stale native_ui.dll auto-upgraded from runtime/");
        Assert(File.ReadAllText(Path.Combine(fakeGames, "nvngx_dlss.dll")) == "USER_NVIDIA_DLSS", "nvngx_dlss.dll still preserved after runtime sync");
        Assert(File.ReadAllText(Path.Combine(fakeData, "save", "user", "bf2savefile.sav")) == "USER_SAVE_DATA", "save still preserved after runtime sync");

        // Second call must be a 0-write no-op
        var (syncN2, syncH2) = LauncherUpdater.SyncBundledRuntimeIfOutdated(fakeRoot, fakeGames);
        Assert(syncN2 == 0 && syncH2 == 0, $"Second SyncBundledRuntimeIfOutdated must be 0-write no-op, got {syncN2}");

        // A directory at a late target fails after earlier files have already been replaced.
        string lateTarget = Path.Combine(fakeData, "pc_ui", "presence.dll");
        Directory.CreateDirectory(lateTarget);
        var newPack = Path.Combine(staged, "runtime", "localization", "pt-br-jp", "romfs");
        Directory.CreateDirectory(newPack);
        File.WriteAllText(Path.Combine(newPack, "bf2.arh"), "NEW_JP_PACK");
        File.WriteAllText(Path.Combine(staged, "runtime", "pc_ui", "presence.dll"), "NEW_PRESENCE");
        File.WriteAllText(Path.Combine(staged, "runtime", "xenoblade2.exe"), "NEXT_EXE");
        File.WriteAllText(Path.Combine(staged, "runtime", "xenoblade2.hints"), "new_local_release_hint\n");
        bool failedLate = false;
        try
        {
            LauncherUpdater.ApplyStagedDirectory(staged, fakeRoot, fakeGames,
                Path.Combine(fakeRoot, "Xenoblade2Launcher.exe"), "v0.3.0");
        }
        catch (IOException) { failedLate = true; }
        Assert(failedLate, "A conflicting late destination must fail the update");
        Assert(File.ReadAllText(Path.Combine(fakeGames, "xenoblade2.exe")) == "NEW_SNX_EXE_V020" &&
               File.ReadAllText(Path.Combine(fakeRuntime, "xenoblade2.exe")) == "NEW_SNX_EXE_V020" &&
               !File.ReadAllText(Path.Combine(fakeGames, "xenoblade2.hints")).Contains("new_local_release_hint") &&
               !File.Exists(Path.Combine(fakeRuntime, "pc_ui", "presence.dll")) && Directory.Exists(lateTarget) &&
               !Directory.Exists(Path.Combine(fakeRuntime, "localization", "pt-br-jp")) &&
               !Directory.Exists(Path.Combine(fakeData, "localization", "pt-br-jp")) &&
               !File.Exists(Path.Combine(fakeRoot, ".xc2-update.json")), "Failure rolls back old and newly added files across both directories");
    });

    Check("LauncherUpdater recovers a replacement interrupted between two renames", () =>
    {
        var root = Path.Combine(tempRoot, "upd_recover");
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Xenoblade2Launcher.exe");
        var existing = Path.Combine(root, "existing.txt");
        var added = Path.Combine(root, "added.txt");
        var id = Guid.NewGuid().ToString("N");
        var journal = Path.Combine(root, ".xc2-update.json");
        File.WriteAllText(exe + ".old", "old launcher");
        File.WriteAllText(exe + $".xc2-{id}.new", "new launcher");
        File.WriteAllText(existing + $".xc2-{id}.bak", "old existing");
        File.WriteAllText(existing, "new existing");
        File.WriteAllText(added, "new file");
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = id, Phase = "applying", Files = new[]
            {
                new { Destination = existing, HadOriginal = true, Launcher = false },
                new { Destination = added, HadOriginal = false, Launcher = false },
                new { Destination = exe, HadOriginal = true, Launcher = true },
            }
        }));
        LauncherUpdater.RecoverInterruptedUpdate(root);
        Assert(File.ReadAllText(exe) == "old launcher" && File.ReadAllText(existing) == "old existing" &&
               !File.Exists(added) && !File.Exists(journal), "Interrupted replacements are recovered on the next startup");
    });
}
finally
{
    try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
}

Console.WriteLine($"\nResult: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
