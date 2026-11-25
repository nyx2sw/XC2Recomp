/* SPDX-License-Identifier: GPL-3.0-or-later
   Copyright 2026 the ScarletNX contributors */
/* Unit tests for native_menu.c (Switch 2 Edition 3.0.1) settings model:
   staging, dirty/discard, FSR/DLSS mode+preset coupling, Discord toggle,
   locked CLI settings, and restart flag tracking. */
#include "native_menu.c"
#include <assert.h>

static char store[COUNT + 1][32];
static int locked_key = -1;

static const char *key_name(int i) {
  return i == COUNT ? "language" : settings[i].key;
}
static int key_idx(const char *k) {
  for (int i = 0; i <= COUNT; i++)
    if (key_name(i) && !strcmp(key_name(i), k))
      return i;
  return -1;
}
static void fake_read(const char *k, char *out, size_t cap) {
  int i = key_idx(k);
  snprintf(out, cap, "%s", i >= 0 ? store[i] : "");
}
static int fake_can_write(const char *k) {
  return key_idx(k) != locked_key;
}
static int fake_write(const char *k, const char *v) {
  int i = key_idx(k);
  assert(i >= 0);
  snprintf(store[i], sizeof store[i], "%s", v);
  return 1;
}
static void quiet(const char *s) { (void)s; }

