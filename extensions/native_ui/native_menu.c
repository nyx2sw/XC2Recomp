/* SPDX-License-Identifier: GPL-3.0-or-later
   Copyright 2026 the ScarletNX contributors */
/* Native in-game PC Settings category inside System > Options for Xenoblade
   Chronicles 2: Nintendo Switch 2 Edition 3.0.1 (module main at 0x08021000).
   The game's own UI draws everything; only MenuOption's indirect callbacks are
   extended, verified by FNV-1a fingerprints on every hooked function. */
#include "runtime/native_ui.h"
#include <stdio.h>
#include <string.h>
#include <windows.h>

static const SnxNativeUiHost *host;
static SRWLOCK model_lock = SRWLOCK_INIT;
typedef struct {
  const char *key, *pt, *en, *help_pt, *help_en, *def;
  int page, restart, count;
  const char *values[12];
  const char *pt_values[12];
  const char *en_values[12];
} Setting;
static const Setting settings[] = {
    {"resource_pack",
     "Idioma dos textos",
     "Text language",
     "Tradução PT-BR (ocidental ou nomes JP) e Inglês.\nReinicie para mudar o idioma.",
     "Complete PT-BR (Western or JP names) and English.\nRestart to change the language.",
     "pt-br",
     0,
     1,
     3,
     {"pt-br", "pt-br-jp", ""},
     {"Português (BR)", "PT-BR (Nomes JP)", "English"},
     {"Português (BR)", "PT-BR (JP Names)", "English"}},
    {"discord_presence",
     "Presença no Discord",
     "Discord Presence",
     "Mostra local ao vivo e grupo no Discord.\nAplicado na hora com Aplicar.",
     "Shows live location and party on Discord.\nApplied immediately on Apply.",
     "1",
     0,
     0,
     2,
     {"1", "0"},
     {"Ativada", "Desativada"},
     {"Enabled", "Disabled"}},
    {"window_mode",
     "Modo de exibição",
     "Display mode",
     "Escolha o modo de janela.\nAplicado na hora com Aplicar.",
     "Choose a window mode.\nApplied immediately on Apply.",
     "auto",
     1,
     0,
     4,
     {"auto", "windowed", "borderless", "fullscreen"},
     {"Automático", "Janela", "Sem bordas", "Tela cheia"},
     {"Automatic", "Windowed", "Borderless", "Fullscreen"}},
    {"output_resolution",
     "Resolução de saída",
     "Output resolution",
     "Sem bordas usa a área de trabalho.\nAplicado na hora com Aplicar.",
     "Borderless uses the desktop.\nApplied immediately on Apply.",
     "auto",
     1,
     0,
     6,
     {"auto", "desktop", "1280x720", "1920x1080", "2560x1440", "3840x2160"},
     {"Automática", "Desktop", "1280×720", "1920×1080", "2560×1440",
      "3840×2160"},
     {"Automatic", "Desktop", "1280×720", "1920×1080", "2560×1440",
      "3840×2160"}},
    {"resolution",
     "Escala de resolução",
     "Resolution scale",
     "Escala 3.0.1 (100% = Switch 2, 167% = 960p, 188% = 1080p, 250% = 1440p).\nAplicado na hora com Aplicar.",
     "3.0.1 scale (100% = Switch 2, 167% = 960p, 188% = 1080p, 250% = 1440p).\nApplied immediately on Apply.",
     "1",
     2,
     0,
     7,
     {"0.75", "1", "1.3333", "1.6667", "1.875", "2", "2.5"},
     {"75% (432p)", "100% (Switch 2)", "133% (768p)", "167% (960p)",
      "188% (1080p)", "200% (1152p)", "250% (1440p)"},
     {"75% (432p)", "100% (Switch 2)", "133% (768p)", "167% (960p)",
      "188% (1080p)", "200% (1152p)", "250% (1440p)"}},
    {"anisotropy",
     "Filtro anisotrópico",
     "Anisotropic filtering",
     "Melhora texturas vistas em ângulo.\nAplicado na hora com Aplicar.",
     "Improves textures viewed at an angle.\nApplied immediately on Apply.",
     "0",
     2,
     0,
     5,
     {"0", "2", "4", "8", "16"},
     {"Original", "2×", "4×", "8×", "16×"},
     {"Original", "2×", "4×", "8×", "16×"}},
    {"shadows",
     "Sombras",
     "Shadows",
     "Ativa ou desativa as sombras.\nAplicado na hora com Aplicar.",
     "Enables or disables shadows.\nApplied immediately on Apply.",
     "1",
     2,
     0,
     2,
     {"1", "0"},
     {"Ativadas", "Desativadas"},
     {"Enabled", "Disabled"}},
    {"upscale",
     "Modo",
     "Mode",
     "Escolha FSR 3.1, DLSS 4.5, Intel XeSS, Auto ou Desligado.\nAplicado na hora com Aplicar.",
     "Choose FSR 3.1, DLSS 4.5, Intel XeSS, Auto or Off.\nApplied immediately on Apply.",
     "dlss_native",
     3,
     0,
     5,
     {"off", "fsr3", "dlss", "xess", "auto"},
     {"Desligado", "FSR 3.1", "DLSS 4.5", "Intel XeSS", "Automático"},
     {"Off", "FSR 3.1", "DLSS 4.5", "Intel XeSS", "Automatic"}},
    {NULL, /* This row edits the same persisted upscale key as Mode. */
     "Preset",
     "Preset",
     "Nativo: antisserrilhado máximo.\nAplicado na hora com Aplicar.",
     "Native: maximum anti-aliasing.\nApplied immediately on Apply.",
     "",
     3,
     0,
     5,
     {"native", "quality", "balanced", "performance", "1080p"},
     {"Nativo (AA)", "Qualidade", "Equilibrado", "Desempenho", "Cena 1080p"},
     {"Native (AA)", "Quality", "Balanced", "Performance", "1080p scene"}},
    {"dlss_preset",
     "Modelo DLSS",
     "DLSS Model",
     "Transformer DLSS 4.5 (K, M FP8, L ou J).\nAplicado na hora com Aplicar.",
     "DLSS 4.5 Transformer (K, M FP8, L or J).\nApplied immediately on Apply.",
     "auto",
     3,
     0,
     5,
     {"auto", "k", "m", "l", "j"},
     {"Auto (K/M)", "Preset K (Estável)", "Preset M (v2 FP8)",
      "Preset L (Ultra)", "Preset J (Rápido)"},
     {"Auto (K/M)", "Preset K (Stable)", "Preset M (v2 FP8)",
      "Preset L (Ultra)", "Preset J (Low ghost)"}},
    {"amiibo",
     "Amiibo virtual",
     "Virtual amiibo",
     "Use no menu Sistema > amiibo (Pyra/Mythra: Novos Estilos).\nAplicado na hora com Aplicar.",
     "Use in System > amiibo (Pyra/Mythra: New Styles).\nApplied immediately on Apply.",
     "0",
     0,
     0,
     6,
     {"0", "1", "2", "3", "4", "5"},
     {"Desativado", "Pyra", "Mythra", "Shulk", "Noah", "Mio"},
     {"Disabled", "Pyra", "Mythra", "Shulk", "Noah", "Mio"}},
    {"upscale_sharpness",
     "Nitidez FSR",
     "FSR Sharpness",
     "Nitidez RCAS aplicada pelo FSR 3.1.\nAplicado na hora com Aplicar.",
     "RCAS sharpening for FSR 3.1.\nApplied immediately on Apply.",
     "0.1",
     4,
     0,
     4,
     {"0", "0.1", "0.25", "0.5"},
     {"Desligada", "Suave (0.1)", "Média (0.25)", "Alta (0.5)"},
     {"Off", "Subtle (0.1)", "Medium (0.25)", "Strong (0.5)"}},
    {"upscale_mip_bias",
     "Detalhe de textura",
     "Texture detail",
     "Viés de detalhe (mip bias) das texturas.\nAplicado na hora com Aplicar.",
     "Texture mip LOD bias.\nApplied immediately on Apply.",
     "auto",
     4,
     0,
     4,
     {"auto", "0", "-0.5", "-1"},
     {"Automático", "Original", "-0.5", "-1.0"},
     {"Automatic", "Original", "-0.5", "-1.0"}},
    {"tonemap",
     "Mapeamento HDR",
     "HDR Tonemapping",
     "Curva fílmica preservando as LUTs 3D originais.\nAplicado na hora com Aplicar.",
     "Filmic curve preserving XC2's dual 3D color LUTs.\nApplied immediately on Apply.",
     "native",
     5,
     0,
     5,
     {"native", "gt7", "agx", "uchimura", "pbr_neutral"},
     {"Original", "GT7 Fílmico", "AgX", "Uchimura", "PBR Neutro"},
     {"Original", "GT7 Filmic", "AgX", "Uchimura", "PBR Neutral"}},
    {"bloom_mode",
     "Bloom",
     "Bloom",
     "Físico evita estourar nuvens e luzes fortes.\nAplicado na hora com Aplicar.",
     "Physical avoids blowing out clouds and highlights.\nApplied immediately on Apply.",
     "native",
     5,
     0,
     2,
     {"native", "physical"},
     {"Original", "Físico"},
     {"Original", "Physical"}},
    {"contact_shadows",
     "Sombras de contato",
     "Contact shadows",
     "Sombras finas em grama, pedras e cenário.\nAplicado na hora com Aplicar.",
     "Fine screen-space sun shadows on terrain & props.\nApplied immediately on Apply.",
     "0",
     5,
     0,
     2,
     {"0", "1"},
     {"Desativadas", "Ativadas"},
     {"Disabled", "Enabled"}},
    {"gtao",
     "Oclusão ambiental",
     "Ambient occlusion",
     "GTAO com filtro bilateral para profundidade.\nAplicado na hora com Aplicar.",
     "GTAO with bilateral denoise for crevice depth.\nApplied immediately on Apply.",
     "0",
     6,
     0,
     2,
     {"0", "1"},
     {"Original", "GTAO"},
     {"Original", "GTAO"}},
    {"env_shading",
     "Materiais do cenário",
     "Scene materials",
     "Oren-Nayar, Micro-Sombras e Oclusão Especular.\nAplicado na hora com Aplicar.",
     "Oren-Nayar, Micro-Shadows & Specular Occlusion.\nApplied immediately on Apply.",
     "0",
     6,
     0,
     2,
     {"0", "1"},
     {"Original", "Aprimorado"},
     {"Original", "Enhanced"}},
    {"water_caustics",
     "Cáusticas na água",
     "Water caustics",
     "Reflexos 3D e desfoque óptico no fundo da água.\nAplicado na hora com Aplicar.",
     "Animated 3D caustics & optical depth blur.\nApplied immediately on Apply.",
     "0",
     6,
     0,
     2,
     {"0", "1"},
     {"Desativadas", "Ativadas"},
     {"Disabled", "Enabled"}},
    {"ssgi",
     "Iluminação global",
     "Global illumination",
     "Luz indireta por rebate (SSGI) no cenário.\nAplicado na hora com Aplicar.",
     "Screen-space bounce lighting (SSGI) on scenery.\nApplied immediately on Apply.",
     "0",
     7,
     0,
     2,
     {"0", "1"},
     {"Desativada", "SSGI"},
     {"Disabled", "SSGI"}},
    {"ssr",
     "Reflexos em tela",
     "Screen reflections",
     "Reflexos em superfícies metálicas e úmidas (SSR).\nAplicado na hora com Aplicar.",
     "Roughness-aware reflections on metal & wet floors.\nApplied immediately on Apply.",
     "0",
     7,
     0,
     2,
     {"0", "1"},
     {"Desativados", "SSR"},
     {"Disabled", "SSR"}},
    {"fps_limit",
     "Limite de quadros",
     "Frame limit",
     "Limite dinâmico (cutscenes ficam 30 FPS).\nAplicado na hora com Aplicar.",
     "Dynamic frame limit (cutscenes stay 30 FPS).\nApplied immediately on Apply.",
     "game",
     8,
     0,
     6,
     {"30", "60", "90", "120", "0", "game"},
     {"30 FPS", "60 FPS", "90 FPS", "120 FPS", "Sem limite", "Do jogo"},
     {"30 FPS", "60 FPS", "90 FPS", "120 FPS", "Unlimited", "Game"}},
    {"vsync",
     "Sincronização vertical",
     "Vertical sync",
     "Adaptável evita quedas; Rígida elimina rasgo; Desativada usa VRR.\nAplicado na hora com Aplicar.",
     "Adaptive avoids drops; Strict removes tearing; Off uses VRR.\nApplied immediately on Apply.",
     "1",
     8,
     0,
     3,
     {"1", "2", "0"},
     {"Adaptável", "Rígida (FIFO)", "Desativada"},
     {"Adaptive", "Strict (FIFO)", "Disabled"}},
    {"show_fps",
     "Contador de FPS",
     "FPS counter",
     "Exibe a taxa real de quadros na HUD.\nAplicado na hora com Aplicar.",
     "Shows real-time frame rate on the HUD.\nApplied immediately on Apply.",
     "0",
     8,
     0,
     2,
     {"0", "1"},
     {"Desativado", "Ativado"},
     {"Disabled", "Enabled"}},
    {"quality_preset",
     "Predefinição gráfica",
     "Graphics preset",
     "Ajusta escala e efeitos para o nível da GPU (Auto detecta VRAM).\nReinicie o jogo para aplicar.",
     "Scales resolution & effects to your GPU tier (Auto detects VRAM).\nRestart to apply.",
     "custom",
     1,
     1,
     6,
     {"custom", "auto", "low", "medium", "high", "ultra"},
     {"Personalizada", "Automática", "Baixo", "Médio", "Alto", "Ultra"},
     {"Custom", "Auto", "Low", "Medium", "High", "Ultra"}},
    {"cloud_quality",
     "Mar de Nuvens",
     "Cloud Sea",
     "Alta dobra passos volumétricos (sem banding); Ultra amplia o volume.\nReinicie o jogo para aplicar.",
     "High doubles raymarch steps (no banding); Ultra raises volume res.\nRestart to apply.",
     "0",
     4,
     1,
     3,
     {"0", "1", "2"},
     {"Original", "Alta (2×)", "Ultra (1.5×)"},
     {"Original", "High (2×)", "Ultra (1.5×)"}},
    {"shadow_soften",
     "Suavizar sombras",
     "Shadow smoothing",
     "Filtro bilateral guiado por profundidade nas sombras do cenário.\nAplicado na hora com Aplicar.",
     "Depth-guided bilateral shadow edge smoothing (character-safe).\nApplied immediately on Apply.",
     "0",
     7,
     0,
     2,
     {"0", "1"},
     {"Original", "Suave"},
     {"Original", "Smooth"}},
    {"low_latency",
     "Baixa latência",
     "Low latency",
     "NVIDIA Reflex (VK_NV_low_latency2) + pacer sub-ms em todas GPUs.\nAplicado na hora com Aplicar.",
     "NVIDIA Reflex (VK_NV_low_latency2) + sub-ms pacer on all GPUs.\nApplied immediately on Apply.",
     "1",
     9,
     0,
     3,
     {"0", "1", "2"},
     {"Desativada", "Ativada (Reflex)", "Reflex + Boost"},
     {"Disabled", "On (Reflex)", "Reflex + Boost"}},
    {"hdr",
     "Saída HDR10",
     "HDR10 output",
     "Saída 10-bit ST.2084 PQ (volta para SDR se o monitor não suportar).\nAplicado na hora com Aplicar.",
     "Native 10-bit ST.2084 PQ output (clean SDR fallback if unsupported).\nApplied immediately on Apply.",
     "0",
     9,
     0,
     2,
     {"0", "1"},
     {"Desativada (SDR)", "HDR10 (PQ)"},
     {"Disabled (SDR)", "HDR10 (PQ)"}},
    {"hdr_peak_nits",
     "Pico HDR (nits)",
     "HDR Peak (nits)",
     "Luminância máxima do monitor HDR10 (400 a 2000 nits).\nAplicado na hora com Aplicar.",
     "Peak luminance of your HDR10 display (400 to 2000 nits).\nApplied immediately on Apply.",
     "1000",
     9,
     0,
     6,
     {"400", "600", "800", "1000", "1500", "2000"},
     {"400 nits", "600 nits", "800 nits", "1000 nits", "1500 nits", "2000 nits"},
     {"400 nits", "600 nits", "800 nits", "1000 nits", "1500 nits", "2000 nits"}}};
