/* SPDX-License-Identifier: GPL-3.0-or-later
   Copyright 2026 the ScarletNX contributors */
/* Discord Rich Presence 2.0 for Xenoblade Chronicles 2 (Switch 2 Edition 3.0.1
   with 100% live engine memory reads + Switch 1 v2.1.0 fallback).
   Talks directly to Discord's local pipe (discord-ipc-N): no SDK, read-only
   memory reads on a lowest-priority background thread, zero full-RAM scanning
   on 3.0.1 so gameplay frames never stall. */
#include "runtime/native_ui.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <windows.h>

static const SnxNativeUiHost *host;
static const char default_client_id[] = "1565188165587828832";
static char client_id[24];
static long long started; /* Unix seconds when the game started */
static volatile LONG enabled;
static volatile LONG portuguese; /* 0 = EN, 1 = PT-BR, 2 = PT-BR (JP names) */
static HANDLE pipe = INVALID_HANDLE_VALUE;
static unsigned nonce;

/* Live tracked world & party state (owned by worker thread) */
static unsigned current_map, current_location;
static int current_in_world, current_on_title, current_torna;
static unsigned party_level;
static char party_driver_name[96], party_blade_name[96];

typedef struct {
  const char *label;
  u64 loc_fn, loc_sig;
  u64 map_fn, map_sig;
  u64 map_ptr, map_off;
  u64 zones_ptr;
  /* Switch 2 Edition 3.0.1 direct live engine singletons (0 on legacy Switch 1) */
  u64 gd_fn, gd_sig;
  u64 ira_fn, ira_sig;
  u64 gd_mgr_ptr;
  u64 torna_fn, torna_sig, torna_ptr;
  u64 homuri_fn, homuri_sig, homuri_ptr;
  u64 hud_fn, hud_sig, hud_ptr;
} LocationProfile;

static const LocationProfile profiles[] = {
    /* Switch 2 Edition 3.0.1 (module main @ 0x08021000):
       - TXT_location binding 6 (0x82ee870): *(u64*)0x90919e0 -> +0x20 -> +0x30 -> u16 fld_landmark
       - FLD_maplist getter (0x8322ff0): *(u64*)0x9091f88 -> u32[+0xc]
       - Live GameData (0x8293ca0): *(u64*)0x8f4e2c0 + 0x20
       - Live IraGameData (0x8293ce0): *(u64*)0x8f4e2c0 + 0x126e28
       - IsTornaMode (0x8326bc0): *(u64*)0x8f47a78 -> u32[+0x8] == 1
       - HomuriName choice (0x8375c60): *(u64*)0x8f47a20 -> *(u64*)(+0x138) -> u8[+0x85] == 2
       - Active HUD party (0x8407930/70): *(u64*)0x91cd400 -> +0x4cc00 (drv), +0x4cc20 (blade) */
    {"Switch 2 Edition 3.0.1",
     0x82ee870, 0xac3d77799f39abd6ull,
     0x8322ff0, 0x7a075bd931b3a152ull,
     0x9091f88, 0xc, 0x90919e0,
     0x8293ca0, 0xbd6712ee0096dfadull,
     0x8293ce0, 0x1d53990b6c7f8431ull,
     0x8f4e2c0,
     0x8326bc0, 0x2d9f0ac56f472cd0ull, 0x8f47a78,
     0x8375c60, 0x14862bd147400ffbull, 0x8f47a20,
     0x8407930, 0xdcead038f3558560ull, 0x91cd400},
    /* Switch 1 v2.1.0 (legacy save-block fallback for party): */
    {"Switch 1 v2.1.0",
     0x827c0fc, 0xf940ac4f7531d2b7ull,
     0x82aaa1c, 0xcb8628412af46130ull,
     0x8d70bd8, 0xc, 0x8d708d8,
     0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
};
static const LocationProfile *active_profile;
static int live_gd_ok, live_torna_ok, live_homuri_ok, live_hud_ok;

enum { OP_HANDSHAKE, OP_FRAME, OP_CLOSE, OP_PING, OP_PONG };

typedef struct {
  char kind;
  unsigned id;
  const char *pt, *en, *pt_jp;
} Name;
static Name *names;
static int name_count;

static int lang_mode(void) { return (int)InterlockedCompareExchange(&portuguese, 0, 0); }
static int is_pt(void) { return lang_mode() != 0; }

static void load_names(void) {
  char path[MAX_PATH];
  HMODULE self;
  if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                              GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          (LPCSTR)(void *)load_names, &self))
    return;
  DWORD n = GetModuleFileNameA(self, path, sizeof path);
  char *slash = strrchr(path, '\\');
  if (!n || n >= sizeof path || !slash ||
      (size_t)(slash + 1 - path) + sizeof "presence_names.tsv" > sizeof path)
    return;
  strcpy(slash + 1, "presence_names.tsv");
  FILE *f = fopen(path, "rb");
  if (!f) {
    host->log("no presence_names.tsv; the card shows the time only");
    return;
  }
  fseek(f, 0, SEEK_END);
  long size = ftell(f);
  fseek(f, 0, SEEK_SET);
  char *text = size > 0 ? malloc((size_t)size + 1) : NULL;
  if (!text || fread(text, 1, (size_t)size, f) != (size_t)size) {
    fclose(f);
    free(text);
    return;
  }
  fclose(f);
  text[size] = 0;
  int lines = 0;
  for (char *c = text; *c; c++)
    lines += *c == '\n';
  names = calloc((size_t)lines + 1, sizeof *names);
  if (!names)
    return;
  for (char *line = text, *next; *line; line = next) {
    next = strchr(line, '\n');
    next = next ? (*next = 0, next + 1) : line + strlen(line);
    size_t len = strlen(line);
    if (len && line[len - 1] == '\r')
      line[len - 1] = 0;
    char *id = strchr(line, '\t'), *pt = id ? strchr(id + 1, '\t') : NULL,
         *en = pt ? strchr(pt + 1, '\t') : NULL,
         *jp = en ? strchr(en + 1, '\t') : NULL;
    if (!en)
      continue;
    *id++ = *pt++ = *en++ = 0;
    if (jp)
      *jp++ = 0;
    names[name_count++] =
        (Name){line[0], (unsigned)strtoul(id, NULL, 10), pt, en,
               (jp && *jp) ? jp : pt};
  }
}
static const char *name_of(char kind, unsigned id) {
  if (!id)
    return NULL;
  int mode = lang_mode();
  for (int i = 0; i < name_count; i++)
    if (names[i].kind == kind && names[i].id == id)
      return mode == 2 ? names[i].pt_jp : (mode == 1 ? names[i].pt : names[i].en);
  return NULL;
}

