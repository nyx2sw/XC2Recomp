using System.Text.Json;
using System.Text.Json.Serialization;

namespace XC2.Core;

public sealed class LauncherConfig
{
    public string? InstallPath { get; set; }
    public string Preset { get; set; } = "120_1440_dlaa";
    public string AccentTheme { get; set; } = "aegis"; // aegis | pyra | mythra | torna
    public string SoundLevel { get; set; } = "low";    // off | low | med | high
    public bool MusicEnabled { get; set; } = true;
    public string MusicTrack { get; set; } = "m01";    // m01 | m90 | m02 | m103
    public int CustomXc2Fps { get; set; } = 120;       // 60 | 90 | 120 | 144 | 165 | 240
    public bool Custom4kSelectiveScale { get; set; } = false;
    public bool MinimizeOnLaunch { get; set; } = false;
    public bool CloseAfterLaunch { get; set; } = false;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool OnboardingCompleted { get; set; } = false;
    public int OnboardingVersion { get; set; } = 0;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static LauncherConfig Load(string? path = null)
    {
        var file = path ?? LauncherPaths.ConfigFile;
        try
        {
            if (File.Exists(file))
            {
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(file), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex) { LauncherLog.Write("config load: " + ex.Message); }
        return new LauncherConfig();
    }

    public void Save(string? path = null)
    {
        var file = path ?? LauncherPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + $".tmp.{Environment.ProcessId}";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, file, overwrite: true);
    }
}

public sealed record LaunchPreset(
    string Id,
    string Name,
    string ShortTag,
    string Description,
    string ResolutionScale,
    string Upscale,
    int TargetFps,
    bool Selective4kScale,
    string Badge
);

public static class LaunchPresets
{
    public static readonly LaunchPreset[] All =
    [
        new("120_4k_dlss_qual", "4K DLSS 4.5 Qualidade · 120 FPS", "1440p → 4K · 120 FPS",
            "Renderiza em 1440p e reconstrói direto em 4K com DLSS 4.5 (ou FSR 3.1), sem blit bilinear: máxima nitidez a +100 FPS.",
            "2.5", "dlss_quality", 120, false, "Máxima Nitidez"),
        new("120_1440_dlaa", "1440p DLAA · 120 FPS", "1440p · 120 FPS",
            "Anti-aliasing DLAA em 1440p com foco em taxa máxima de quadros.",
            "2.5", "dlss_native", 120, false, "Recomendado"),
        new("120_1440_dlss_qual", "1440p DLSS 4.5 Qualidade · 120 FPS", "960p → 1440p · 120 FPS",
            "Renderiza em 960p e reconstrói direto em 1440p com DLSS 4.5 (ou FSR 3.1): folga ampla de GPU e +100 FPS em mundo aberto.",
            "1.6667", "dlss_quality", 120, false, "1440p Fluido"),
        new("60_1440_dlss_qual", "1440p DLSS 4.5 Qualidade · 60 FPS", "960p → 1440p · 60 FPS",
            "Renderiza em 960p e reconstrói em 1440p a 60 FPS com alta margem de GPU e temperatura reduzida.",
            "1.6667", "dlss_quality", 60, false, "Leve 1440p"),
        new("60_1440_dlaa", "1440p DLAA · 60 FPS", "1440p · 60 FPS",
            "1440p nativo com DLAA a 60 quadros constantes.",
            "2.5", "dlss_native", 60, false, "Estável"),
        new("60_4k_dlaa", "4K DLAA Nativo · 60 FPS", "4K · 60 FPS",
            "Resolução 4K 1:1 nativa com DLAA e máximo detalhe visual.",
            "3.75", "dlss_native", 60, false, "Ultra 4K"),
        new("120_1080_dlaa", "1080p DLAA · 120 FPS", "1080p · 120 FPS",
            "1080p com DLAA focado em alta fluidez.",
            "1.875", "dlss_native", 120, false, "Desempenho"),
        new("60_1080_dlaa", "1080p DLAA · 60 FPS", "1080p · 60 FPS",
            "1080p nativo com limite clássico de 60 FPS.",
            "1.875", "dlss_native", 60, false, "Leve"),
        new("original", "Original", "Original · Switch 2",
            "Configurações originais do jogo no Nintendo Switch 2 (escala 1×, DLSS/DRS nativo do jogo, 60 FPS e filtros padrão do console).",
            "1", "auto", 60, false, "Switch 2"),
        new("custom", "Personalizado", "Personalizado",
            "Usa exatamente os ajustes definidos em Configurações.",
            "", "", 0, false, "Manual")
    ];

    public static LaunchPreset Find(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