#define COUNT ((int)(sizeof settings / sizeof settings[0]))
#define PAGES 11
static char current[COUNT][32], staged[COUNT][32], saved[COUNT][32],
    pending[COUNT][32];
static int apply_request, apply_reply, refresh, restart_needed, page, focus,
    confirm_back, portuguese, jp_names;
static u64 applied_mask;
enum { UPSCALE_MODE = 7, UPSCALE_PRESET = 8, AMIIBO_IDX = 10 };
static int editing_preset;
static const char *const upscale_values[3][5] = {
    {"fsr3_native", "fsr3_quality", "fsr3_balanced", "fsr3_performance",
     "fsr3"},
    {"dlss_native", "dlss_quality", "dlss_balanced", "dlss_performance", NULL},
    {"xess_native", "xess_quality", "xess_balanced", "xess_performance", NULL}};
static const char *message;
static u64 owner, panel, submenu, rows, cursor;
static u16 style_index = 37, style_name = 151, style_param = 35, style_info = 25;
static GuestFn originals[6];

/* Verified Switch 2 Edition 3.0.1 addresses (module main loaded at 0x08021000) */
static const u64 addresses[] = {
    0x850b760, /* MenuOption::init */
    0x850dae0, /* MenuOption::category_event */
    0x850e000, /* MenuOption::panel_event */
    0x850c8a0, /* MenuOption::update */
    0x850b620, /* MenuOption::~MenuOption */
    0x850b720  /* MenuOption::~MenuOption (deleting) */
};
static const unsigned sizes[] = {4332, 1296, 4264, 144, 252, 48};
static const u64 fingerprints[] = {
    0x23ede52a64693548ull, 0x9b454d7fce21a3edull, 0x1790712b6af174c5ull,
    0x22a2e6bb782c7e09ull, 0xe0a6ecc0e36ffb0full, 0x55cf8b80fac4009bull};