/* Base GameData offsets (mgr + 0x20 == bf2savefile.sav + 0x10) */
enum {
  GD_BASE_OFF = 0x20,
  GD_DRIVERS = 0x2C,
  DRIVER_SIZE = 0x5A0,
  DRIVER_ID = 0x22,
  DRIVER_SET_BLADE = 0x24, /* signed s16: 0..3 (Rex slot 3 = Mythra!) */
  DRIVER_BLADES = 0x26,    /* u16[4] */
  DRIVER_LEVEL = 0x94,
  GD_BLADES = 0x5A2C,
  BLADE_SIZE = 0x8A4,
  BLADE_RARE_NAME_ID = 0x832, /* u16 index into chr_bl_ms ('H') */
  BLADE_COMM_NAME_ID = 0x834, /* u16 index into bld_bladename ('C') */
  GD_PARTY_LEADER = 0xE98C4,
  GD_HIKARI = 0x10AE7D,

  /* Torna IraGameData offsets (mgr + 0x126E28) */
  IRA_BASE_OFF = 0x126E28,
  IRA_DRIVER_SIZE = 0xDAC,
  IRA_VANGUARDS = 0x10D04, /* 3 teams, stride 6, u16 driver id (17..25) */
  IRA_REARGUARDS = 0x10D40, /* 3 teams, stride 6, u16[2] partner ids (17..25) */
  IRA_TEAM_STRIDE = 6,
  IRA_PARTY_LEADER = 0x10D7C,

  /* Active HUD party offsets (*(u64*)hud_ptr) */
  HUD_DRV_OFF = 0x4CC00,
  HUD_BLADE_OFF = 0x4CC20,

  /* Legacy Switch 1 save-block scan offsets (bf2savefile.sav + 0x00) */
  SAVE_MAP_JUMP = 0x14,
  SAVE_DRIVERS = 0x3C,
  SAVE_PARTY_LEADER = 0xE98D4,
  SAVE_PLAY_TIME = 0x1098D4,
  SAVE_HIKARI = 0x10AE8D,
  SAVE_SPAN = 0x10AE90,

  BLADE_PYRA = 1001,
  BLADE_MYTHRA = 1002,
  BLADE_PNEUMA = 1003,
  BLADE_PNEUMA_ALT = 1074,
  BLADE_TORNA_FIRST = 1124,
  BLADE_TORNA_LAST = 1132,
  BLADE_TORNA_TO_DRV = 0x453,
};

#define AS_BASE 0x08000000ull
#define AS_SIZE 0x2000000000ull
#define SCAN_CHUNK (1u << 20)

