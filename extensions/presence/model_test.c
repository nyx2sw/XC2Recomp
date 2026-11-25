/* SPDX-License-Identifier: GPL-3.0-or-later
   Copyright 2026 the ScarletNX contributors */
/* Comprehensive unit test for Rich Presence 2.0 (Switch 2 Edition 3.0.1 live
   GameData + IraGameData + HUD + HomuriName + Common Blades + Title Screen +
   map-only fallback + dedup + PT/EN live switching). */
#include "presence.c"
#include <assert.h>

static void quiet(const char *s) { (void)s; }
static void commit_span(u64 at, size_t bytes) {
  u64 base = at & ~4095ull;
  size_t len = (size_t)((at + bytes - base + 4095ull) & ~4095ull);
  assert(VirtualAlloc(host->memory + base, len, MEM_COMMIT, PAGE_READWRITE));
}
static void put64(u64 at, u64 n) { memcpy(host->memory + at, &n, 8); }
static void put32(u64 at, u32 n) { memcpy(host->memory + at, &n, 4); }
static void put16(u64 at, u16 n) { memcpy(host->memory + at, &n, 2); }
static void put8(u64 at, u8 n) { memcpy(host->memory + at, &n, 1); }

int main(void) {
  SnxNativeUiHost fake = {0};
  fake.memory = VirtualAlloc(NULL, AS_SIZE, MEM_RESERVE, PAGE_NOACCESS);
  assert(fake.memory);
  fake.log = quiet;
  host = &fake;

  /* Activate Switch 2 Edition 3.0.1 full live profile */
  active_profile = &profiles[0];
  live_gd_ok = live_torna_ok = live_homuri_ok = live_hud_ok = 1;

  commit_span(active_profile->map_ptr, 8);
  commit_span(active_profile->zones_ptr, 8);
  commit_span(active_profile->gd_mgr_ptr, 8);
  commit_span(active_profile->torna_ptr, 8);
  commit_span(active_profile->homuri_ptr, 8);
  commit_span(active_profile->hud_ptr, 8);

  const u64 map_obj = 0x100100000ull;
  const u64 zones_obj = 0x100101000ull;
  const u64 zone_obj = 0x100102000ull;
  const u64 row_obj = 0x100103000ull;
  const u64 torna_obj = 0x100104000ull;
  const u64 homuri_obj = 0x100105000ull;
  const u64 homuri_sub = 0x100106000ull;
  const u64 hud_obj = 0x100200000ull;
  const u64 gd_mgr = 0x110000000ull;

  commit_span(map_obj, 0x100);
  commit_span(zones_obj, 0x100);
  commit_span(zone_obj, 0x100);
  commit_span(row_obj, 0x100);
  commit_span(torna_obj, 0x100);
  commit_span(homuri_obj, 0x200);
  commit_span(homuri_sub, 0x200);
  commit_span(hud_obj + HUD_DRV_OFF, 0x100);
  commit_span(gd_mgr, 0x150000);

  put64(active_profile->map_ptr, map_obj);
  put64(active_profile->zones_ptr, zones_obj);
  put64(zones_obj + 0x20, zone_obj);
  put64(zone_obj + 0x30, row_obj);
  put64(active_profile->gd_mgr_ptr, gd_mgr);
  put64(active_profile->torna_ptr, torna_obj);
  put64(active_profile->homuri_ptr, homuri_obj);
  put64(homuri_obj + 0x138, homuri_sub);
  put64(active_profile->hud_ptr, hud_obj);

  InterlockedExchange(&portuguese, 1);
  load_names();
  assert(name_of('M', 9) && name_of('N', 151) && name_of('N', 176));
  assert(name_of('D', 17) && !strcmp(name_of('D', 17), "Lora"));
  assert(name_of('B', 1113) && !strcmp(name_of('B', 1113), "MOMO"));
  assert(name_of('H', 111) && !strcmp(name_of('H', 111), "MOMO Real"));
  assert(!name_of('B', 1003)); /* no raw [ML:HomuriName ] in table */
  assert(!name_of('B', 1124)); /* no raw Utsuro in table */

  /* Find Reika's Common Blade name_id in C table */
  unsigned reika_cid = 0;
  for (int i = 0; i < name_count; i++)
    if (names[i].kind == 'C' && !strcmp(names[i].en, "Reika")) {
      reika_cid = names[i].id;
      break;
    }
  assert(reika_cid >= 2);

  /* Populate Base GameData: Rex (leader 1, lv 99) with 4 blade slots:
     slot 0 = Pyra(1001), slot 1 = Roc(1008), slot 2 = Common Blade 1252 (Reika), slot 3 = Mythra(1002) */
  u64 gd = gd_mgr + GD_BASE_OFF;
  put32(gd + GD_PARTY_LEADER, 1);
  u64 rex = gd + GD_DRIVERS + 0 * DRIVER_SIZE;
  put16(rex + DRIVER_ID, 1);
  put16(rex + DRIVER_LEVEL, 99);
  put16(rex + DRIVER_SET_BLADE, 3); /* slot 3 = Mythra! (previously broken by set < 3 clamp) */
  put16(rex + DRIVER_BLADES + 0, 1001);
  put16(rex + DRIVER_BLADES + 2, 1008);
  put16(rex + DRIVER_BLADES + 4, 1252);
  put16(rex + DRIVER_BLADES + 6, 1002);

  /* Populate SaveBlade entries */
  u64 bl_pyra = gd + GD_BLADES + (u64)blade_slot_index(1001) * BLADE_SIZE;
  put16(bl_pyra + BLADE_RARE_NAME_ID, 1);
  u64 bl_mythra = gd + GD_BLADES + (u64)blade_slot_index(1002) * BLADE_SIZE;
  put16(bl_mythra + BLADE_RARE_NAME_ID, 2);
  u64 bl_roc = gd + GD_BLADES + (u64)blade_slot_index(1008) * BLADE_SIZE;
  put16(bl_roc + BLADE_RARE_NAME_ID, 8);
  u64 bl_reika = gd + GD_BLADES + (u64)blade_slot_index(1252) * BLADE_SIZE;
  put16(bl_reika + BLADE_RARE_NAME_ID, 0);
  put16(bl_reika + BLADE_COMM_NAME_ID, (u16)reika_cid);
  u64 bl_momo = gd + GD_BLADES + (u64)blade_slot_index(1113) * BLADE_SIZE;
  put16(bl_momo + BLADE_RARE_NAME_ID, 111); /* MOMO Real */

  char json[1100];

  /* 1. Title Screen (map 39 ma70a & map 1 ma90a): NEVER leak Rex/Mythra! */
  put32(map_obj + active_profile->map_off, 39);
  put16(row_obj, 151);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(!current_in_world && current_on_title);
  assert(strstr(json, "Na Tela de Título") && !strstr(json, "Rex") && !strstr(json, "state"));

  put32(map_obj + active_profile->map_off, 1);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(!current_in_world && current_on_title);
  assert(strstr(json, "Na Tela de Título") && !strstr(json, "Rex") && !strstr(json, "state"));

  /* 2. In-world Teatro Olethro with Rex slot 3 (Mythra) via GameData (HUD empty) */
  put32(map_obj + active_profile->map_off, 9);
  put16(row_obj, 151);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Reino de Uraya · Teatro Olethro"));
  assert(strstr(json, "Rex com Mythra · Nv. 99"));

  /* 3. Walk to Ruínas de Olethro + live Blade switch to Common Blade 1252 (Reika) via set_blade=2! */
  put16(row_obj, 176);
  put16(rex + DRIVER_SET_BLADE, 2);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Reino de Uraya · Ruínas de Olethro"));
  assert(strstr(json, "Rex com Reika · Nv. 99"));

  /* 4. Live HUD override: instant switch to Pyra (1001), Roc (1008), MOMO Real (1113) */
  put32(hud_obj + HUD_DRV_OFF, 1);
  put32(hud_obj + HUD_BLADE_OFF, 1001);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Rex com Pyra · Nv. 99"));

  put32(hud_obj + HUD_BLADE_OFF, 1113);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Rex com MOMO Real · Nv. 99"));

  /* 5. Pneuma (1003) respects player's HomuriName choice (1=Pyra, 2=Mythra) */
  put32(hud_obj + HUD_BLADE_OFF, 1003);
  put8(homuri_sub + 0x85, 1);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Rex com Pyra · Nv. 99") && !strstr(json, "ML:"));

  put8(homuri_sub + 0x85, 2);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Rex com Mythra · Nv. 99") && !strstr(json, "ML:"));
  put32(hud_obj + HUD_DRV_OFF, 0);
  put32(hud_obj + HUD_BLADE_OFF, 0);

  /* 6. Map without landmark zone (zones->+0x20 == 0) still shows Titan/Region! */
  put32(map_obj + active_profile->map_off, 58); /* Terra do Desafio */
  put64(zones_obj + 0x20, 0);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "\"details\":\"Terra do Desafio\""));

  /* 7. Live EN & PT-BR (JP names) language toggle */
  InterlockedExchange(&portuguese, 0);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "\"details\":\"Land of Challenge\""));
  assert(strstr(json, "Rex with Reika · Lv. 99"));

  InterlockedExchange(&portuguese, 2);
  put32(map_obj + active_profile->map_off, 9);
  put64(zones_obj + 0x20, zone_obj);
  put16(row_obj, 151);
  put16(rex + DRIVER_SET_BLADE, 3); /* Mythra -> Hikari */
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Reino de Invidia · Teatro Kalam"));
  assert(strstr(json, "Rex com Hikari · Nv. 99"));
  put16(rex + DRIVER_SET_BLADE, 0); /* Pyra -> Homura */
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Rex com Homura · Nv. 99"));
  put16(rex + DRIVER_SET_BLADE, 2);
  InterlockedExchange(&portuguese, 1);

  /* 8. Torna ~ The Golden Country (is_torna=1): Lora Vanguard + Jin Rear Guard, then Vanguard switch! */
  put32(torna_obj + 0x8, 1);
  put32(map_obj + active_profile->map_off, 52); /* Reino de Torna */
  put64(zones_obj + 0x20, zone_obj);
  put16(row_obj, 0);
  u64 ira = gd_mgr + IRA_BASE_OFF;
  put32(ira + IRA_PARTY_LEADER, 17); /* Lora */
  put16(ira + IRA_VANGUARDS, 17);
  put16(ira + IRA_REARGUARDS, 20); /* Jin */
  put16(ira + (17 - 17) * IRA_DRIVER_SIZE + DRIVER_ID, 17);
  put16(ira + (17 - 17) * IRA_DRIVER_SIZE + DRIVER_LEVEL, 42);
  put16(ira + (20 - 17) * IRA_DRIVER_SIZE + DRIVER_ID, 20);
  put16(ira + (20 - 17) * IRA_DRIVER_SIZE + DRIVER_LEVEL, 42);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "\"details\":\"Reino de Torna\""));
  assert(strstr(json, "Lora com Jin · Nv. 42"));

  /* Vanguard swap to Jin + Lora Rear Guard via HUD Torna Blade ID 1124 (= 17 Lora) */
  put32(hud_obj + HUD_DRV_OFF, 20);
  put32(hud_obj + HUD_BLADE_OFF, 1124);
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Jin com Lora · Nv. 42"));

  /* 9. Return to Torna Title Screen clears party cleanly (PT-BR & PT-BR JP) */
  put32(map_obj + active_profile->map_off, 1);
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(strstr(json, "Torna · Na Tela de Título") && !strstr(json, "state"));

  InterlockedExchange(&portuguese, 2);
  describe(json, sizeof json);
  assert(strstr(json, "Ira · Na Tela de Título") && !strstr(json, "state"));
  InterlockedExchange(&portuguese, 1);

  /* 10. Loading / unmapped pointer fails closed cleanly */
  put32(torna_obj + 0x8, 0);
  put32(map_obj + active_profile->map_off, 0);
  assert(VirtualFree(host->memory + row_obj, 4096, MEM_DECOMMIT));
  track_location();
  track_live_party();
  describe(json, sizeof json);
  assert(!current_in_world && !current_on_title && !strstr(json, "details") && !strstr(json, "state"));

  assert(VirtualFree(fake.memory, 0, MEM_RELEASE));
  puts("PASS: Rich Presence 2.0 (3.0.1 live GameData/IraGameData/HUD/HomuriName/Common Blades/MOMO/Torna/Title/PT-EN)");
  return 0;
}