/* Verified Switch 2 Edition 3.0.1 helper & GOT addresses */
enum : u64 {
  FN_FIND = 0x87af650,
  FN_REF_CTOR = 0x87aa1c0,
  FN_REF_VISIBLE = 0x87aa6c0,
  FN_REF_SET_ANIM = 0x87aa700,
  FN_REF_DTOR = 0x87aa2a0,
  FN_TEXT_CTOR = 0x87ae560,
  FN_REF_SET_TEXT = 0x87ab1e0,
  FN_TEXT_DTOR = 0x87ae5a0,
  FN_GET_HEAP = 0x83bd600,
  FN_HEAP_ALLOC = 0x83bd610,
  FN_LIST_CTOR = 0x87981a0,
  FN_LIST_ENUM = 0x87983c0,
  FN_LIST_SELECT = 0x8797d30,
  FN_REGISTER_LISTENER = 0x87901e0,
  FN_DEFAULT_EVENT = 0x878fce0,
  FN_CURSOR_PARAMS_INIT = 0x878ac50,
  FN_CURSOR_CREATE = 0x878ac90,
  FN_CURSOR_ENABLE = 0x878b190,
  FN_PLAY_ANIM = 0x8549e90,
  FN_ATTACH_ANIM = 0x8403060,
  FN_PLAY_OUT_ANIM = 0x854a860,
  FN_PANEL_OPEN = 0x8402ad0,
  FN_PANEL_CLOSE = 0x8402b20,
  FN_PANEL_SYNC = 0x84029b0,
  FN_PANEL_ACTIVATE = 0x8402960,
  FN_PANEL_CLEAR_INPUT = 0x84024f0,
  FN_PANEL_BIND_LIST = 0x8402540,
  FN_OPT_ENTER_PANEL = 0x850b170,
  FN_OPT_LEAVE_PANEL = 0x850b230,
  FN_OPT_FOOTER = 0x850b010,
  FN_SFX_CONFIRM = 0x854e870,
  FN_SFX_CANCEL = 0x854e970,
  FN_SFX_CURSOR = 0x854e9e0,
  GOT_PANEL_LISTENER_VTABLE = 0x8e8d588,
  GOT_LISTENER_REGISTRY = 0x8e8b540,
  OFF_OWNER_FOOTER_FLAG = 0x3f1,
  OFF_TEXT_STYLE_ID = 0x138,
};