/* Legacy Switch 1 fallback state */
static u64 live;
static u64 candidates[8];
static u32 candidate_time[8];
static int candidate_count;
static u64 known[16];
static u32 known_time[16];
static ULONGLONG known_written[16];
static int known_count;
static int trusted;

static int peek(u64 guest, void *out, size_t size) {
  SIZE_T got;
  return guest >= AS_BASE && guest + size <= AS_SIZE &&
         ReadProcessMemory(GetCurrentProcess(), host->memory + guest, out, size,
                           &got) &&
         got == size;
}
static u32 peek32(u64 guest) {
  u32 v = 0;
  peek(guest, &v, sizeof v);
  return v;
}
static unsigned peek16(u64 guest) {
  u16 v = 0;
  peek(guest, &v, sizeof v);
  return v;
}
static unsigned peek8(u64 guest) {
  u8 v = 0;
  peek(guest, &v, sizeof v);
  return v;
}
static u64 signature(u64 at) {
  unsigned char bytes[24];
  if (!at || !peek(at, bytes, sizeof bytes))
    return 0;
  u64 hash = 14695981039346656037ull;
  for (unsigned i = 0; i < sizeof bytes; i++)
    hash = (hash ^ bytes[i]) * 1099511628211ull;
  return hash;
}
static u64 pointer(u64 at) {
  u64 p = 0;
  return at && peek(at, &p, sizeof p) && p >= AS_BASE && p < AS_SIZE ? p : 0;
}

/* Exact SaveBlade slot mapping from 3.0.1 0x829f010 */
static int blade_slot_index(unsigned blade_id) {
  if (blade_id >= 1001 && blade_id <= 1013)
    return (int)(blade_id - 1001);
  if (blade_id >= 1014 && blade_id <= 1189)
    return (int)(blade_id - 994);
  if (blade_id >= 1197 && blade_id <= 1422)
    return (int)(blade_id - 1001);
  return -1;
}

/* Player's Chapter 8 choice for Pneuma ([ML:HomuriName ]): 2 = Mythra, else Pyra */
static const char *resolve_homuri_name(void) {
  if (active_profile && live_homuri_ok) {
    u64 obj = pointer(active_profile->homuri_ptr);
    u64 sub = obj ? pointer(obj + 0x138) : 0;
    if (sub && peek8(sub + 0x85) == 2)
      return name_of('B', BLADE_MYTHRA);
  }
  return name_of('B', BLADE_PYRA);
}

/* Resolve any Base/DLC/Story/Common/Torna Blade ID using live SaveBlade + BDAT tables */
static const char *resolve_blade_name(u64 base_gd, unsigned blade_id) {
  if (!blade_id)
    return NULL;
  if (blade_id == BLADE_PNEUMA || blade_id == BLADE_PNEUMA_ALT)
    return resolve_homuri_name();
  if (blade_id >= BLADE_TORNA_FIRST && blade_id <= BLADE_TORNA_LAST)
    return name_of('D', blade_id - BLADE_TORNA_TO_DRV);
  if (base_gd) {
    int slot = blade_slot_index(blade_id);
    if (slot >= 0) {
      u64 bl = base_gd + GD_BLADES + (u64)slot * BLADE_SIZE;
      unsigned rare_nid = peek16(bl + BLADE_RARE_NAME_ID);
      unsigned comm_nid = peek16(bl + BLADE_COMM_NAME_ID);
      if (rare_nid == 3)
        return resolve_homuri_name();
      if (rare_nid) {
        const char *rn = name_of('H', rare_nid);
        if (rn)
          return rn;
      }
      if (comm_nid >= 2) {
        const char *cn = name_of('C', comm_nid);
        if (cn)
          return cn;
      }
    }
  }
  return name_of('B', blade_id);
}

static int is_title_map(unsigned map) {
  /* FLD_maplist: 1=ma90a (blank/title), 39..44=ma70a..ma75a & 50..51=ma76a..ma77a
     (3D Title Screen dioramas across chapters/NG+). */
  return map == 1 || (map >= 39 && map <= 44) || map == 50 || map == 51;
}

static void track_location(void) {
  unsigned map = 0, location = 0;
  int in_world = 0, on_title = 0, torna = 0;
  if (active_profile) {
    if (live_torna_ok) {
      u64 tm = pointer(active_profile->torna_ptr);
      if (tm && peek32(tm + 0x8) == 1)
        torna = 1;
    }
    u64 manager = pointer(active_profile->map_ptr);
    if (manager && peek(manager + active_profile->map_off, &map, sizeof map)) {
      u64 zones = pointer(active_profile->zones_ptr);
      u64 zone = zones ? pointer(zones + 0x20) : 0;
      u64 row = zone ? pointer(zone + 0x30) : 0;
      if (row)
        location = peek16(row);
      if (manager != pointer(active_profile->map_ptr) ||
          map != peek32(manager + active_profile->map_off)) {
        map = location = 0;
      } else if (is_title_map(map)) {
        location = 0;
        on_title = 1;
      } else if (map > 1) {
        in_world = 1;
      }
    }
  }
  if (current_map != map || current_location != location ||
      current_torna != torna) {
    char line[160];
    snprintf(line, sizeof line, "live location: map %u, name %u%s", map,
             location, torna ? " (Torna)" : "");
    host->log(line);
  }
  current_map = map;
  current_location = location;
  current_in_world = in_world;
  current_on_title = on_title;
  current_torna = torna;
}

