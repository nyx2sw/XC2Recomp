using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace XC2.Core;

/// <summary>
/// Pure managed C# artwork extractor reading directly (strictly read-only) from the player's
/// own Xenoblade Chronicles 2 romfs/bf2.arh + romfs/bf2.ard archives:
/// - Decodes ARH1 XOR trie + XBC1 zlib streams
/// - Parses WILAY texture tables + 40-byte LBIM footers
/// - Performs Tegra X1/T239 GOB block-linear deswizzling
/// - Decodes BC1/BC3/BC4/BC7 (via BCnEncoder.Net) and RGBA8/R8 directly into RGBA buffers
/// - Generates cached PNGs and multi-size PNG-in-ICO icons under %LOCALAPPDATA%\Xenoblade2PC\media\art
/// Zero external tools, zero copyrighted game artwork distributed with the launcher.
/// </summary>
public sealed class GameArtwork
{
    const string CacheVersion = "v1";

    readonly string romfsDir;
    readonly string artCacheDir;
    readonly string steamCacheDir;
    readonly object archiveLock = new();
    readonly ConcurrentDictionary<string, object> keyLocks = new(StringComparer.OrdinalIgnoreCase);
    readonly BcDecoder bcDecoder = new();

    ArhArchive? archive;
    bool archiveLoadFailed;

    public GameArtwork(string romfsDir, string mediaCacheDir)
    {
        this.romfsDir = romfsDir;
        artCacheDir = Path.Combine(mediaCacheDir, "art");
        steamCacheDir = Path.Combine(mediaCacheDir, "steam_art");
        Directory.CreateDirectory(artCacheDir);
    }

    public bool IsAvailable =>
        File.Exists(Path.Combine(romfsDir, "bf2.arh")) &&
        File.Exists(Path.Combine(romfsDir, "bf2.ard"));