static u16 read16(u64 a) {
  u16 v;
  memcpy(&v, host->memory + a, 2);
  return v;
}
static u32 read32(u64 a) {
  u32 v;
  memcpy(&v, host->memory + a, 4);
  return v;
}
static u64 read64(u64 a) {
  u64 v;
  memcpy(&v, host->memory + a, 8);
  return v;
}
static void write16(u64 a, u16 v) { memcpy(host->memory + a, &v, 2); }
static void write32(u64 a, u32 v) { memcpy(host->memory + a, &v, 4); }
static void write64(u64 a, u64 v) { memcpy(host->memory + a, &v, 8); }
static GuestFn function(u64 a) {
  if (a < host->code_lo || a >= host->code_hi || (a & 3))
    return NULL;
  return host->dispatch[(a - host->code_lo) / 4];
}
/* Guest calls run on the existing guest thread and stack. Preserve registers
   but never restore CPU.poll: another host thread may have requested a kernel
   yield while the call was running. */
static u64 call(CPU *c, u64 address, u64 a, u64 b, u64 d, u64 e, u64 f) {
  CPU before = *c;
  GuestFn fn = function(address);
  if (!fn) {
    host->log("missing compiled UI function");
    return 0;
  }
  c->x[0] = a;
  c->x[1] = b;
  c->x[2] = d;
  c->x[3] = e;
  c->x[4] = f;
  c->x[31] = (before.x[31] - 0x1100) & ~15ull;
  fn(c, address);
  u64 result = c->x[0];
  memcpy(c->x, before.x, sizeof c->x);
  memcpy(c->v, before.v, sizeof c->v);
  c->pc = before.pc;
  c->n = before.n;
  c->z = before.z;
  c->cf = before.cf;
  c->vf = before.vf;
  c->fpcr = before.fpcr;
  c->fpsr = before.fpsr;
  return result;
}
#define C(a, b, d, e) call(c, a, b, d, e, 0, 0)
static u64 scratch(CPU *c) { return (c->x[31] - 0x1000) & ~15ull; }
static u64 literal(CPU *c, const char *text) {
  u64 a = scratch(c) + 0x200;
  snprintf((char *)host->memory + a, 0x300, "%s", text);
  return a;
}
static u32 find(CPU *c, u64 root, const char *name) {
  u32 id = (u32)C(FN_FIND, root, literal(c, name), 0);
  if (!id && owner && root != read32(owner + 0x18))
    id = (u32)C(FN_FIND, read32(owner + 0x18), literal(c, name), 0);
  return id;
}
static void visible(CPU *c, u64 id, int show) {
  if (!id)
    return;
  u64 ref = scratch(c);
  C(FN_REF_CTOR, ref, id, 0);
  C(FN_REF_VISIBLE, ref, (u64)show, 0);
  C(FN_REF_DTOR, ref, 0, 0);
}
static u16 probe_style(CPU *c, u64 root, const char *name, u16 fallback) {
  u32 id = find(c, root, name);
  if (!id)
    return fallback;
  u64 ref = scratch(c);
  C(FN_REF_CTOR, ref, id, 0);
  u64 obj = read64(ref + 8);
  u16 style = obj ? read16(obj + OFF_TEXT_STYLE_ID) : 0;
  C(FN_REF_DTOR, ref, 0, 0);
  return (style && style != 0xffff) ? style : fallback;
}
/* Switch 2 Edition 3.0.1 UIText::SetText (0x879c990) initializes style_id at
   [obj + 0x138] from BDAT metadata when it is -1 (0xffff); raw C-strings return
   0, causing LookupFontStyle(0) (0x87946e0) to fail and skip rendering. Seed
   cloned widgets with the exact native 3.0.1 style ID before calling SetText. */