static void clear_party(void) {
  party_level = 0;
  party_driver_name[0] = party_blade_name[0] = 0;
}

static void track_live_party(void) {
  static unsigned last_log_drv, last_log_bl, last_log_lvl;
  static int last_log_torna = -1;
  static char last_log_bname[96];

  if (!active_profile || !live_gd_ok)
    return;
  if (!current_in_world) {
    clear_party();
    return;
  }
  u64 mgr = pointer(active_profile->gd_mgr_ptr);
  if (!mgr) {
    clear_party();
    return;
  }

  unsigned h_drv = 0, h_bl = 0;
  if (live_hud_ok) {
    u64 hud = pointer(active_profile->hud_ptr);
    if (hud) {
      h_drv = peek32(hud + HUD_DRV_OFF);
      h_bl = peek32(hud + HUD_BLADE_OFF);
    }
  }

  unsigned drv_id = 0, bl_id = 0, lvl = 0;
  const char *who = NULL, *with = NULL;

  if (current_torna) {
    u64 ira = mgr + IRA_BASE_OFF;
    unsigned leader = peek32(ira + IRA_PARTY_LEADER);
    unsigned van = 0, rear = 0;
    for (unsigned t = 0; t < 3; t++) {
      unsigned v = peek16(ira + IRA_VANGUARDS + t * IRA_TEAM_STRIDE);
      unsigned r0 = peek16(ira + IRA_REARGUARDS + t * IRA_TEAM_STRIDE);
      unsigned r1 = peek16(ira + IRA_REARGUARDS + t * IRA_TEAM_STRIDE + 2);
      if (v >= 17 && v <= 25 && (v == leader || !van)) {
        van = v;
        rear = (r0 >= 17 && r0 <= 25) ? r0 : ((r1 >= 17 && r1 <= 25) ? r1 : 0);
        if (v == leader)
          break;
      }
    }
    if (h_drv >= 17 && h_drv <= 25)
      van = h_drv;
    if (h_bl >= BLADE_TORNA_FIRST && h_bl <= BLADE_TORNA_LAST) {
      unsigned hb_drv = h_bl - BLADE_TORNA_TO_DRV;
      if (hb_drv != van)
        rear = hb_drv;
    } else if (h_bl >= 17 && h_bl <= 25 && h_bl != van) {
      rear = h_bl;
    }
    if (van >= 17 && van <= 25) {
      drv_id = van;
      bl_id = rear;
      who = name_of('D', van);
      with = (rear >= 17 && rear <= 25 && rear != van) ? name_of('D', rear) : NULL;
      u64 idrv = ira + (u64)(van - 17) * IRA_DRIVER_SIZE;
      if (peek16(idrv + DRIVER_ID) == van) {
        unsigned lv = peek16(idrv + DRIVER_LEVEL);
        if (lv >= 1 && lv <= 99)
          lvl = lv;
      }
    }
  } else {
    u64 gd = mgr + GD_BASE_OFF;
    unsigned leader = peek32(gd + GD_PARTY_LEADER);
    if (leader >= 1 && leader <= 16) {
      u64 drv = gd + GD_DRIVERS + (u64)(leader - 1) * DRIVER_SIZE;
      if (peek16(drv + DRIVER_ID) == leader) {
        drv_id = leader;
        unsigned lv = peek16(drv + DRIVER_LEVEL);
        if (lv >= 1 && lv <= 99)
          lvl = lv;
        int set_blade = (int)(i16)peek16(drv + DRIVER_SET_BLADE);
        unsigned gd_blade =
            (set_blade >= 0 && set_blade < 4)
                ? peek16(drv + DRIVER_BLADES + 2u * (unsigned)set_blade)
                : 0;
        /* Prefer live HUD active Blade when synced with current Driver so
           real-time ZL+D-Pad / Pyra<->Mythra / Pneuma switches reflect instantly. */
        if (h_drv == leader && h_bl >= 1001 && h_bl <= 1422) {
          bl_id = h_bl;
        } else {
          bl_id = gd_blade;
          if (bl_id == BLADE_PYRA && set_blade == 0 &&
              (peek8(gd + GD_HIKARI) & 1u))
            bl_id = BLADE_MYTHRA;
        }
        who = name_of('D', leader);
        with = resolve_blade_name(gd, bl_id);
      }
    }
  }

  party_level = lvl;
  snprintf(party_driver_name, sizeof party_driver_name, "%s", who ? who : "");
  snprintf(party_blade_name, sizeof party_blade_name, "%s", with ? with : "");

  if (drv_id != last_log_drv || bl_id != last_log_bl || lvl != last_log_lvl ||
      current_torna != last_log_torna ||
      strcmp(party_blade_name, last_log_bname)) {
    char line[220];
    snprintf(line, sizeof line,
             "live party%s: driver %u (%s), blade %u (%s), lv %u (hud %u/%u)",
             current_torna ? " [Torna]" : "", drv_id,
             party_driver_name[0] ? party_driver_name : "-", bl_id,
             party_blade_name[0] ? party_blade_name : "-", lvl, h_drv, h_bl);
    host->log(line);
    last_log_drv = drv_id;
    last_log_bl = bl_id;
    last_log_lvl = lvl;
    last_log_torna = current_torna;
    strcpy(last_log_bname, party_blade_name);
  }
}

