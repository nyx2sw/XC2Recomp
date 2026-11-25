using System.Reflection;
using XC2.Core;

namespace XC2.Launcher;

/// <summary>
/// Serves all virtual origin requests under https://app.xc2/:
///   /ui/...           embedded HTML/CSS/JS UI
///   /media/art/<key>  approved local visual_identity_kit artwork (read from disk, never baked into exe)
///   /media/icon/<key> native extracted XC2 icons from visual_identity_kit
///   /media/bgm/<id>.ogg  transmuxed OggOpus from player's romfs/stream/bgm/*.nop
///   /media/sfx/<id>.wav  decoded PTADPCM WAV from player's romfs/sound/system.pck
/// </summary>
public sealed class Content
{
    readonly Assembly asm = typeof(Content).Assembly;
    readonly Dictionary<string, string> embedded;

    public Func<string, (byte[] Data, string Mime)?>? BgmProvider;
    public Func<string, (byte[] Data, string Mime)?>? SfxProvider;
    public Func<string, (byte[] Data, string Mime)?>? ArtProvider;
    public Func<string, (byte[] Data, string Mime)?>? IconProvider;
    public Func<string, (byte[] Data, string Mime)?>? PackThumbProvider;
    public string? VisualKitDir { get; set; }

    static readonly Dictionary<string, string> ArtMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logo"]          = @"02_titulo\xc2_pc_edition_1280.png",
        ["logo_sm"]       = @"02_titulo\xc2_pc_edition_640.png",
        ["logo_official"] = @"..\sources\game_local\official_logo_xc2_clean.png",
        ["logo_torna"]    = @"..\sources\game_local\official_logo_torna_clean.png",
        ["keyart_bg"]     = @"..\sources\game_local\official_keyart_alrest_2320x1376.png",
        ["title_bg"]      = @"04_fundos\arte_original_titulo_1018x720.png",
        ["torna_bg"]      = @"..\sources\game_local\official_torna_hd_2320x1376.png",
        ["dlc_crest"]   = @"..\sources\game_local\mnu105_bg_image_00_0.png",
        ["dlc_items"]   = @"..\sources\game_local\mnu105_bg_image_01_0.png",
        ["dlc_quests"]  = @"..\sources\game_local\mnu105_bg_image_02_0.png",
        ["dlc_blades"]  = @"..\sources\game_local\mnu105_bg_image_03_0.png",
        ["dlc_battle"]  = @"..\sources\game_local\mnu105_bg_image_04_0.png",
        ["argentum_bg"] = @"..\sources\game_local\mnu010_bg_0.png",
        ["save_rex"]    = @"..\sources\game_local\mnu094_rootmenu_save_0.png",
        ["menu_bg"]     = @"04_fundos\menu_azul_1920x1080.png",
        ["menu_tex"]    = @"04_fundos\textura_azul_original.png",
        ["hero"]        = @"03_biblioteca\hero_sem_texto_1920x620.png",
        ["banner"]      = @"03_biblioteca\banner_920x430.png",
        ["cover"]       = @"03_biblioteca\capa_600x900.png",
        ["crystal"]     = @"01_atalhos\png\xc2_cristal_256.png",
        ["classic"]     = @"01_atalhos\png\xc2_classico_256.png",
        ["root_ribbon"] = @"..\sources\game_local\mnu094_rootmenu_bg01_0.png",
        ["hex_bar"]     = @"..\sources\game_local\mnu094_rootmenu_base_0.png",
        ["art_blades"]  = @"..\sources\game_local\mnu094_rootmenu_blades_0.png",
        ["art_options"] = @"..\sources\game_local\mnu094_rootmenu_option_0.png",
        ["opt_tab"]     = @"..\sources\game_local\mnu080_option_tab_0.png",
        ["opt_slide"]   = @"..\sources\game_local\mnu080_option_tab_slide_0.png",
        ["win_sys"]     = @"..\sources\game_local\cmn_window_system_0.png",
        ["win_blade"]   = @"..\sources\game_local\cmn_window_blade_0.png",
    };

    public Content()
    {
        embedded = asm.GetManifestResourceNames()
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);
    }

    public (byte[] Data, string Mime)? Get(string rawPath)
    {
        var path = Uri.UnescapeDataString(rawPath.Split('?', '#')[0]).TrimStart('/');
        try
        {
            if (path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase))
            {
                if (!embedded.TryGetValue(path, out var res)) return null;
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return (ms.ToArray(), MimeOf(path));
            }

            if (path.StartsWith("media/art/", StringComparison.OrdinalIgnoreCase))
            {
                var key = Path.GetFileNameWithoutExtension(path["media/art/".Length..]);
                if (VisualKitDir != null && ArtMap.TryGetValue(key, out var rel))
                {
                    var fp = Path.GetFullPath(Path.Combine(VisualKitDir, rel));
                    if (File.Exists(fp)) return (File.ReadAllBytes(fp), "image/png");
                }
                return ArtProvider?.Invoke(key);
            }

            if (path.StartsWith("media/icon/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(path["media/icon/".Length..]);
                var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                if (VisualKitDir != null)
                {
                    var fp = Path.Combine(VisualKitDir, "05_icones_nativos", "png_128", safe + ".png");
                    if (File.Exists(fp)) return (File.ReadAllBytes(fp), "image/png");
                }
                return IconProvider?.Invoke(safe);
            }

            if (path.StartsWith("media/pack-thumb/", StringComparison.OrdinalIgnoreCase) && PackThumbProvider != null)
            {
                var id = Path.GetFileNameWithoutExtension(path["media/pack-thumb/".Length..]);
                return PackThumbProvider(id);
            }

            if (path.StartsWith("media/bgm/", StringComparison.OrdinalIgnoreCase) && BgmProvider != null)
            {
                var id = Path.GetFileNameWithoutExtension(path["media/bgm/".Length..]);
                return BgmProvider(id);
            }

            if (path.StartsWith("media/sfx/", StringComparison.OrdinalIgnoreCase) && SfxProvider != null)
            {
                var id = Path.GetFileNameWithoutExtension(path["media/sfx/".Length..]);
                return SfxProvider(id);
            }
        }
        catch (Exception ex) { LauncherLog.Write($"content {path}: {ex.Message}"); }
        return null;
    }

    static string MimeOf(string p) => Path.GetExtension(p).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