    /// <summary>
    /// Returns PNG bytes for any /media/art/<key> key expected by the launcher UI.
    /// </summary>
    public (byte[] Data, string Mime)? GetArtPng(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string safe = SanitizeKey(key);
        if (string.IsNullOrEmpty(safe)) return null;

        string cached = Path.Combine(artCacheDir, $"art_{safe}_{CacheVersion}.png");
        if (File.Exists(cached) && new FileInfo(cached).Length > 64)
            return (File.ReadAllBytes(cached), "image/png");

        if (!IsAvailable) return null;

        var gate = keyLocks.GetOrAdd("art:" + safe, _ => new object());
        lock (gate)
        {
            if (File.Exists(cached) && new FileInfo(cached).Length > 64)
                return (File.ReadAllBytes(cached), "image/png");

            try
            {
                var img = RenderArt(safe);
                if (img == null) return null;
                byte[] png = EncodePng(img);
                WriteAtomic(cached, png);
                LauncherLog.Write($"art: cached {safe} ({img.Width}x{img.Height}, {png.Length / 1024:N0} KiB)");
                return (png, "image/png");
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"art: failed '{safe}': {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Returns PNG bytes for any /media/icon/<name> native XC2 icon.
    /// </summary>
    public (byte[] Data, string Mime)? GetIconPng(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string safe = SanitizeKey(name);
        if (string.IsNullOrEmpty(safe)) return null;

        string cached = Path.Combine(artCacheDir, $"icon_{safe}_{CacheVersion}.png");
        if (File.Exists(cached) && new FileInfo(cached).Length > 64)
            return (File.ReadAllBytes(cached), "image/png");

        if (!IsAvailable) return null;

        var gate = keyLocks.GetOrAdd("icon:" + safe, _ => new object());
        lock (gate)
        {
            if (File.Exists(cached) && new FileInfo(cached).Length > 64)
                return (File.ReadAllBytes(cached), "image/png");

            try
            {
                var img = RenderIcon(safe);
                if (img == null) return null;
                byte[] png = EncodePng(img);
                WriteAtomic(cached, png);
                LauncherLog.Write($"art: cached icon/{safe} ({img.Width}x{img.Height})");
                return (png, "image/png");
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"art: failed icon/{safe}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Materializes a multi-size Windows .ico (Core Crystal) in the local media cache for shortcuts.
    /// </summary>
    public string? EnsureCrystalIcon()
    {
        string icoPath = Path.Combine(artCacheDir, $"xc2_pc_cristal_{CacheVersion}.ico");
        if (File.Exists(icoPath) && new FileInfo(icoPath).Length > 256)
            return icoPath;

        if (!IsAvailable) return null;

        var gate = keyLocks.GetOrAdd("ico:crystal", _ => new object());
        lock (gate)
        {
            if (File.Exists(icoPath) && new FileInfo(icoPath).Length > 256)
                return icoPath;

            try
            {
                var raw = DecodeWilay("/menu/image/mnu094_rootmenu_crystal.wilay");
                if (raw == null) return null;
                var trimmed = TrimAlpha(raw, minAlpha: 8, pad: 4);
                int[] sizes = [16, 24, 32, 48, 64, 128, 256];
                var frames = new List<(int Size, byte[] Png)>(sizes.Length);
                foreach (int sz in sizes)
                {
                    var sq = FitSquare(trimmed, sz, marginRatio: 0.055);
                    frames.Add((sz, EncodePng(sq)));
                }
                byte[] ico = EncodeIco(frames);
                WriteAtomic(icoPath, ico);
                LauncherLog.Write($"art: cached Core Crystal icon ({ico.Length / 1024:N0} KiB)");
                return icoPath;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"art: failed crystal ico: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Materializes the 5 Steam library grid PNGs using the exact layout expected by SteamIntegration.
    /// </summary>
    public string? EnsureSteamArtDir()
    {
        if (!IsAvailable) return null;

        (string ArtKey, string RelPath)[] items =
        [
            ("cover",   @"03_biblioteca\capa_600x900.png"),
            ("banner",  @"03_biblioteca\banner_920x430.png"),
            ("hero",    @"03_biblioteca\hero_sem_texto_1920x620.png"),
            ("logo_sm", @"02_titulo\xc2_pc_edition_640.png"),
            ("crystal", @"01_atalhos\png\xc2_cristal_256.png"),
        ];

        int ready = 0;
        foreach (var (key, rel) in items)
        {
            string dst = Path.Combine(steamCacheDir, rel);
            if (File.Exists(dst) && new FileInfo(dst).Length > 64)
            {
                ready++;
                continue;
            }
            var data = GetArtPng(key);
            if (data != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                WriteAtomic(dst, data.Value.Data);
                ready++;
            }
        }
        return ready > 0 ? steamCacheDir : null;
    }

    /// <summary>
    /// Pre-decodes high-priority launcher textures on a background thread so screens appear instantly.
    /// </summary>
    public void WarmupAsync()
    {
        if (!IsAvailable) return;
        _ = Task.Run(() =>
        {
            try
            {
                string[] priorityArt =
                [
                    "keyart_bg", "logo_official", "crystal", "menu_bg",
                    "torna_bg", "logo_torna", "save_rex"
                ];
                foreach (var k in priorityArt) GetArtPng(k);
                foreach (var ic in new[] { "cristal", "salvar", "bolsa", "opcoes", "tela", "livros" })
                    GetIconPng(ic);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("art warmup: " + ex.Message);
            }
        });
    }

    // ==================== Artwork & Icon Recipes ====================

    RgbaImage? RenderArt(string key) => key switch
    {
        "keyart_bg" => ResizeMaxWidth(DecodeWilay("/menu/image/dlc3_mnu105_bg_image.wilay"), 1920),
        "torna_bg"  => ResizeMaxWidth(DecodeWilay("/menu/image/mnu105_bg_image_05.wilay"), 1920),
        "title_bg"  => RenderTitleBg(),

        "logo_official" or "logo" => RenderOfficialLogo(1280),
        "logo_sm"                 => RenderOfficialLogo(640),
        "logo_torna"              => RenderTornaLogo(1280),

        "dlc_crest"   => ResizeExact(DecodeWilay("/menu/image/mnu105_bg_image_00.wilay"), 1160, 688),
        "dlc_items"   => ResizeExact(DecodeWilay("/menu/image/mnu105_bg_image_01.wilay"), 1160, 688),
        "dlc_quests"  => ResizeExact(DecodeWilay("/menu/image/mnu105_bg_image_02.wilay"), 1160, 688),
        "dlc_blades"  => ResizeExact(DecodeWilay("/menu/image/mnu105_bg_image_03.wilay"), 1160, 688),
        "dlc_battle"  => ResizeExact(DecodeWilay("/menu/image/mnu105_bg_image_04.wilay"), 1160, 688),
        "argentum_bg" => ResizeMaxWidth(DecodeWilay("/menu/image/mnu010_bg.wilay"), 1280),

        "save_rex"    => ResizeMaxDim(TrimAlphaOrNull(DecodeWilay("/menu/image/mnu094_rootmenu_save.wilay")), 420),
        "art_blades"  => ResizeMaxDim(DecodeWilay("/menu/image/mnu094_rootmenu_blades.wilay"), 1024),
        "art_options" => ResizeMaxDim(TrimAlphaOrNull(DecodeWilay("/menu/image/mnu094_rootmenu_option.wilay")), 512),

        "menu_tex"    => RenderMenuTexture(),
        "menu_bg"     => RenderMenuBackground(1920, 1080),

        "crystal" or "classic" => RenderSquareSprite("/menu/image/mnu094_rootmenu_crystal.wilay", 256, 0.055),

        "cover"       => RenderSteamCover(),
        "banner"      => RenderSteamBanner(),
        "hero"        => RenderSteamHero(),

        "root_ribbon" => ResizeMaxWidth(DecodeWilay("/menu/image/mnu094_rootmenu_bg01.wilay"), 1280),
        "hex_bar"     => DecodeWilay("/menu/image/mnu094_rootmenu_base.wilay"),
        "opt_tab"     => DecodeWilay("/menu/image/mnu080_option_tab.wilay"),
        "opt_slide"   => DecodeWilay("/menu/image/mnu080_option_tab_slide.wilay"),
        "win_sys"     => DecodeWilay("/menu/image/cmn_window_system.wilay"),
        "win_blade"   => DecodeWilay("/menu/image/cmn_window_blade.wilay"),
        _ => null
    };

    RgbaImage? RenderIcon(string name) => name switch
    {
        "cristal" => RenderSquareSprite("/menu/image/mnu094_rootmenu_crystal.wilay", 128, 0.06),
        "salvar"  => RenderSquareSprite("/menu/image/mnu094_rootmenu_save.wilay", 128, 0.06),
        "opcoes"  => RenderSquareSprite("/menu/image/mnu094_rootmenu_option.wilay", 128, 0.06),
        "horario" => RenderSquareSprite("/menu/image/mnu094_rootmenu_skiptime.wilay", 128, 0.06),

        "som"           => RenderAtlasCell("/menu/image/mnu080_submenu_icon.wilay", 5, 2, 0, 0, 128),
        "camera"        => RenderAtlasCell("/menu/image/mnu080_submenu_icon.wilay", 5, 2, 1, 0, 128),
        "tela"          => RenderAtlasCell("/menu/image/mnu080_submenu_icon.wilay", 5, 2, 2, 0, 128),
        "configuracoes" => RenderAtlasCell("/menu/image/mnu080_submenu_icon.wilay", 5, 2, 3, 0, 128),

        "bolsa"       => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 0, 0, 128),
        "pergaminho"  => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 1, 0, 128),
        "personagens" => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 2, 0, 128),
        "livros"      => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 3, 0, 128),
        "vestuario"   => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 4, 0, 128),
        "viagem"      => RenderAtlasCell("/menu/image/mnu094_rootmenu_icon.wilay", 6, 1, 5, 0, 128),
        _ => null
    };

    RgbaImage? RenderTitleBg()
    {
        var raw = DecodeWilay("/menu/image/mnu001_titlebg.wilay");
        if (raw == null) return null;
        // Trim black vertical pillarboxes (ratio 131/1280 on each side)
        int margin = (int)Math.Round(raw.Width * (131.0 / 1280.0));
        int cropW = Math.Max(1, raw.Width - margin * 2);
        var cropped = Crop(raw, margin, 0, cropW, raw.Height);
        return ResizeMaxWidth(cropped, 1280);
    }

    RgbaImage? RenderOfficialLogo(int maxWidth)
    {
        var raw = DecodeWilay("/menu/image/mnu001_titlelogo_us.wilay");
        if (raw == null) return null;

        // In Switch 2 Edition 3.0.1, mnu001_titlelogo_us includes a red "Nintendo Switch 2 Edition"
        // ribbon separated below the main logo by a transparent horizontal band (~78% height).
        int cutY = FindSwitch2RibbonGapY(raw);
        var mainOnly = cutY > 0 ? Crop(raw, 0, 0, raw.Width, cutY) : raw;
        var trimmed = TrimAlpha(mainOnly, minAlpha: 4, pad: 8);
        return ResizeMaxWidth(trimmed, maxWidth);
    }

    RgbaImage? RenderTornaLogo(int maxWidth)
    {
        var raw = DecodeWilay("/menu/image/dlc3_mnu001_titlelogo_us.wilay");
        if (raw == null) return null;
        var trimmed = TrimAlpha(raw, minAlpha: 4, pad: 8);
        return ResizeMaxWidth(trimmed, maxWidth);
    }

    RgbaImage? RenderMenuTexture()
    {
        var raw = DecodeWilay("/menu/image/mnu094_rootmenu_bg03.wilay");
        if (raw == null) return null;
        int x0 = (int)Math.Round(raw.Width * 0.025);
        int y0 = (int)Math.Round(raw.Height * 0.262);
        int w = raw.Width - x0 * 2;
        int h = (int)Math.Round(raw.Height * 0.476);
        return Resize(Crop(raw, x0, y0, w, h), 1216, 266);
    }

    RgbaImage? RenderMenuBackground(int outW, int outH)
    {
        var raw = DecodeWilay("/menu/image/mnu094_rootmenu_bg03.wilay");
        if (raw == null) return null;
        // Sample clean blue diamond-lattice interior (avoiding the BC3 ring around the left emblem)
        int x0 = (int)Math.Round(raw.Width * 0.38);
        int y0 = (int)Math.Round(raw.Height * 0.265);
        int w = (int)Math.Round(raw.Width * 0.54);
        int h = (int)Math.Round(raw.Height * 0.470);
        var scaled = Resize(Crop(raw, x0, y0, w, h), outW, outH);
        // Add subtle soft-blue radial glow on left edge matching the approved menu_azul look
        byte[] px = scaled.Rgba;
        for (int y = 0; y < outH; y++)
        {
            double dy = (y - outH * 0.52) / (outH * 0.65);
            for (int x = 0; x < outW; x++)
            {
                double dx = (x + outW * 0.04) / (outW * 0.34);
                double d = Math.Sqrt(dx * dx + dy * dy);
                double glow = Math.Clamp(1.0 - d, 0.0, 1.0);
                glow = glow * glow * 0.38;
                int idx = (y * outW + x) * 4;
                px[idx + 0] = ClampByte(px[idx + 0] + glow * (96 - px[idx + 0]));
                px[idx + 1] = ClampByte(px[idx + 1] + glow * (196 - px[idx + 1]));
                px[idx + 2] = ClampByte(px[idx + 2] + glow * (255 - px[idx + 2]));
                px[idx + 3] = 255;
            }
        }
        return scaled;
    }

    RgbaImage? RenderSteamCover()
    {
        var keyart = DecodeWilay("/menu/image/dlc3_mnu105_bg_image.wilay");
        var logo = RenderOfficialLogo(520);
        if (keyart == null) return null;

        // Portrait 2:3 crop from the right illustration half (framing World Tree, Titan, Rex & Pyra)
        int cropH = keyart.Height;
        int cropW = (int)Math.Round(cropH * (600.0 / 900.0)); // 917px wide at 1376h
        int cropX = Math.Clamp(keyart.Width - cropW - (int)(keyart.Width * 0.065), 0, keyart.Width - cropW);
        var canvas = Resize(Crop(keyart, cropX, 0, cropW, cropH), 600, 900);

        // Flatten onto deep Alrest navy + gentle top vignette for logo legibility
        byte[] px = canvas.Rgba;
        for (int y = 0; y < 900; y++)
        {
            double topDark = y < 320 ? Math.Pow(1.0 - y / 320.0, 1.6) * 0.52 : 0.0;
            for (int x = 0; x < 600; x++)
            {
                int i = (y * 600 + x) * 4;
                double a = px[i + 3] / 255.0;
                double r = px[i + 0] * a + 10.0 * (1.0 - a);
                double g = px[i + 1] * a + 34.0 * (1.0 - a);
                double b = px[i + 2] * a + 88.0 * (1.0 - a);
                r = r * (1.0 - topDark) + 6.0 * topDark;
                g = g * (1.0 - topDark) + 22.0 * topDark;
                b = b * (1.0 - topDark) + 64.0 * topDark;
                px[i + 0] = ClampByte(r);
                px[i + 1] = ClampByte(g);
                px[i + 2] = ClampByte(b);
                px[i + 3] = 255;
            }
        }

        if (logo != null)
        {
            int lx = (600 - logo.Width) / 2;
            AlphaOver(canvas, logo, lx, 34);
        }
        return canvas;
    }

    RgbaImage? RenderSteamHero()
    {
        var keyart = DecodeWilay("/menu/image/dlc3_mnu105_bg_image.wilay");
        var menu = RenderMenuBackground(1920, 620);
        if (keyart == null) return menu;

        var canvas = menu ?? SolidImage(1920, 620, 12, 36, 92, 255);
        DarkenToNavy(canvas, 0.52);

        int artH = 620;
        int artW = (int)Math.Round(keyart.Width * (artH / (double)keyart.Height));
        var artScaled = Resize(keyart, artW, artH);
        AlphaOver(canvas, artScaled, 1920 - artW, 0);
        return canvas;
    }

    RgbaImage? RenderSteamBanner()
    {
        var keyart = DecodeWilay("/menu/image/dlc3_mnu105_bg_image.wilay");
        var menu = RenderMenuBackground(920, 430);
        var logo = RenderOfficialLogo(390);
        if (keyart == null) return menu;

        var canvas = menu ?? SolidImage(920, 430, 12, 36, 92, 255);
        DarkenToNavy(canvas, 0.50);

        int artH = 430;
        int artW = (int)Math.Round(keyart.Width * (artH / (double)keyart.Height));
        var artScaled = Resize(keyart, artW, artH);
        AlphaOver(canvas, artScaled, 920 - artW, 0);

        if (logo != null)
        {
            int ly = (430 - logo.Height) / 2;
            AlphaOver(canvas, logo, 42, ly);
        }
        return canvas;
    }

    RgbaImage? RenderSquareSprite(string wilayPath, int size, double margin)
    {
        var raw = DecodeWilay(wilayPath);
        if (raw == null) return null;
        var trimmed = TrimAlpha(raw, minAlpha: 8, pad: 2);
        return FitSquare(trimmed, size, margin);
    }

    RgbaImage? RenderAtlasCell(string wilayPath, int cols, int rows, int col, int row, int outSize)
    {
        var raw = DecodeWilay(wilayPath);
        if (raw == null) return null;
        int cw = raw.Width / cols;
        int ch = raw.Height / rows;
        if (cw <= 0 || ch <= 0) return null;
        var cell = Crop(raw, col * cw, row * ch, cw, ch);
        var trimmed = TrimAlpha(cell, minAlpha: 8, pad: 2);
        return FitSquare(trimmed, outSize, marginRatio: 0.06);
    }

    // ==================== WILAY + LBIM + Tegra Block-Linear Decoder ====================

    RgbaImage? DecodeWilay(string virtualPath, int textureIndex = 0)
    {
        var arc = GetArchive();
        if (arc == null) return null;

        byte[]? wilay = arc.ReadFile(virtualPath);
        if (wilay == null || wilay.Length < 0x40) return null;

        int baseOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(wilay.AsSpan(0x24, 4));
        if (baseOff <= 0 || baseOff + 8 > wilay.Length) return null;

        int tableOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(wilay.AsSpan(baseOff, 4));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(wilay.AsSpan(baseOff + 4, 4));
        if (textureIndex < 0 || textureIndex >= count) return null;

        int entryPos = baseOff + tableOff + textureIndex * 12;
        if (entryPos + 12 > wilay.Length) return null;

        int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(wilay.AsSpan(entryPos + 4, 4));
        int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(wilay.AsSpan(entryPos + 8, 4));
        if (at < 0 || sz < 40 || baseOff + at + sz > wilay.Length) return null;

        return DecodeLbim(wilay.AsSpan(baseOff + at, sz));
    }

    RgbaImage DecodeLbim(ReadOnlySpan<byte> blob)
    {
        var foot = blob.Slice(blob.Length - 40, 40);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(8, 4));
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(12, 4));
        uint depth = BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(16, 4));
        uint view = BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(20, 4));
        uint fmt = BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(24, 4));
        uint mips = BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(28, 4));
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(foot.Slice(36, 4));

        if (magic != 0x4D49424Cu) // "LBIM"
            throw new InvalidDataException($"Invalid LBIM magic 0x{magic:X8}");
        if (depth != 1 || view != 1 || mips != 1 || width <= 0 || height <= 0)
            throw new InvalidDataException($"Unsupported LBIM layout {width}x{height} d={depth} v={view} m={mips}");

        int bpp = fmt switch
        {
            1 => 1,
            37 => 4,
            66 => 8,
            68 => 16,
            73 => 8,
            77 => 16,
            _ => throw new NotSupportedException($"Unsupported LBIM format {fmt}")
        };

        int bw = fmt is 1 or 37 ? width : (width + 3) / 4;
        int bhPx = fmt is 1 or 37 ? height : (height + 3) / 4;
        byte[] linear = DeswizzleTegra(blob, bw, bhPx, bpp);

        if (fmt == 37)
            return new RgbaImage(width, height, linear);

        if (fmt == 1)
        {
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int o = i * 4;
                rgba[o] = 255; rgba[o + 1] = 255; rgba[o + 2] = 255; rgba[o + 3] = linear[i];
            }
            return new RgbaImage(width, height, rgba);
        }

        CompressionFormat cf = fmt switch
        {
            66 => CompressionFormat.Bc1WithAlpha,
            68 => CompressionFormat.Bc3,
            73 => CompressionFormat.Bc4,
            77 => CompressionFormat.Bc7,
            _ => throw new NotSupportedException($"Unsupported BCn format {fmt}")
        };

        ColorRgba32[] decoded = bcDecoder.DecodeRaw(linear, width, height, cf);
        var outRgba = new byte[width * height * 4];
        if (fmt == 73)
        {
            for (int i = 0; i < decoded.Length; i++)
            {
                int o = i * 4;
                outRgba[o] = 255; outRgba[o + 1] = 255; outRgba[o + 2] = 255; outRgba[o + 3] = decoded[i].r;
            }
        }
        else
        {
            for (int i = 0; i < decoded.Length; i++)
            {
                int o = i * 4;
                var c = decoded[i];
                outRgba[o] = c.r;
                outRgba[o + 1] = c.g;
                outRgba[o + 2] = c.b;
                outRgba[o + 3] = c.a;
            }
        }
        return new RgbaImage(width, height, outRgba);
    }

    static byte[] DeswizzleTegra(ReadOnlySpan<byte> src, int w, int h, int bpp)
    {
        int gobsY = (h + 7) / 8;
        int bh = Math.Min(16, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, gobsY)));
        int gobW = (w * bpp + 63) / 64;
        int pitch = w * bpp;
        var dst = new byte[pitch * h];

        for (int y = 0; y < h; y++)
        {
            int gy = y >> 3;
            int yb = y & 7;
            int rowBase = (gy / bh * gobW) * 512 * bh + (gy % bh) * 512;
            int dstRow = y * pitch;

            for (int x = 0; x < pitch; x += 16)
            {
                int xb = x & 63;
                int off = rowBase
                    + (x >> 6) * 512 * bh
                    + ((xb >> 5) << 8)
                    + ((yb >> 1) << 6)
                    + (((xb >> 4) & 1) << 5)
                    + ((yb & 1) << 4);
                int n = Math.Min(16, pitch - x);
                if (off >= 0 && off + n <= src.Length)
                    src.Slice(off, n).CopyTo(dst.AsSpan(dstRow + x, n));
            }
        }
        return dst;
    }

    // ==================== Read-Only ARH1 + ARD + XBC1 Reader ====================

    ArhArchive? GetArchive()
    {
        if (archive != null) return archive;
        if (archiveLoadFailed) return null;
        lock (archiveLock)
        {
            if (archive != null) return archive;
            if (archiveLoadFailed) return null;
            try
            {
                string arhPath = Path.Combine(romfsDir, "bf2.arh");
                string ardPath = Path.Combine(romfsDir, "bf2.ard");
                if (!File.Exists(arhPath) || !File.Exists(ardPath)) return null;
                archive = new ArhArchive(arhPath, ardPath);
                return archive;
            }
            catch (Exception ex)
            {
                archiveLoadFailed = true;
                LauncherLog.Write($"art: failed to open bf2.arh: {ex.Message}");
                return null;
            }
        }
    }

    sealed class ArhArchive
    {
        readonly string ardPath;
        readonly byte[] rawArh;
        readonly byte[] strings;
        readonly byte[] nodes;
        readonly uint nodeCount;
        readonly int fileTableOff;
        readonly uint fileCount;

        public ArhArchive(string arhPath, string ardPath)
        {
            this.ardPath = ardPath;
            rawArh = File.ReadAllBytes(arhPath);
            if (rawArh.Length < 40 || Encoding.ASCII.GetString(rawArh, 0, 4) != "arh1")
                throw new InvalidDataException("Not an arh1 index file.");

            nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(8, 4));
            int strOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(12, 4));
            int strLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(16, 4));
            int nodeOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(20, 4));
            int nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(24, 4));
            fileTableOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(28, 4));
            fileCount = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(32, 4));
            uint keyVal = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(36, 4)) ^ 0xF3F35353u;

            Span<byte> key = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(key, keyVal);
            strings = DecodeXor(rawArh, strOff, strLen, key);
            nodes = DecodeXor(rawArh, nodeOff, nodeLen, key);
        }

        static byte[] DecodeXor(byte[] src, int off, int len, ReadOnlySpan<byte> key)
        {
            var buf = new byte[len];
            Buffer.BlockCopy(src, off, buf, 0, len);
            int aligned = (len / 4) * 4;
            for (int i = 0; i < aligned; i++)
                buf[i] ^= key[i & 3];
            return buf;
        }

        public byte[]? ReadFile(string virtualPath)
        {
            var tail = Encoding.ASCII.GetBytes(virtualPath);
            int index = 0;
            int pos = 0;
            int nxt;

            while (true)
            {
                nxt = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(index * 8, 4));
                if (nxt < 0) break;
                if (pos >= tail.Length) return null;
                int child = nxt ^ tail[pos];
                if (child < 0 || (uint)child >= nodeCount) return null;
                int parent = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(child * 8 + 4, 4));
                if (parent != index) return null;
                index = child;
                pos++;
            }

            int strStart = -nxt;
            if (strStart < 0 || strStart >= strings.Length) return null;
            int nul = Array.IndexOf(strings, (byte)0, strStart);
            if (nul < 0 || nul + 5 > strings.Length) return null;

            int rem = tail.Length - pos;
            if (nul - strStart != rem) return null;
            for (int i = 0; i < rem; i++)
                if (strings[strStart + i] != tail[pos + i]) return null;

            uint fid = BinaryPrimitives.ReadUInt32LittleEndian(strings.AsSpan(nul + 1, 4));
            if (fid >= fileCount) return null;

            int metaOff = fileTableOff + (int)fid * 24;
            ulong ardOff = BinaryPrimitives.ReadUInt64LittleEndian(rawArh.AsSpan(metaOff, 8));
            uint compSize = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 8, 4));
            uint expSize = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 12, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 16, 4));

            int readLen = (int)compSize + ((flags & 2) != 0 ? 48 : 0);
            var raw = new byte[readLen];
            using (var fs = new FileStream(ardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek((long)ardOff, SeekOrigin.Begin);
                int got = 0;
                while (got < raw.Length)
                {
                    int n = fs.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
            }

            if ((flags & 2) != 0)
            {
                if (raw.Length < 48 || Encoding.ASCII.GetString(raw, 0, 4) != "xbc1")
                    throw new InvalidDataException($"Expected xbc1 header for {virtualPath}");
                int xExp = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
                int xComp = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
                using var srcMs = new MemoryStream(raw, 48, Math.Min(xComp, raw.Length - 48), writable: false);
                using var zlib = new ZLibStream(srcMs, CompressionMode.Decompress);
                using var dstMs = new MemoryStream(xExp > 0 ? xExp : (int)expSize);
                zlib.CopyTo(dstMs);
                return dstMs.ToArray();
            }

            return raw;
        }
    }

    // ==================== Pure Managed Image Processing (Premultiplied Alpha) ====================

    sealed record RgbaImage(int Width, int Height, byte[] Rgba);

    static RgbaImage SolidImage(int w, int h, byte r, byte g, byte b, byte a)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = a;
        }
        return new RgbaImage(w, h, buf);
    }

    static void DarkenToNavy(RgbaImage img, double strength)
    {
        byte[] px = img.Rgba;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i + 0] = ClampByte(px[i + 0] * (1.0 - strength) + 7.0 * strength);
            px[i + 1] = ClampByte(px[i + 1] * (1.0 - strength) + 24.0 * strength);
            px[i + 2] = ClampByte(px[i + 2] * (1.0 - strength) + 66.0 * strength);
            px[i + 3] = 255;
        }
    }

    static int FindSwitch2RibbonGapY(RgbaImage img)
    {
        int startY = (int)(img.Height * 0.62);
        int endY = (int)(img.Height * 0.92);
        bool sawOpaqueBefore = false;
        int firstGapY = -1;

        for (int y = startY; y < endY; y++)
        {
            byte maxA = 0;
            int row = y * img.Width * 4;
            for (int x = 0; x < img.Width; x++)
            {
                byte a = img.Rgba[row + x * 4 + 3];
                if (a > maxA) maxA = a;
            }
            if (maxA >= 20)
            {
                if (firstGapY > 0)
                    return firstGapY; // Confirmed there is a ribbon below the transparent gap!
                sawOpaqueBefore = true;
            }
            else if (sawOpaqueBefore && firstGapY < 0)
            {
                firstGapY = y;
            }
        }
        return -1;
    }

    static RgbaImage Crop(RgbaImage src, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, src.Width - 1);
        y = Math.Clamp(y, 0, src.Height - 1);
        w = Math.Clamp(w, 1, src.Width - x);
        h = Math.Clamp(h, 1, src.Height - y);

        var dst = new byte[w * h * 4];
        int rowBytes = w * 4;
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src.Rgba, ((y + row) * src.Width + x) * 4, dst, row * rowBytes, rowBytes);
        return new RgbaImage(w, h, dst);
    }

    static RgbaImage? TrimAlphaOrNull(RgbaImage? src) => src == null ? null : TrimAlpha(src, minAlpha: 8, pad: 2);

    static RgbaImage TrimAlpha(RgbaImage src, byte minAlpha = 8, int pad = 2)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
        {
            int row = y * src.Width * 4;
            for (int x = 0; x < src.Width; x++)
            {
                if (src.Rgba[row + x * 4 + 3] >= minAlpha)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < minX || maxY < minY) return src;
        int x0 = Math.Max(0, minX - pad);
        int y0 = Math.Max(0, minY - pad);
        int x1 = Math.Min(src.Width - 1, maxX + pad);
        int y1 = Math.Min(src.Height - 1, maxY + pad);
        return Crop(src, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    static RgbaImage? ResizeMaxWidth(RgbaImage? src, int maxW)
    {
        if (src == null) return null;
        if (src.Width <= maxW) return src;
        int dstH = Math.Max(1, (int)Math.Round(src.Height * (maxW / (double)src.Width)));
        return Resize(src, maxW, dstH);
    }

    static RgbaImage? ResizeMaxDim(RgbaImage? src, int maxDim)
    {
        if (src == null) return null;
        int curMax = Math.Max(src.Width, src.Height);
        if (curMax <= maxDim) return src;
        double scale = maxDim / (double)curMax;
        return Resize(src, Math.Max(1, (int)Math.Round(src.Width * scale)), Math.Max(1, (int)Math.Round(src.Height * scale)));
    }

    static RgbaImage? ResizeExact(RgbaImage? src, int w, int h) => src == null ? null : Resize(src, w, h);

    static RgbaImage FitSquare(RgbaImage src, int size, double marginRatio)
    {
        int inner = Math.Max(1, (int)Math.Round(size * (1.0 - marginRatio * 2.0)));
        double scale = inner / (double)Math.Max(src.Width, src.Height);
        int nw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int nh = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = Resize(src, nw, nh);

        var canvas = new RgbaImage(size, size, new byte[size * size * 4]);
        int ox = (size - nw) / 2;
        int oy = (size - nh) / 2;
        AlphaOver(canvas, scaled, ox, oy);
        return canvas;
    }

    /// <summary>
    /// High-quality premultiplied-alpha resampler (box area filter for downscaling, bilinear for upscaling).
    /// Prevents dark edge halos on transparent logos and icons.
    /// </summary>
    static RgbaImage Resize(RgbaImage src, int dstW, int dstH)
    {
        if (src.Width == dstW && src.Height == dstH) return src;
        var dst = new byte[dstW * dstH * 4];
        byte[] s = src.Rgba;
        int sw = src.Width, sh = src.Height;
        double sx = sw / (double)dstW;
        double sy = sh / (double)dstH;
        bool areaFilter = sx > 1.25 || sy > 1.25;

        for (int y = 0; y < dstH; y++)
        {
            for (int x = 0; x < dstW; x++)
            {
                double pr = 0, pg = 0, pb = 0, pa = 0;
                if (areaFilter)
                {
                    double x0 = x * sx, x1 = (x + 1) * sx;
                    double y0 = y * sy, y1 = (y + 1) * sy;
                    int ix0 = Math.Max(0, (int)Math.Floor(x0));
                    int ix1 = Math.Min(sw - 1, (int)Math.Ceiling(x1) - 1);
                    int iy0 = Math.Max(0, (int)Math.Floor(y0));
                    int iy1 = Math.Min(sh - 1, (int)Math.Ceiling(y1) - 1);
                    double wSum = 0;

                    for (int iy = iy0; iy <= iy1; iy++)
                    {
                        double wy = Math.Min(y1, iy + 1.0) - Math.Max(y0, iy);
                        if (wy <= 0) continue;
                        int row = iy * sw * 4;
                        for (int ix = ix0; ix <= ix1; ix++)
                        {
                            double wx = Math.Min(x1, ix + 1.0) - Math.Max(x0, ix);
                            if (wx <= 0) continue;
                            double w = wx * wy;
                            int o = row + ix * 4;
                            double a = s[o + 3] / 255.0;
                            pr += s[o + 0] * a * w;
                            pg += s[o + 1] * a * w;
                            pb += s[o + 2] * a * w;
                            pa += a * w;
                            wSum += w;
                        }
                    }
                    if (wSum > 0) { pr /= wSum; pg /= wSum; pb /= wSum; pa /= wSum; }
                }
                else
                {
                    double fx = (x + 0.5) * sx - 0.5;
                    double fy = (y + 0.5) * sy - 0.5;
                    int x0 = Math.Clamp((int)Math.Floor(fx), 0, sw - 1);
                    int y0 = Math.Clamp((int)Math.Floor(fy), 0, sh - 1);
                    int x1 = Math.Min(sw - 1, x0 + 1);
                    int y1 = Math.Min(sh - 1, y0 + 1);
                    double tx = Math.Clamp(fx - x0, 0, 1);
                    double ty = Math.Clamp(fy - y0, 0, 1);

                    double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty);
                    double w01 = (1 - tx) * ty, w11 = tx * ty;
                    int o00 = (y0 * sw + x0) * 4, o10 = (y0 * sw + x1) * 4;
                    int o01 = (y1 * sw + x0) * 4, o11 = (y1 * sw + x1) * 4;

                    double a00 = s[o00 + 3] / 255.0, a10 = s[o10 + 3] / 255.0;
                    double a01 = s[o01 + 3] / 255.0, a11 = s[o11 + 3] / 255.0;

                    pr = s[o00] * a00 * w00 + s[o10] * a10 * w10 + s[o01] * a01 * w01 + s[o11] * a11 * w11;
                    pg = s[o00 + 1] * a00 * w00 + s[o10 + 1] * a10 * w10 + s[o01 + 1] * a01 * w01 + s[o11 + 1] * a11 * w11;
                    pb = s[o00 + 2] * a00 * w00 + s[o10 + 2] * a10 * w10 + s[o01 + 2] * a01 * w01 + s[o11 + 2] * a11 * w11;
                    pa = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;
                }

                int di = (y * dstW + x) * 4;
                if (pa > 1e-5)
                {
                    dst[di + 0] = ClampByte(pr / pa);
                    dst[di + 1] = ClampByte(pg / pa);
                    dst[di + 2] = ClampByte(pb / pa);
                    dst[di + 3] = ClampByte(pa * 255.0);
                }
            }
        }
        return new RgbaImage(dstW, dstH, dst);
    }

    static void AlphaOver(RgbaImage dst, RgbaImage src, int ox, int oy)
    {
        byte[] d = dst.Rgba, s = src.Rgba;
        for (int y = 0; y < src.Height; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= dst.Height) continue;
            for (int x = 0; x < src.Width; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= dst.Width) continue;
                int si = (y * src.Width + x) * 4;
                byte saByte = s[si + 3];
                if (saByte == 0) continue;

                int di = (dy * dst.Width + dx) * 4;
                if (saByte == 255)
                {
                    d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = 255;
                    continue;
                }

                double sa = saByte / 255.0;
                double da = d[di + 3] / 255.0;
                double oa = sa + da * (1.0 - sa);
                if (oa > 1e-5)
                {
                    d[di + 0] = ClampByte((s[si + 0] * sa + d[di + 0] * da * (1.0 - sa)) / oa);
                    d[di + 1] = ClampByte((s[si + 1] * sa + d[di + 1] * da * (1.0 - sa)) / oa);
                    d[di + 2] = ClampByte((s[si + 2] * sa + d[di + 2] * da * (1.0 - sa)) / oa);
                    d[di + 3] = ClampByte(oa * 255.0);
                }
            }
        }
    }

    static byte ClampByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);

    // ==================== Pure Managed PNG & ICO Writers ====================

    static byte[] EncodePng(RgbaImage img)
    {
        using var ms = new MemoryStream(img.Width * img.Height);
        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(0, 4), img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), img.Height);
        ihdr[8] = 8; // 8-bit depth
        ihdr[9] = 6; // RGBA
        WritePngChunk(ms, "IHDR"u8, ihdr);

        // IDAT (PNG Sub filter = 1 for fast, compact compression)
        int stride = img.Width * 4;
        var rowBuf = new byte[stride + 1];
        rowBuf[0] = 1; // Filter type 1: Sub
        using var idatMs = new MemoryStream();
        using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < img.Height; y++)
            {
                int srcOff = y * stride;
                for (int i = 0; i < 4; i++)
                    rowBuf[1 + i] = img.Rgba[srcOff + i];
                for (int i = 4; i < stride; i++)
                    rowBuf[1 + i] = unchecked((byte)(img.Rgba[srcOff + i] - img.Rgba[srcOff + i - 4]));
                zlib.Write(rowBuf, 0, rowBuf.Length);
            }
        }
        WritePngChunk(ms, "IDAT"u8, idatMs.GetBuffer().AsSpan(0, (int)idatMs.Length));

        // IEND
        WritePngChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    static readonly uint[] PngCrcTable = BuildPngCrcTable();
    static uint[] BuildPngCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static void WritePngChunk(MemoryStream ms, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        ms.Write(lenBuf);
        ms.Write(type);
        if (!data.IsEmpty) ms.Write(data);

        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++)
            crc = PngCrcTable[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < data.Length; i++)
            crc = PngCrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        ms.Write(crcBuf);
    }

    static byte[] EncodeIco(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        int headerSize = 6 + frames.Count * 16;
        int totalSize = headerSize + frames.Sum(f => f.Png.Length);
        var buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), 1); // Icon type
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), (ushort)frames.Count);

        int dataOff = headerSize;
        for (int i = 0; i < frames.Count; i++)
        {
            var (sz, png) = frames[i];
            int eOff = 6 + i * 16;
            buf[eOff + 0] = sz >= 256 ? (byte)0 : (byte)sz;
            buf[eOff + 1] = sz >= 256 ? (byte)0 : (byte)sz;
            buf[eOff + 2] = 0;
            buf[eOff + 3] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eOff + 4, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eOff + 6, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(eOff + 8, 4), png.Length);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(eOff + 12, 4), dataOff);
            Buffer.BlockCopy(png, 0, buf, dataOff, png.Length);
            dataOff += png.Length;
        }
        return buf;
    }

    static string SanitizeKey(string key) =>
        new string(key.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();

    static void WriteAtomic(string path, byte[] data)
    {
        string tmp = $"{path}.tmp.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