/* ---------- Legacy Switch 1 v2.1.0 save-block fallback (NEVER runs on 3.0.1) ---------- */
static int is_save(u64 save) {
  for (unsigned i = 0; i < 16; i++)
    if (peek16(save + SAVE_DRIVERS + i * DRIVER_SIZE + DRIVER_ID) != i + 1)
      return 0;
  return 1;
}
static void scan(void) {
  enum { TAIL = SAVE_DRIVERS + DRIVER_SIZE + DRIVER_ID + 2 };
  static u8 *buffer;
  if (!buffer && !(buffer = malloc(SCAN_CHUNK + TAIL)))
    return;
  candidate_count = 0;
  for (u8 *p = host->memory + AS_BASE;
       p < host->memory + AS_SIZE && candidate_count < 8;) {
    MEMORY_BASIC_INFORMATION m;
    if (!VirtualQuery(p, &m, sizeof m))
      break;
    u8 *end = (u8 *)m.BaseAddress + m.RegionSize;
    int usable = m.State == MEM_COMMIT && m.Type == MEM_PRIVATE &&
                 !(m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) &&
                 m.RegionSize >= SAVE_SPAN;
    for (u8 *chunk = usable ? p : end; chunk + SAVE_SPAN <= end;
         chunk += SCAN_CHUNK) {
      u64 guest = (u64)(chunk - host->memory);
      size_t want = (size_t)(end - chunk) < SCAN_CHUNK + TAIL
                        ? (size_t)(end - chunk)
                        : SCAN_CHUNK + TAIL;
      if (!peek(guest, buffer, want))
        continue;
      for (size_t at = 0; at + TAIL <= want && at < SCAN_CHUNK; at += 8) {
        u16 first, second;
        memcpy(&first, buffer + at + SAVE_DRIVERS + DRIVER_ID, 2);
        memcpy(&second, buffer + at + SAVE_DRIVERS + DRIVER_SIZE + DRIVER_ID,
               2);
        if (first == 1 && second == 2 && is_save(guest + at) &&
            candidate_count < 8) {
          candidates[candidate_count] = guest + at;
          candidate_time[candidate_count++] =
              peek32(guest + at + SAVE_PLAY_TIME);
        }
      }
      Sleep(0);
    }
    p = end;
  }
}
static ULONGLONG next_scan;
static void track_legacy_save(void) {
  if (live_gd_ok)
    return;
  if (live && !is_save(live)) {
    host->log("the game's save data moved; looking for it again");
    live = next_scan = 0;
  }
  if (GetTickCount64() < next_scan)
    return;
  next_scan = GetTickCount64() + (live ? 120000 : 30000);
  char line[200];
  u64 before = live;
  ULONGLONG begun = GetTickCount64();
  scan();
  ULONGLONG now = GetTickCount64(), newest = 0;
  int had_known = known_count > 0;
  for (int i = 0; i < candidate_count; i++) {
    int k = 0;
    while (k < known_count && known[k] != candidates[i])
      k++;
    if (k == known_count) {
      if (known_count == 16)
        continue;
      known[known_count++] = candidates[i];
      known_time[k] = candidate_time[i];
      known_written[k] = had_known ? now : 0;
    } else if (known_time[k] != candidate_time[i]) {
      known_time[k] = candidate_time[i];
      known_written[k] = now;
    }
  }
  long long most = -1;
  live = 0;
  for (int i = 0; i < candidate_count; i++)
    for (int k = 0; k < known_count; k++)
      if (known[k] == candidates[i] && known_written[k] > newest) {
        newest = known_written[k];
        live = candidates[i];
      }
  if (!live)
    for (int i = 0; i < candidate_count; i++)
      if (candidate_time[i] > most) {
        most = candidate_time[i];
        live = candidates[i];
      }
  if (live != before || newest)
    trusted = newest != 0;
  if (live == before)
    return;
  snprintf(line, sizeof line,
           "scan: %d save blocks in %llu ms; using %llx: map jump %u, "
           "leader %u, play time %u h",
           candidate_count, (unsigned long long)(GetTickCount64() - begun),
           (unsigned long long)live, live ? peek32(live + SAVE_MAP_JUMP) : 0,
           live ? peek32(live + SAVE_PARTY_LEADER) : 0,
           live ? peek32(live + SAVE_PLAY_TIME) >> 12 : 0);
  host->log(line);
}