int main(void) {
  SnxNativeUiHost fake = {0};
  fake.read_setting = fake_read;
  fake.can_write_setting = fake_can_write;
  fake.write_setting = fake_write;
  fake.log = quiet;
  host = &fake;

  for (int i = 0; i < COUNT; i++)
    if (settings[i].key)
      snprintf(store[i], sizeof store[i], "%s", settings[i].def);
  snprintf(store[COUNT], sizeof store[COUNT], "12");
  snprintf(store[UPSCALE_MODE], sizeof store[UPSCALE_MODE], "dlss_native");

  poll();
  begin_edit();
  assert(!dirty());
  assert(choice(UPSCALE_MODE) == 2 && choice(UPSCALE_PRESET) == 0);

  /* Switch upscale preset DLSS native -> quality -> FSR -> 1080p -> DLSS (falls back to quality) -> XeSS -> Auto -> Off */
  change_setting(UPSCALE_PRESET, 1);
  assert(!strcmp(staged[UPSCALE_MODE], "dlss_quality"));
  change_setting(UPSCALE_MODE, -1); /* DLSS -> FSR */
  assert(!strcmp(staged[UPSCALE_MODE], "fsr3_quality"));
  change_setting(UPSCALE_PRESET, 3); /* quality(1) -> 1080p(4) */
  assert(!strcmp(staged[UPSCALE_MODE], "fsr3"));
  change_setting(UPSCALE_MODE, 1); /* FSR -> DLSS drops legacy 1080p -> dlss_quality */
  assert(!strcmp(staged[UPSCALE_MODE], "dlss_quality"));
  change_setting(UPSCALE_MODE, 1); /* DLSS -> XeSS */
  assert(!strcmp(staged[UPSCALE_MODE], "xess_quality") && choice(UPSCALE_MODE) == 3);
  change_setting(UPSCALE_MODE, 1); /* XeSS -> Auto */
  assert(!strcmp(staged[UPSCALE_MODE], "auto") && choice(UPSCALE_MODE) == 4);
  change_setting(UPSCALE_MODE, 1); /* Auto -> Off */
  assert(!strcmp(staged[UPSCALE_MODE], "off") && choice(UPSCALE_MODE) == 0);
  change_setting(UPSCALE_MODE, 2); /* Off -> FSR -> DLSS */
  assert(!strcmp(staged[UPSCALE_MODE], "dlss_quality"));
  assert(dirty());

  /* Cycle DLSS 4.5 render preset (auto -> k -> m -> l -> j -> auto) */
  int dlss_preset_idx = key_idx("dlss_preset");
  assert(dlss_preset_idx == 9 && settings[dlss_preset_idx].page == 3);
  assert(!strcmp(staged[dlss_preset_idx], "auto"));
  change_setting(dlss_preset_idx, 1);
  assert(!strcmp(staged[dlss_preset_idx], "k"));
  change_setting(dlss_preset_idx, 1);
  assert(!strcmp(staged[dlss_preset_idx], "m"));
  change_setting(dlss_preset_idx, 3);
  assert(!strcmp(staged[dlss_preset_idx], "auto"));

  /* Toggle FPS counter live (Page 8 Desempenho, restart == 0) */
  int show_fps_idx = key_idx("show_fps");
  assert(show_fps_idx >= 0 && settings[show_fps_idx].page == 8 && !settings[show_fps_idx].restart);
  assert(!strcmp(staged[show_fps_idx], "0"));
  change_setting(show_fps_idx, 1);
  assert(!strcmp(staged[show_fps_idx], "1"));
  snprintf(staged[UPSCALE_MODE], 32, "dlss_native");
  assert(dirty());
  request_apply();
  poll();
  assert(apply_reply == 1 && !restart_needed && !strcmp(store[show_fps_idx], "1"));
  memcpy(saved, pending, sizeof saved);

  /* Verify all 8 Tier 1, Tier 2 & Tier 3 visual enhancement keys live (Pages 5, 6 & 7, restart == 0) */
  int tm_idx = key_idx("tonemap");
  int bm_idx = key_idx("bloom_mode");
  int cs_idx = key_idx("contact_shadows");
  int gtao_idx = key_idx("gtao");
  int env_idx = key_idx("env_shading");
  int wc_idx = key_idx("water_caustics");
  int ssgi_idx = key_idx("ssgi");
  int ssr_idx = key_idx("ssr");
  assert(tm_idx >= 0 && settings[tm_idx].page == 5 && !settings[tm_idx].restart);
  assert(bm_idx >= 0 && settings[bm_idx].page == 5 && !settings[bm_idx].restart);
  assert(cs_idx >= 0 && settings[cs_idx].page == 5 && !settings[cs_idx].restart);
  assert(gtao_idx >= 0 && settings[gtao_idx].page == 6 && !settings[gtao_idx].restart);
  assert(env_idx >= 0 && settings[env_idx].page == 6 && !settings[env_idx].restart);
  assert(wc_idx >= 0 && settings[wc_idx].page == 6 && !settings[wc_idx].restart);
  assert(ssgi_idx >= 0 && settings[ssgi_idx].page == 7 && !settings[ssgi_idx].restart);
  assert(ssr_idx >= 0 && settings[ssr_idx].page == 7 && !settings[ssr_idx].restart);
  change_setting(tm_idx, 1); /* native -> gt7 */
  assert(!strcmp(staged[tm_idx], "gt7"));
  change_setting(tm_idx, 1); /* gt7 -> agx */
  assert(!strcmp(staged[tm_idx], "agx"));
  change_setting(tm_idx, 2); /* agx -> uchimura -> pbr_neutral */
  assert(!strcmp(staged[tm_idx], "pbr_neutral"));
  change_setting(tm_idx, 1); /* pbr_neutral -> native */
  assert(!strcmp(staged[tm_idx], "native"));
  change_setting(tm_idx, 1); /* native -> gt7 */
  assert(!strcmp(staged[tm_idx], "gt7"));
  change_setting(bm_idx, 1);
  change_setting(cs_idx, 1);
  change_setting(gtao_idx, 1);
  change_setting(env_idx, 1);
  change_setting(wc_idx, 1);
  change_setting(ssgi_idx, 1);
  change_setting(ssr_idx, 1);
  request_apply();
  poll();
  assert(apply_reply == 1 && !restart_needed);
  assert(!strcmp(store[tm_idx], "gt7") && !strcmp(store[bm_idx], "physical") &&
         !strcmp(store[cs_idx], "1") && !strcmp(store[gtao_idx], "1") &&
         !strcmp(store[env_idx], "1") && !strcmp(store[wc_idx], "1") &&
         !strcmp(store[ssgi_idx], "1") && !strcmp(store[ssr_idx], "1"));
  memcpy(saved, pending, sizeof saved);

  /* Toggle Discord Presence live (restart == 0) */
  assert(!dirty());
  change_setting(1, 1);
  assert(!strcmp(staged[1], "0") && dirty());
  request_apply();
  poll();
  assert(apply_reply == 1 && !restart_needed && !strcmp(store[1], "0"));

  /* Verify only resource_pack (0) requires restart; all other 15 rows (including virtual amiibo) apply live */
  int amiibo_idx = key_idx("amiibo");
  assert(amiibo_idx == 10 && settings[amiibo_idx].page == 0 && !settings[amiibo_idx].restart);
  assert(!strcmp(staged[amiibo_idx], "0"));
  change_setting(amiibo_idx, 1);
  assert(!strcmp(staged[amiibo_idx], "1")); /* Pyra */
  change_setting(amiibo_idx, 1);
  assert(!strcmp(staged[amiibo_idx], "2")); /* Mythra */
  change_setting(amiibo_idx, 4);
  assert(!strcmp(staged[amiibo_idx], "0")); /* wrap back to off */

  /* Verify new scalability, HDR10, Reflex, Cloud Sea, Shadow Soften & 3-mode VSync rows */
  int qp_idx = key_idx("quality_preset");
  int cq_idx = key_idx("cloud_quality");
  int ss_idx = key_idx("shadow_soften");
  int ll_idx = key_idx("low_latency");
  int hdr_idx = key_idx("hdr");
  int peak_idx = key_idx("hdr_peak_nits");
  int vs_idx = key_idx("vsync");
  assert(qp_idx >= 0 && settings[qp_idx].page == 1 && settings[qp_idx].restart == 1);
  assert(cq_idx >= 0 && settings[cq_idx].page == 4 && settings[cq_idx].restart == 1);
  assert(ss_idx >= 0 && settings[ss_idx].page == 7 && settings[ss_idx].restart == 0);
  assert(ll_idx >= 0 && settings[ll_idx].page == 9 && settings[ll_idx].restart == 0);
  assert(hdr_idx >= 0 && settings[hdr_idx].page == 9 && settings[hdr_idx].restart == 0);
  assert(peak_idx >= 0 && settings[peak_idx].page == 9 && settings[peak_idx].restart == 0);
  assert(vs_idx >= 0 && settings[vs_idx].count == 3);
  change_setting(vs_idx, 1); /* 1 -> 2 (Strict FIFO) */
  assert(!strcmp(staged[vs_idx], "2"));
  change_setting(vs_idx, 1); /* 2 -> 0 (VRR) */
  assert(!strcmp(staged[vs_idx], "0"));
  change_setting(vs_idx, 1); /* 0 -> 1 (Adaptive) */
  assert(!strcmp(staged[vs_idx], "1"));
  change_setting(ll_idx, 1); /* 1 -> 2 (Reflex + Boost) */
  assert(!strcmp(staged[ll_idx], "2"));
  change_setting(hdr_idx, 1); /* 0 -> 1 (HDR10) */
  assert(!strcmp(staged[hdr_idx], "1"));
  change_setting(ss_idx, 1); /* 0 -> 1 (Smooth shadows) */
  assert(!strcmp(staged[ss_idx], "1"));
  request_apply();
  poll();
  assert(apply_reply == 1 && !restart_needed && !strcmp(store[ll_idx], "2") &&
         !strcmp(store[hdr_idx], "1") && !strcmp(store[ss_idx], "1"));
  memcpy(saved, pending, sizeof saved);

  for (int i = 0; i < COUNT; i++) {
    int expect_restart = (i == 0 || i == qp_idx || i == cq_idx);
    assert(settings[i].restart == expect_restart);
  }

  /* Locked setting alone returns -1 and does not modify store */
  memcpy(saved, pending, sizeof saved);
  locked_key = 1;
  change_setting(1, 1);
  request_apply();
  poll();
  assert(apply_reply == -1 && !strcmp(store[1], "0"));

  /* Mixed batch: locked key blocked, unlocked live key still applies (reply == 2) */
  change_setting(show_fps_idx, 1);
  request_apply();
  poll();
  assert(apply_reply == 2 && !strcmp(store[1], "0") && !strcmp(store[show_fps_idx], "0"));
  locked_key = -1;

  /* restart_needed is NOT sticky across subsequent live-only applies, and resource_pack cycles pt-br -> pt-br-jp -> "" */
  memcpy(saved, store, sizeof saved);
  memcpy(staged, store, sizeof staged);
  change_setting(0, 1); /* pt-br -> pt-br-jp (restart=1) */
  assert(!strcmp(staged[0], "pt-br-jp"));
  request_apply();
  poll();
  assert(apply_reply == 1 && restart_needed == 1 && !strcmp(store[0], "pt-br-jp") && !strcmp(store[COUNT], "17"));
  memcpy(saved, pending, sizeof saved);
  change_setting(0, 1); /* pt-br-jp -> "" (English) */
  assert(!strcmp(staged[0], ""));
  request_apply();
  poll();
  assert(apply_reply == 1 && restart_needed == 1 && !strcmp(store[0], "") && !strcmp(store[COUNT], "1"));
  memcpy(saved, pending, sizeof saved);
  change_setting(amiibo_idx, 1); /* live-only virtual amiibo */
  request_apply();
  poll();
  assert(apply_reply == 1 && restart_needed == 0 && !strcmp(store[amiibo_idx], "1"));

  puts("PASS: 3.0.1 native_menu settings model, 23 live rows (incl. Tier 1/2/3 shaders & virtual amiibo), partial apply, non-sticky restart");
  return 0;
}