static void text_styled(CPU *c, u64 id, const char *value, u16 style) {
  if (!id)
    return;
  u64 ref = scratch(c), object = ref + 32;
  C(FN_REF_CTOR, ref, id, 0);
  if (style) {
    u64 obj = read64(ref + 8);
    if (obj) {
      u16 cur = read16(obj + OFF_TEXT_STYLE_ID);
      if (cur == 0xffff || cur == 0)
        write16(obj + OFF_TEXT_STYLE_ID, style);
    }
  }
  C(FN_TEXT_CTOR, object, literal(c, *value ? value : " "), 1);
  C(FN_REF_SET_TEXT, ref, object, 0);
  C(FN_TEXT_DTOR, object, 0, 0);
  C(FN_REF_DTOR, ref, 0, 0);
}
static void text(CPU *c, u64 id, const char *value) {
  text_styled(c, id, value, 0);
}
static void named_text_styled(CPU *c, const char *name, const char *value,
                              u16 style) {
  u32 id = find(c, panel, name);
  text_styled(c, id, value, style);
}
static void release(CPU *c, u64 object) {
  if (!object)
    return;
  u32 refs = read32(object + 8);
  if (!refs)
    return;
  write32(object + 8, refs - 1);
  if (refs == 1)
    C(read64(read64(object) + 8), object, 0, 0);
}
static int dirty(void) {
  for (int i = 0; i < COUNT; i++)
    if (settings[i].key && strcmp(staged[i], saved[i]))
      return 1;
  return 0;
}
static int setting_row(int row) {
  if (row < 1 || row > 3)
    return -1;
  for (int i = 0; i < COUNT; i++)
    if (settings[i].page == page && --row == 0)
      return i;
  return -1;
}
static int upscale_mode(const char *value) {
  if (!strcmp(value, "off"))
    return 0;
  if (!strcmp(value, "auto"))
    return 4;
  for (int mode = 1; mode <= 3; mode++)
    for (int preset = 0; preset < (mode == 1 ? 5 : 4); preset++)
      if (!strcmp(value, upscale_values[mode - 1][preset]))
        return mode;
  return -1;
}
static int upscale_preset(const char *value) {
  if (!strcmp(value, "auto"))
    return -1;
  int mode = upscale_mode(value);
  if (mode >= 1 && mode <= 3)
    for (int preset = 0; preset < (mode == 1 ? 5 : 4); preset++)
      if (!strcmp(value, upscale_values[mode - 1][preset]))
        return preset;
  return -1;
}
static void remember_preset(void) {
  editing_preset = upscale_preset(staged[UPSCALE_MODE]);
  if (editing_preset < 0)
    editing_preset = 0;
}
static int choice(int i) {
  if (i == UPSCALE_MODE)
    return upscale_mode(staged[i]);
  if (i == UPSCALE_PRESET)
    return upscale_preset(staged[UPSCALE_MODE]);
  for (int n = 0; n < settings[i].count; n++)
    if (!strcmp(staged[i], settings[i].values[n]))
      return n;
  return -1;
}
static void change_setting(int i, int step) {
  if (i == UPSCALE_MODE || i == UPSCALE_PRESET) {
    int mode = upscale_mode(staged[UPSCALE_MODE]);
    if (i == UPSCALE_MODE) {
      mode = mode < 0 ? 0 : (mode + 5 + step) % 5;
      if ((mode == 2 || mode == 3) && editing_preset == 4)
        editing_preset = 1;
    } else {
      if (mode < 1 || mode > 3)
        return;
      int count = mode == 1 ? 5 : 4;
      editing_preset = (editing_preset + count + step) % count;
    }
    snprintf(staged[UPSCALE_MODE], 32, "%s",
             mode == 4   ? "auto"
             : mode == 0 ? "off"
                         : upscale_values[mode - 1][editing_preset]);
    return;
  }
  int n = choice(i);
  n = n < 0 ? 0 : (n + settings[i].count + step) % settings[i].count;
  snprintf(staged[i], 32, "%s", settings[i].values[n]);
}
static const char *tr(const char *pt, const char *en) {
  return portuguese ? pt : en;
}
static void draw_values(CPU *c) {
  static const char *pt_pages[PAGES] = {"Geral",         "Tela",
                                         "Gráficos",      "Upscaling",
                                         "Imagem",        "HDR & Luz",
                                         "Ambiente",      "Iluminação GI",
                                         "Desempenho",    "HDR10 & Reflex",
                                         "Controles"};
  static const char *en_pages[PAGES] = {"General",         "Display",
                                         "Graphics",        "Upscaling",
                                         "Image",           "HDR & Lighting",
                                         "Environment",     "Global Lighting",
                                         "Performance",     "HDR10 & Reflex",
                                         "Controls"};
  char name[64], value[96], label[96];
  named_text_styled(c, "TXT_pc_index",
                    tr("Configurações de PC", "PC Settings"), style_index);
  for (int row = 0; row < 7; row++) {
    const char *title = "";
    const char *param = "";
    int i = setting_row(row);
    if (row == 0) {
      title = tr("Categoria", "Category");
      param = portuguese ? pt_pages[page] : en_pages[page];
    } else if (i >= 0) {
      snprintf(label, sizeof label, "%s%s",
               portuguese ? settings[i].pt : settings[i].en,
               settings[i].restart ? " *" : "");
      title = label;
      int n = choice(i);
      param = n < 0 ? staged[i]
                    : (portuguese ? settings[i].pt_values[n]
                                  : settings[i].en_values[n]);
      if (i == UPSCALE_PRESET) {
        int um = choice(UPSCALE_MODE);
        if (um == 4)
          param = tr("Automático", "Automatic");
        else if (n < 0)
          param = "-";
        else if (n == 0 && um == 2)
          param = tr("Nativo (DLAA)", "Native (DLAA)");
      } else if (i == AMIIBO_IDX && jp_names) {
        if (n == 1)
          param = "Homura";
        else if (n == 2)
          param = "Hikari";
      }
    } else if (row == 4) {
      title = tr("Aplicar alterações", "Apply changes");
      param = dirty() ? tr("Pendente", "Pending") : tr("Salvo", "Saved");
    } else if (row == 5)
      title = tr("Descartar alterações", "Discard changes");
    else if (row == 6)
      title = tr("Voltar", "Back");
    else if (page == PAGES - 1) {
      static const char *pt[] = {"Teclado", "Navegar", "Confirmar / voltar"};
      static const char *en[] = {"Keyboard", "Navigate", "Confirm / back"};
      static const char *pv[] = {"F1: comandos", "Direcional", "A / B"};
      static const char *ev[] = {"F1: controls", "D-pad", "A / B"};
      title = portuguese ? pt[row - 1] : en[row - 1];
      param = portuguese ? pv[row - 1] : ev[row - 1];
    } else {
      title = tr("Taxa real", "Actual rate");
      param = tr("Varia por PC", "Depends on PC");
    }
    snprintf(name, sizeof name, "TXT_pc_item_name@%02d", row + 1);
    named_text_styled(c, name, title, style_name);
    snprintf(value, sizeof value, "%s", param);
    snprintf(name, sizeof name, "TXT_pc_item_param_on@%02d", row + 1);
    u32 id = find(c, panel, name);
    text_styled(c, id, value, style_param);
    visible(c, id, 1);
    snprintf(name, sizeof name, "TXT_pc_item_param_off@%02d", row + 1);
    visible(c, find(c, panel, name), 0);
    snprintf(name, sizeof name, "LAY_pc_item_arrow@%02d", row + 1);
    visible(c, find(c, panel, name),
            row == 0 ||
                (i >= 0 && (i != UPSCALE_PRESET ||
                            (choice(UPSCALE_MODE) >= 1 &&
                             choice(UPSCALE_MODE) <= 3))));
  }
  int i = setting_row(focus);
  const char *help = message;
  if (!help) {
    if (focus == 0)
      help =
          tr("Esquerda/direita: categoria.\n* exige reiniciar o jogo.",
             "Left/right: choose category.\n* requires restarting the game.");
    else if (i == UPSCALE_PRESET && choice(UPSCALE_MODE) == 4)
      help = tr("Modo Automático escolhe DLSS, XeSS ou FSR 3.1\nconforme a placa de vídeo.",
                "Auto chooses DLSS, XeSS or FSR 3.1\nbased on your GPU.");
    else if (i == UPSCALE_PRESET && choice(UPSCALE_MODE) <= 0)
      help = tr("Selecione FSR, DLSS ou XeSS em Modo.\nDepois escolha o preset.",
                "Select FSR, DLSS or XeSS in Mode.\nThen choose a preset.");
    else if (i == AMIIBO_IDX && jp_names)
      help = "Use no menu Sistema > amiibo (Homura/Hikari: Novos Estilos).\nAplicado na hora com Aplicar.";
    else if (i >= 0)
      help = portuguese ? settings[i].help_pt : settings[i].help_en;
    else if (focus == 4)
      help =
          tr("Confirme para aplicar as alterações.\n* exige reiniciar o jogo.",
             "Confirm to apply changes.\n* requires restarting the game.");
    else if (focus == 5)
      help =
          tr("Descarte as alterações pendentes.", "Discard pending changes.");
    else if (focus < 4)
      help = page == PAGES - 1
                 ? tr("A: X ou Espaço. B: Z.\nSetas para navegar.",
                      "A: X or Space. B: Z.\nArrow keys to navigate.")
             : page == 4
                 ? tr("Volumetria nativa da Switch 2 Edition 3.0.1\ncom sombras e jitter corrigidos no PC.",
                      "Native Switch 2 Edition 3.0.1 volumetrics\nwith corrected shadows and jitter on PC.")
             : page == 7
                 ? tr("SSGI e SSR respeitam a máscara de personagens\npara manter o estilo anime sem artefatos.",
                      "SSGI and SSR respect the character MatID mask\nto preserve anime shading without artifacts.")
                 : tr("Informação sobre esta configuração.",
                      "Information about this setting.");
    else
      help = tr("Volte ao menu de opções do jogo.",
                "Return to the game's options menu.");
  }
  named_text_styled(c, "TXT_pc_info", help, style_info);
}
static void begin_edit(void) {
  AcquireSRWLockExclusive(&model_lock);
  memcpy(staged, current, sizeof staged);
  memcpy(saved, current, sizeof saved);
  ReleaseSRWLockExclusive(&model_lock);
  remember_preset();
  page = focus = confirm_back = 0;
  message = NULL;
}
static void request_apply(void) {
  AcquireSRWLockExclusive(&model_lock);
  memcpy(pending, staged, sizeof pending);
  apply_request = 1;
  ReleaseSRWLockExclusive(&model_lock);
  message = tr("Aplicando alterações...", "Applying changes...");
}
static void panel_back(CPU *c) {
  if (dirty() && !confirm_back) {
    confirm_back = 1;
    message = tr("Há alterações pendentes.\nVoltar novamente descarta.",
                 "You have pending changes.\nPress Back again to discard.");
    draw_values(c);
    return;
  }
  memcpy(staged, saved, sizeof staged);
  remember_preset();
  confirm_back = 0;
  message = NULL;
  C(FN_PANEL_CLOSE, panel, 0, 0);
  C(FN_PANEL_OPEN, submenu, 0, 0);
  C(FN_PANEL_SYNC, panel, 0, 0);
  call(c, FN_OPT_LEAVE_PANEL, panel, 0, 0, 0, 0);
  C(FN_SFX_CANCEL, 0, 0, 0);
}
static void panel_event(CPU *c, u64 listener, u64 event, u64 id) {
  u32 type = read32(event) & 0xffff;
  int a = (int)read32(event + 8), action = (int)read32(event + 12);
  if (type == 1) {
    visible(c, id, 0);
    C(FN_PANEL_SYNC, id, 0, 0);
  } else if (type == 0x21) {
    begin_edit();
    u64 animation = C(FN_PLAY_ANIM, id, 1, 2);
    if ((int)animation >= 0)
      C(FN_ATTACH_ANIM, id, animation, 0);
    C(FN_LIST_SELECT, rows, 0, 0);
    u64 cursor_lay = find(c, id, "LAY_pc_item_cursor");
    u64 params = scratch(c);
    memset(host->memory + params, 0, 48);
    write16(params + 0x18, 1);
    host->memory[params + 0x20] = 1;
    C(FN_CURSOR_PARAMS_INIT, params, 0, 0);
    write16(params + 0x18, 0);
    cursor = C(FN_CURSOR_CREATE, cursor_lay, params, rows);
    if (cursor_lay) {
      u64 ref = scratch(c);
      C(FN_REF_CTOR, ref, cursor_lay, 0);
      call(c, FN_REF_SET_ANIM, ref, 2, 1, 1, 0);
      C(FN_REF_DTOR, ref, 0, 0);
    }
    C(FN_PANEL_CLEAR_INPUT, id, 0, 0);
    C(FN_PANEL_BIND_LIST, id, rows, 0);
    C(FN_PANEL_SYNC, id, 0, 0);
    call(c, FN_OPT_ENTER_PANEL, id, 0, 0, 0, 0);
    C(FN_OPT_FOOTER, 0x2b, host->memory[owner + OFF_OWNER_FOOTER_FLAG], 0);
    draw_values(c);
    host->log("PC panel entered through native menu");
  } else if (type == 3) {
    if (a == 1) {
      C(FN_PANEL_ACTIVATE, id, 0, 0);
      if (cursor)
        C(FN_CURSOR_ENABLE, cursor, 1, 0);
    } else if (a != 0)
      visible(c, id, 0);
  } else if (type == 0x22) {
    if (cursor) {
      C(FN_CURSOR_ENABLE, cursor, 0, 0);
      release(c, cursor);
      cursor = 0;
    }
    C(FN_PLAY_OUT_ANIM, id, 2, 2);
    C(FN_PANEL_SYNC, id, 0, 0);
  } else if (type == 6 && a >= 0 && a < 7) {
    focus = a;
    confirm_back = 0;
    message = NULL;
    C(FN_LIST_SELECT, rows, (u64)a, 0);
    draw_values(c);
    if (a != action && action != -1)
      C(FN_SFX_CURSOR, 0, 0, 0);
  } else if (type == 8) {
    if (action == 2) {
      panel_back(c);
      return;
    }
    if (a < 0 || a >= 7)
      return;
    focus = a;
    confirm_back = 0;
    int confirm = action == 1 || action == 12;
    if (confirm && a == 4)
      request_apply();
    else if (confirm && a == 5) {
      memcpy(staged, saved, sizeof staged);
      remember_preset();
      message = tr("Alterações descartadas.", "Changes discarded.");
    } else if (confirm && a == 6) {
      panel_back(c);
      return;
    } else if (action == 11) {
      for (int i = 0; i < COUNT; i++)
        snprintf(staged[i], 32, "%s", settings[i].def);
      remember_preset();
      message = tr("Padrões restaurados.\nUse Aplicar para confirmar.",
                   "Defaults restored.\nChoose Apply to confirm.");
    } else if (action == 9 || action == 10 || confirm) {
      int step = action == 10 ? -1 : 1, i = setting_row(a);
      message = NULL;
      if (a == 0)
        page = (page + (step < 0 ? PAGES - 1 : 1)) % PAGES;
      else if (i >= 0)
        change_setting(i, step);
    }
    draw_values(c);
  } else {
    C(FN_DEFAULT_EVENT, listener, event, id);
  }
}
static void panel_hook(CPU *c, u64 entry) {
  u64 listener = c->x[0];
  if (read32(listener + 0x10) == 0x50435549 &&
      read64(listener + 0x20) == owner) {
    panel_event(c, listener, c->x[1], c->x[2]);
    c->pc = c->x[30];
    return;
  }
  originals[2](c, entry);
}
static void category_hook(CPU *c, u64 entry) {
  u64 listener = c->x[0], event = c->x[1], id = c->x[2];
  u32 type = read32(event) & 0xffff;
  int selected = (int)read32(event + 8), action = (int)read32(event + 12);
  if (read64(listener + 0x20) != owner || !panel) {
    originals[1](c, entry);
    return;
  }
  if (type == 6 && selected == 4) {
    if (action >= 0) {
      char name[64];
      snprintf(name, sizeof name, "IMG_submenu_item_sel@%02d", action + 1);
      visible(c, find(c, id, name), 0);
    }
    visible(c, find(c, id, "IMG_submenu_item_sel@05"), 1);
    C(FN_LIST_SELECT, read64(owner + 0x28), 4, 0);
    text(c, find(c, id, "TXT_submenu_index"),
         tr("Configurações de PC", "PC Settings"));
    text(c, find(c, id, "TXT_submenu_info"),
         tr("Ajuste as configurações do computador.", "Adjust PC settings."));
    C(FN_SFX_CURSOR, 0, 0, 0);
    c->pc = c->x[30];
    return;
  }
  if (type == 8 && action == 1 && selected == 4) {
    C(FN_PANEL_CLOSE, id, 0, 0);
    C(FN_PANEL_OPEN, panel, 0, 0);
    C(FN_PANEL_SYNC, id, 0, 0);
    call(c, FN_OPT_LEAVE_PANEL, id, 0, 0, 0, 0);
    C(FN_SFX_CONFIRM, 0, 0, 0);
    c->pc = c->x[30];
    return;
  }
  originals[1](c, entry);
}
static void init_hook(CPU *c, u64 entry) {
  u64 this_owner = c->x[0];
  originals[0](c, entry);
  owner = this_owner;
  panel = find(c, read32(owner + 0x18), "LAY_pc");
  if (!panel) {
    host->log("native PC layout absent; original options preserved");
    owner = 0;
    return;
  }
  submenu = find(c, read32(owner + 0x18), "LAY_submenu");
  u32 cam = find(c, read32(owner + 0x18), "LAY_cam");
  style_index = probe_style(c, submenu, "TXT_submenu_index", 37);
  style_name = probe_style(c, cam, "TXT_cam_item_name@01", 151);
  style_param = probe_style(c, cam, "TXT_cam_item_param_on@01", 35);
  style_info = probe_style(c, submenu, "TXT_submenu_info", 25);
  u64 heap = C(FN_GET_HEAP, 0, 0, 0), scratch_base = scratch(c);
  write64(scratch_base, heap);
  rows = C(FN_LIST_CTOR, scratch_base, 0, 0);
  call(c, FN_LIST_ENUM, rows, panel, literal(c, "LAY_pc_item@%02d"), 1, 0);
  u64 listener = call(c, FN_HEAP_ALLOC, 0x28, heap, 0, 0, 8);
  memset(host->memory + listener, 0, 0x28);
  write64(listener, read64(GOT_PANEL_LISTENER_VTABLE) + 16);
  write32(listener + 8, 1);
  write32(listener + 0xc, 6);
  write32(listener + 0x10, 0x50435549);
  write64(listener + 0x20, owner);
  C(FN_REGISTER_LISTENER, read64(read64(GOT_LISTENER_REGISTRY)), panel,
    listener);
  release(c, listener);
  visible(c, panel, 0);
  host->log("native PC category and seven rows bound (Switch 2 Edition 3.0.1)");
}
static void update_hook(CPU *c, u64 entry) {
  u64 this_owner = c->x[0];
  originals[3](c, entry);
  if (this_owner != owner || !panel)
    return;
  int reply = 0;
  AcquireSRWLockExclusive(&model_lock);
  if (refresh) {
    reply = apply_reply;
    refresh = 0;
    if (reply > 0) {
      for (int i = 0; i < COUNT; i++)
        if (applied_mask & (1ull << i))
          memcpy(saved[i], pending[i], sizeof saved[i]);
    }
  }
  ReleaseSRWLockExclusive(&model_lock);
  if (reply) {
    message =
        reply < 0 ? tr("Uma opção está fixa nesta execução.\nAlterações não "
                       "aplicadas.",
                       "A setting is locked for this run.\nNo changes applied.")
        : reply == 2 ? tr("Alterações ao vivo aplicadas.\nUma opção * está fixa nesta execução.",
                          "Live changes applied.\nA * setting is locked for this run.")
        : restart_needed ? tr("Salvo.\nReinicie para concluir opções com *.",
                              "Saved.\nRestart to complete settings with *.")
                         : tr("Configurações aplicadas ao vivo.", "Settings applied live.");
    draw_values(c);
  }
}
static void destroy(CPU *c) {
  if (c->x[0] != owner)
    return;
  if (cursor) {
    C(FN_CURSOR_ENABLE, cursor, 0, 0);
    release(c, cursor);
  }
  release(c, rows);
  owner = panel = submenu = rows = cursor = 0;
}
static void destructor_hook(CPU *c, u64 entry) {
  destroy(c);
  originals[4](c, entry);
}
static void deleting_hook(CPU *c, u64 entry) {
  destroy(c);
  originals[5](c, entry);
}
static void poll(void) {
  char values[COUNT][32] = {{0}};
  for (int i = 0; i < COUNT; i++)
    if (settings[i].key)
      host->read_setting(settings[i].key, values[i], 32);
  AcquireSRWLockExclusive(&model_lock);
  memcpy(current, values, sizeof current);
  if (apply_request) {
    int applied = 0, blocked = 0, changed_restart = 0;
    u64 mask = 0;
    for (int i = 0; i < COUNT; i++)
      if (settings[i].key && strcmp(pending[i], saved[i])) {
        int ok = host->can_write_setting(settings[i].key) &&
                 (i != 0 || host->can_write_setting("language"));
        if (!ok) {
          blocked++;
          continue;
        }
        host->write_setting(settings[i].key, pending[i]);
        if (i == 0)
          host->write_setting("language", pending[0][0] ? "17" : "1");
        changed_restart |= settings[i].restart;
        mask |= (1ull << i);
        applied++;
      }
    applied_mask = mask;
    restart_needed = changed_restart;
    apply_reply = blocked ? (applied ? 2 : -1) : 1;
    apply_request = 0;
    refresh = 1;
  }
  ReleaseSRWLockExclusive(&model_lock);
}
__declspec(dllexport) const SnxNativeUiExtension *
snx_native_ui_open(const SnxNativeUiHost *api);
__declspec(dllexport) const SnxNativeUiExtension *
snx_native_ui_open(const SnxNativeUiHost *api) {
  static const SnxNativeUiExtension extension = {SNX_NATIVE_UI_ABI,
                                                 sizeof extension, poll};
  if (!api || api->abi != SNX_NATIVE_UI_ABI || api->size != sizeof *api ||
      api->cpu_size != sizeof(CPU))
    return NULL;
  host = api;
  for (int i = 0; i < 6; i++) {
    u64 address = addresses[i], hash = 14695981039346656037ull;
    if (!function(address) || address + sizes[i] > host->code_hi)
      return NULL;
    for (unsigned n = 0; n < sizes[i]; n++)
      hash = (hash ^ host->memory[address + n]) * 1099511628211ull;
    if (hash != fingerprints[i]) {
      host->log("game code fingerprint mismatch; no UI callbacks changed");
      return NULL;
    }
  }
  for (int i = 0; i < COUNT; i++)
    if (settings[i].key)
      host->read_setting(settings[i].key, current[i], 32);
  portuguese = !strncmp(current[0], "pt-br", 5);
  jp_names = !strcmp(current[0], "pt-br-jp");
  GuestFn hooks[] = {init_hook,   category_hook,   panel_hook,
                     update_hook, destructor_hook, deleting_hook};
  for (int i = 0; i < 6; i++) {
    u64 slot = (addresses[i] - host->code_lo) / 4;
    originals[i] = host->dispatch[slot];
    host->dispatch[slot] = hooks[i];
  }
  host->log("verified native menu bindings installed (Switch 2 Edition 3.0.1)");
  return &extension;
}