static void escape(char *out, size_t cap, const char *s) {
  size_t n = 0;
  for (; *s && n + 7 < cap; s++) {
    unsigned char c = (unsigned char)*s;
    if (c == '"' || c == '\\')
      out[n++] = '\\', out[n++] = (char)c;
    else if (c < 0x20)
      n += (size_t)snprintf(out + n, cap - n, "\\u%04x", c);
    else
      out[n++] = (char)c;
  }
  out[n] = 0;
}

static void format_party_state(char *state, size_t cap, const char *who,
                               const char *with, unsigned level) {
  int pt = is_pt();
  if (!who || !who[0]) {
    state[0] = 0;
    return;
  }
  if (with && with[0] && level)
    snprintf(state, cap, pt ? "%s com %s · Nv. %u" : "%s with %s · Lv. %u",
             who, with, level);
  else if (with && with[0])
    snprintf(state, cap, pt ? "%s com %s" : "%s with %s", who, with);
  else if (level)
    snprintf(state, cap, pt ? "%s · Nv. %u" : "%s · Lv. %u", who, level);
  else
    snprintf(state, cap, "%s", who);
}

static void describe(char *json, size_t cap) {
  char details[192] = "", state[192] = "", a[400], b[400];
  int pt = is_pt();

  if (current_in_world) {
    const char *region = name_of('M', current_map);
    const char *place = name_of('N', current_location);
    if (region && place) {
      if (!strcmp(region, place))
        snprintf(details, sizeof details, "%s", region);
      else
        snprintf(details, sizeof details, "%s · %s", region, place);
    } else if (region) {
      snprintf(details, sizeof details, "%s", region);
    } else if (place) {
      snprintf(details, sizeof details, "%s", place);
    }

    if (live_gd_ok) {
      format_party_state(state, sizeof state, party_driver_name,
                         party_blade_name, party_level);
    } else {
      /* Legacy Switch 1 fallback */
      if (live && !trusted && region) {
        const char *saved = name_of('R', peek32(live + SAVE_MAP_JUMP));
        if (saved && !strcmp(saved, region)) {
          trusted = 1;
          host->log("save block matches the live region; showing its party");
        }
      }
      if (live && trusted) {
        unsigned leader = peek32(live + SAVE_PARTY_LEADER);
        if (leader >= 1 && leader <= 16) {
          u64 driver = live + SAVE_DRIVERS + (leader - 1) * DRIVER_SIZE;
          int set = (int)(i16)peek16(driver + DRIVER_SET_BLADE);
          unsigned blade =
              (set >= 0 && set < 4)
                  ? peek16(driver + DRIVER_BLADES + 2u * (unsigned)set)
                  : 0;
          if (blade == BLADE_PYRA && set == 0 &&
              (peek8(live + SAVE_HIKARI) & 1u))
            blade = BLADE_MYTHRA;
          const char *who = name_of('D', leader);
          const char *with = resolve_blade_name(live + 0x10, blade);
          unsigned level = peek16(driver + DRIVER_LEVEL);
          format_party_state(state, sizeof state, who, with, level);
        }
      }
    }
  } else if (current_on_title) {
    int mode = lang_mode();
    if (current_torna)
      snprintf(details, sizeof details, "%s",
               mode == 2 ? "Ira · Na Tela de Título"
                         : (mode == 1 ? "Torna · Na Tela de Título"
                                      : "Torna · On the Title Screen"));
    else
      snprintf(details, sizeof details, "%s",
               pt ? "Na Tela de Título" : "On the Title Screen");
  }

  int n = snprintf(json, cap, "{\"timestamps\":{\"start\":%lld}", started);
  if (strlen(details) >= 2) {
    escape(a, sizeof a, details);
    n += snprintf(json + n, cap - (size_t)n, ",\"details\":\"%s\"", a);
  }
  if (strlen(state) >= 2) {
    escape(b, sizeof b, state);
    n += snprintf(json + n, cap - (size_t)n, ",\"state\":\"%s\"", b);
  }
  snprintf(json + n, cap - (size_t)n, "}");
}

static int send_frame(unsigned op, const char *json, unsigned length) {
  char frame[2048];
  DWORD written;
  if (length > sizeof frame - 8)
    return 0;
  memcpy(frame, &op, 4);
  memcpy(frame + 4, &length, 4);
  memcpy(frame + 8, json, length);
  return WriteFile(pipe, frame, 8 + length, &written, NULL) &&
         written == 8 + length;
}
static int read_frame(char *body, unsigned cap, unsigned *length) {
  unsigned head[2], kept = 0;
  DWORD got;
  if (!ReadFile(pipe, head, 8, &got, NULL) || got != 8)
    return -1;
  for (unsigned left = head[1]; left; left -= got) {
    char chunk[512];
    if (!ReadFile(pipe, chunk, left < sizeof chunk ? left : sizeof chunk, &got,
                  NULL) ||
        !got)
      return -1;
    unsigned copy = got < cap - 1 - kept ? got : cap - 1 - kept;
    memcpy(body + kept, chunk, copy);
    kept += copy;
  }
  body[kept] = 0;
  *length = kept;
  return (int)head[0];
}
static void disconnect(void) {
  if (pipe != INVALID_HANDLE_VALUE)
    CloseHandle(pipe);
  pipe = INVALID_HANDLE_VALUE;
}
static int connect_discord(int *rejected) {
  char json[96], reply[512];
  unsigned length;
  for (int i = 0; i < 10 && pipe == INVALID_HANDLE_VALUE; i++) {
    char name[32];
    snprintf(name, sizeof name, "\\\\.\\pipe\\discord-ipc-%d", i);
    pipe = CreateFileA(name, GENERIC_READ | GENERIC_WRITE, 0, NULL,
                       OPEN_EXISTING, 0, NULL);
  }
  if (pipe == INVALID_HANDLE_VALUE)
    return 0;
  int n = snprintf(json, sizeof json, "{\"v\":1,\"client_id\":\"%s\"}",
                   client_id);
  int op = send_frame(OP_HANDSHAKE, json, (unsigned)n)
               ? read_frame(reply, sizeof reply, &length)
               : -1;
  if (op == OP_FRAME && strstr(reply, "\"READY\""))
    return 1;
  if (op == OP_CLOSE) {
    char line[600];
    snprintf(line, sizeof line, "Discord refused the application id: %s",
             reply);
    host->log(line);
    *rejected = 1;
  }
  disconnect();
  return 0;
}
static int send_activity(const char *activity) {
  char json[1400];
  int n = snprintf(json, sizeof json,
                   "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":%lu,"
                   "\"activity\":%s},\"nonce\":\"%u\"}",
                   GetCurrentProcessId(), activity, ++nonce);
  return n > 0 && n < (int)sizeof json &&
         send_frame(OP_FRAME, json, (unsigned)n);
}
static int drain(int *answered) {
  DWORD waiting;
  while (PeekNamedPipe(pipe, NULL, 0, NULL, &waiting, NULL)) {
    char body[1024];
    unsigned length;
    if (!waiting)
      return 1;
    int op = read_frame(body, sizeof body, &length);
    if (op == OP_PING && send_frame(OP_PONG, body, length))
      continue;
    if (op != OP_FRAME)
      return 0;
    int refused = strstr(body, "\"ERROR\"") != NULL;
    if ((!*answered || refused) && strstr(body, "\"SET_ACTIVITY\"")) {
      char line[1100];
      snprintf(line, sizeof line, "Discord %s",
               refused ? body : "shows the presence");
      host->log(line);
      *answered = 1;
    }
  }
  return 0;
}
static DWORD WINAPI worker(void *unused) {
  (void)unused;
  enum { MIN_GAP = 4000 };
  char sent[1100] = "", want[1100], last_logged[1100] = "";
  int rejected = 0, logged = 0, answered = 0;
  ULONGLONG retry = 0, last_send = 0, next_legacy = 0;
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_LOWEST);
  while (!rejected) {
    if (!InterlockedCompareExchange(&enabled, 0, 0)) {
      disconnect();
      sent[0] = 0;
    } else {
      /* Live location & party tracked every 1s with ~10 pointer reads (<0.05 ms). */
      track_location();
      if (name_count) {
        if (live_gd_ok) {
          track_live_party();
        } else if (GetTickCount64() >= next_legacy) {
          next_legacy = GetTickCount64() + 5000;
          track_legacy_save();
        }
      }
      describe(want, sizeof want);
      if (strcmp(want, last_logged)) {
        char line[1200];
        snprintf(line, sizeof line, "presence: %s", want);
        host->log(line);
        strcpy(last_logged, want);
      }
      if (pipe == INVALID_HANDLE_VALUE) {
        if (GetTickCount64() >= retry) {
          retry = GetTickCount64() + 30000;
          if (connect_discord(&rejected)) {
            host->log("Discord presence connected");
            logged = 1;
            sent[0] = 0;
          }
        }
      } else if (!drain(&answered)) {
        if (logged)
          host->log("Discord closed the presence pipe; retrying every 30 s");
        logged = 0;
        disconnect();
        retry = GetTickCount64() + 30000;
      } else if (strcmp(want, sent) &&
                 (!sent[0] || GetTickCount64() >= last_send + MIN_GAP)) {
        if (!send_activity(want)) {
          disconnect();
          continue;
        }
        if (!sent[0])
          answered = 0;
        strcpy(sent, want);
        last_send = GetTickCount64();
      }
    }
    Sleep(1000);
  }
  return 0;
}
static void refresh_settings(void) {
  char value[32] = "";
  host->read_setting("discord_presence", value, sizeof value);
  InterlockedExchange(&enabled, strcmp(value, "0") != 0);
  value[0] = 0;
  host->read_setting("resource_pack", value, sizeof value);
  int pt = !strcmp(value, "pt-br-jp") ? 2 : (!strcmp(value, "pt-br") ? 1 : 0);
  if (!pt && !value[0]) {
    char lang[16] = "";
    host->read_setting("language", lang, sizeof lang);
    pt = (!strcmp(lang, "17") || !strcmp(lang, "9")) ? 1 : 0;
  }
  InterlockedExchange(&portuguese, pt);
}
static void poll(void) {
  static unsigned frames;
  if (!started || frames++ % 60)
    return;
  refresh_settings();
}
__declspec(dllexport) const SnxNativeUiExtension *
snx_native_ui_open(const SnxNativeUiHost *api);
__declspec(dllexport) const SnxNativeUiExtension *
snx_native_ui_open(const SnxNativeUiHost *api) {
  static const SnxNativeUiExtension extension = {SNX_NATIVE_UI_ABI,
                                                 sizeof extension, poll};
  if (!api || api->abi != SNX_NATIVE_UI_ABI || api->size != sizeof *api)
    return NULL;
  host = api;
  host->read_setting("discord_client_id", client_id, sizeof client_id);
  if (!client_id[0])
    snprintf(client_id, sizeof client_id, "%s", default_client_id);
  size_t digits = strspn(client_id, "0123456789");
  if (client_id[digits] || digits > 20) {
    host->log("discord_client_id= is not an application id; presence off");
    return &extension;
  }
  refresh_settings();
  load_names();
  active_profile = NULL;
  live_gd_ok = live_torna_ok = live_homuri_ok = live_hud_ok = 0;
  for (size_t i = 0; i < sizeof profiles / sizeof profiles[0]; i++) {
    if (signature(profiles[i].loc_fn) == profiles[i].loc_sig &&
        signature(profiles[i].map_fn) == profiles[i].map_sig) {
      active_profile = &profiles[i];
      if (profiles[i].gd_fn &&
          signature(profiles[i].gd_fn) == profiles[i].gd_sig &&
          signature(profiles[i].ira_fn) == profiles[i].ira_sig)
        live_gd_ok = 1;
      if (profiles[i].torna_fn &&
          signature(profiles[i].torna_fn) == profiles[i].torna_sig)
        live_torna_ok = 1;
      if (profiles[i].homuri_fn &&
          signature(profiles[i].homuri_fn) == profiles[i].homuri_sig)
        live_homuri_ok = 1;
      if (profiles[i].hud_fn &&
          signature(profiles[i].hud_fn) == profiles[i].hud_sig)
        live_hud_ok = 1;
      break;
    }
  }
  if (active_profile) {
    char msg[200];
    snprintf(msg, sizeof msg,
             "live bindings verified (%s; live_gd=%d torna=%d homuri=%d hud=%d)",
             active_profile->label, live_gd_ok, live_torna_ok, live_homuri_ok,
             live_hud_ok);
    host->log(msg);
  } else {
    host->log("unknown game code; live location unavailable");
  }
  started = (long long)time(NULL);
  HANDLE thread = CreateThread(NULL, 256 * 1024, worker, NULL, 0, NULL);
  if (!thread) {
    host->log("presence worker thread failed; presence off");
    return &extension;
  }
  CloseHandle(thread);
  host->log("Discord presence ready");
  return &extension;
}
