/* =========================================================================
   Xenoblade Chronicles 2: PC Edition Launcher — Frontend Controller
   - Communicates with .NET 10 WinHost via chrome.webview.postMessage
   - Sample-exact Web Audio BGM loop engine (from romfs/stream/bgm/*.nop)
   - Silent, calm UI interactions (no intrusive generic click/selection beeps)
   - Full Keyboard + Gamepad (Xbox / DualSense / Switch Pro) navigation
   ========================================================================= */

const state = {
  screen: 'home',
  subtab: 'video',
  logTab: 'gameLog',
  config: null,
  install: null,
  hardware: null,
  recommendation: null,
  presets: [],
  launchPlan: null,
  bgmTracks: [],
  saves: null,
  mods: null,
  ini: {},
  steam: [],
  setup: { running: false, preflight: null },
  setupProgress: null,
  playing: { running: false },
  logs: {},
  focusedEl: null,
  gamepadMode: false,
  playingTimerInterval: null,
  playingStartMs: 0,
  onboarding: {
    open: false,
    step: 0,
    forceNupView: false,
    simulateMissing: false,
    autoOpenedOnce: false,
    padTestLock: false
  },
  tour: {
    open: false,
    step: 0
  }
};

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
}[c]));

function fmtBytes(b) {
  if (!b || b <= 0) return '0 B';
  if (b >= 1024 ** 3) return (b / 1024 ** 3).toFixed(2) + ' GiB';
  if (b >= 1024 ** 2) return (b / 1024 ** 2).toFixed(1) + ' MiB';
  if (b >= 1024) return (b / 1024).toFixed(0) + ' KiB';
  return b + ' B';
}

function fmtGold(g) {
  return Number(g || 0).toLocaleString('pt-BR') + ' G';
}

function post(obj) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify(obj));
  }
}

function cmd(c, extra = {}) {
  post({ t: 'cmd', c, ...extra });
}

// ==================== Window Scaling (1280x800 composed canvas) ====================
let winScale = 1;
function fitWindow() {
  const win = $('win');
  const sx = window.innerWidth / 1280;
  const sy = window.innerHeight / 800;
  winScale = Math.min(sx, sy);
  win.style.transform = `scale(${winScale})`;
}
window.addEventListener('resize', fitWindow);
fitWindow();

// ==================== Delicate Alrest Ether Motes ====================
(function initEtherParticles() {
  const cv = $('etherCanvas');
  const ctx = cv.getContext('2d');
  const motes = Array.from({ length: 22 }, () => ({
    x: Math.random() * 1280,
    y: Math.random() * 800,
    r: 0.9 + Math.random() * 1.9,
    vy: 0.12 + Math.random() * 0.22,
    vx: (Math.random() - 0.5) * 0.14,
    phase: Math.random() * Math.PI * 2
  }));

  function tick() {
    ctx.clearRect(0, 0, 1280, 800);
    const rgb = getComputedStyle(document.documentElement).getPropertyValue('--acc-rgb').trim() || '143, 233, 255';
    for (const m of motes) {
      m.y -= m.vy;
      m.x += m.vx + Math.sin(m.phase) * 0.10;
      m.phase += 0.012;
      if (m.y < -10) { m.y = 810; m.x = Math.random() * 1280; }
      if (m.x < -10) m.x = 1290;
      if (m.x > 1290) m.x = -10;
      const alpha = 0.10 + 0.18 * (0.5 + 0.5 * Math.sin(m.phase));
      ctx.beginPath();
      ctx.arc(m.x, m.y, m.r, 0, Math.PI * 2);
      ctx.fillStyle = `rgba(${rgb}, ${alpha.toFixed(3)})`;
      ctx.fill();
    }
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
})();

// ==================== Web Audio Engine (BGM + Subtle Native XC2 UI SFX) ====================
const AudioEngine = (() => {
  let ctx = null;
  let bgmGain = null;
  let bgmSource = null;
  let currentTrackId = null;
  let bgmLoading = false;
  let sfxWarmed = false;
  const bgmCache = new Map();
  const sfxCache = new Map();
  const sfxInFlight = new Map();
  const sfxMissing = new Set();
  const lastSfxTs = new Map();

  function ensureCtx() {
    if (!ctx) {
      ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
      bgmGain = ctx.createGain();
      bgmGain.gain.value = 0;
      bgmGain.connect(ctx.destination);
    }
    if (ctx.state === 'suspended') ctx.resume().catch(() => {});
    return ctx;
  }

  function levelGain(level) {
    switch (level) {
      case 'off': return 0;
      case 'low': return 0.22;
      case 'med': return 0.46;
      case 'high': return 0.78;
      default: return 0.22;
    }
  }

  function loadSfx(id) {
    if (sfxCache.has(id)) return Promise.resolve(sfxCache.get(id));
    if (sfxMissing.has(id)) return Promise.resolve(null);
    if (sfxInFlight.has(id)) return sfxInFlight.get(id);

    const p = (async () => {
      try {
        const res = await fetch(`/media/sfx/${id}.wav`);
        if (!res.ok) { sfxMissing.add(id); return null; }
        const arr = await res.arrayBuffer();
        const ac = ensureCtx();
        const buf = await ac.decodeAudioData(arr);
        sfxCache.set(id, buf);
        return buf;
      } catch {
        return null;
      } finally {
        sfxInFlight.delete(id);
      }
    })();
    sfxInFlight.set(id, p);
    return p;
  }

  function playSfx(id, gainScale = 1.0) {
    if (!id || !state.config || state.config.soundLevel === 'off' || state.playing.running) return;
    if (!state.install?.hasRomfs) return;
    const nowMs = performance.now();
    const minGap = id === 'cursor' ? 45 : 70;
    if (nowMs - (lastSfxTs.get(id) || 0) < minGap) return;
    lastSfxTs.set(id, nowMs);

    const vol = levelGain(state.config.soundLevel) * 0.40 * gainScale;
    if (vol <= 0.001) return;

    const ac = ensureCtx();
    const cached = sfxCache.get(id);
    if (cached) {
      playBuffer(ac, cached, vol);
      return;
    }
    loadSfx(id).then(buf => {
      if (buf && state.config?.soundLevel !== 'off' && !state.playing.running) {
        playBuffer(ac, buf, vol);
      }
    });
  }

  function playBuffer(ac, buf, vol) {
    try {
      const src = ac.createBufferSource();
      const g = ac.createGain();
      g.gain.value = vol;
      src.buffer = buf;
      src.connect(g);
      g.connect(ac.destination);
      src.start(0);
    } catch {}
  }

  function warmupSfx() {
    if (sfxWarmed || !state.install?.hasRomfs) return;
    sfxWarmed = true;
    for (const id of ['cursor', 'decide', 'open', 'cancel', 'play', 'error']) {
      loadSfx(id);
    }
  }

  function resetMediaCache() {
    sfxMissing.clear();
    sfxWarmed = false;
  }

  function stopBgm(fadeSec = 0.45) {
    if (!bgmSource || !ctx || !bgmGain) return;
    const oldSrc = bgmSource;
    bgmSource = null;
    try {
      const now = ctx.currentTime;
      bgmGain.gain.cancelScheduledValues(now);
      bgmGain.gain.setValueAtTime(bgmGain.gain.value, now);
      bgmGain.gain.linearRampToValueAtTime(0.0001, now + fadeSec);
      setTimeout(() => { try { oldSrc.stop(); } catch {} }, fadeSec * 1000 + 60);
    } catch { try { oldSrc.stop(); } catch {} }
  }

  async function syncBgm() {
    if (!state.config) return;
    const wantPlay = state.config.musicEnabled && state.config.soundLevel !== 'off' && !state.playing.running;
    const trackId = state.config.musicTrack || 'm01';
    const targetVol = levelGain(state.config.soundLevel) * 0.52;

    if (!wantPlay) {
      stopBgm(0.45);
      currentTrackId = null;
      return;
    }

    if (bgmSource && currentTrackId === trackId && ctx && bgmGain) {
      const now = ctx.currentTime;
      bgmGain.gain.cancelScheduledValues(now);
      bgmGain.gain.setValueAtTime(bgmGain.gain.value, now);
      bgmGain.gain.linearRampToValueAtTime(targetVol, now + 0.35);
      return;
    }

    if (bgmLoading) return;
    bgmLoading = true;
    try {
      const ac = ensureCtx();
      let buf = bgmCache.get(trackId);
      if (!buf) {
        const res = await fetch(`/media/bgm/${trackId}.ogg`);
        if (!res.ok) { bgmLoading = false; return; }
        const arr = await res.arrayBuffer();
        buf = await ac.decodeAudioData(arr);
        bgmCache.set(trackId, buf);
      }

      if (!state.config.musicEnabled || state.config.soundLevel === 'off' || state.playing.running) {
        bgmLoading = false;
        return;
      }

      stopBgm(0.25);
      const src = ac.createBufferSource();
      src.buffer = buf;
      src.loop = true;

      const meta = state.bgmTracks.find(t => t.id === trackId);
      if (meta && meta.loopEndSec > meta.loopStartSec && meta.loopEndSec <= buf.duration + 0.1) {
        src.loopStart = meta.loopStartSec;
        src.loopEnd = Math.min(meta.loopEndSec, buf.duration);
      }

      src.connect(bgmGain);
      const now = ac.currentTime;
      bgmGain.gain.cancelScheduledValues(now);
      bgmGain.gain.setValueAtTime(0.0001, now);
      bgmGain.gain.linearRampToValueAtTime(targetVol, now + 0.9);
      src.start(0);
      bgmSource = src;
      currentTrackId = trackId;
    } catch {}
    bgmLoading = false;
  }

  return { syncBgm, stopBgm, playSfx, warmupSfx, resetMediaCache };
})();

let lastMediaRomfs = false;
function refreshRuntimeMedia() {
  const v = Date.now();
  AudioEngine.resetMediaCache();

  // Refresh CSS background layers so freshly extracted art appears without restarting the launcher
  let st = $('mediaBustStyle');
  if (!st) {
    st = document.createElement('style');
    st.id = 'mediaBustStyle';
    document.head.appendChild(st);
  }
  st.textContent = `
    .backdrop-img.home-art, .backdrop-img.playing-art, .backdrop-img.ob-art {
      background-image: url('/media/art/keyart_bg.png?v=${v}') !important;
    }
    html[data-theme="torna"] .backdrop-img.home-art, html[data-theme="torna"] .backdrop-img.ob-art {
      background-image: url('/media/art/torna_bg.png?v=${v}'), url('/media/art/keyart_bg.png?v=${v}') !important;
    }
    .backdrop-img.menu-art {
      background-image: url('/media/art/menu_bg.png?v=${v}') !important;
    }
  `;

  document.querySelectorAll('img').forEach(img => {
    const raw = img.getAttribute('src') || '';
    if (!raw.startsWith('/media/art/') && !raw.startsWith('/media/icon/')) return;
    const base = raw.split('?')[0];
    img.style.display = '';
    img.parentElement?.classList.remove('logo-missing', 'crystal-fallback');
    img.setAttribute('src', `${base}?v=${v}`);
  });

  AudioEngine.warmupSfx();
  AudioEngine.syncBgm();
}

window.addEventListener('pointerdown', () => AudioEngine.warmupSfx(), { once: true, passive: true });
window.addEventListener('keydown', () => AudioEngine.warmupSfx(), { once: true, passive: true });

// ==================== Toast ====================
let toastTimer = null;
function showToast(text, isError = false, sfx = null) {
  const el = $('toast');
  el.textContent = text;
  el.classList.toggle('err', !!isError);
  el.classList.add('on');
  if (isError) AudioEngine.playSfx('error', 0.85);
  else if (sfx) AudioEngine.playSfx(sfx, 0.75);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('on'), 3800);
}

// ==================== Helpers for Clean Player-Facing Copy ====================
function cleanSaveLabel(raw, info) {
  if (!raw) return info?.landmarkName || 'Save';
  if (/Save de Produ[çc][ãa]o/i.test(raw)) return 'Save Principal';
  if (/Save 100%.*Cofre/i.test(raw)) return 'Save 100% · Teatro Olethro';
  if (/Progresso Inicial/i.test(raw)) return 'Progresso Inicial';
  const m = raw.match(/Salvo anterior\s*\(([^)]+)\)/i);
  if (m) {
    const dt = m[1].match(/(\d{4})(\d{2})(\d{2})/);
    return dt ? `Backup anterior (${dt[3]}/${dt[2]}/${dt[1]})` : 'Backup anterior';
  }
  return raw;
}

function detectEmulatorName(dirPath) {
  const low = String(dirPath || '').toLowerCase();
  if (low.includes('ryujinx')) return 'Ryujinx';
  if (low.includes('eden')) return 'Eden';
  if (low.includes('sudachi')) return 'Sudachi';
  if (low.includes('citron')) return 'Citron';
  if (low.includes('yuzu')) return 'Yuzu';
  return 'Emulador';
}

function closePresetMenu() {
  $('presetPicker')?.classList.remove('open');
}

function closeHelpMenu() {
  $('helpAnchor')?.classList.remove('open');
}

// ==================== Custom XC2 Dropdowns (replaces native OS select) ====================
const XCSelect = (() => {
  const map = new Map();
  let activeSelect = null;

  function syncButton(sel) {
    const btn = map.get(sel);
    if (!btn) return;
    const opt = sel.options[sel.selectedIndex];
    btn.querySelector('.xc-select-text').textContent = opt ? opt.textContent : '';
  }

  function close() {
    const portal = $('xcSelectPortal');
    if (portal) portal.classList.remove('open', 'up');
    if (activeSelect) {
      map.get(activeSelect)?.classList.remove('open');
      activeSelect = null;
    }
  }

  function open(sel) {
    const portal = $('xcSelectPortal');
    const btn = map.get(sel);
    if (!portal || !btn) return;

    if (activeSelect === sel) { close(); return; }
    close();
    closePresetMenu();
    activeSelect = sel;
    btn.classList.add('open');

    portal.innerHTML = [...sel.options].map((o, idx) => `
      <button class="xc-dd-opt ${o.value === sel.value ? 'on' : ''}" data-idx="${idx}" type="button">
        <span class="xc-dd-gem"></span>
        <span>${esc(o.textContent)}</span>
      </button>
    `).join('');

    portal.querySelectorAll('.xc-dd-opt').forEach(item => {
      item.addEventListener('click', (e) => {
        e.stopPropagation();
        const opt = sel.options[Number(item.dataset.idx)];
        if (opt) {
          sel.value = opt.value;
          syncButton(sel);
          sel.dispatchEvent(new Event('change', { bubbles: true }));
        }
        close();
      });
    });

    // Compute coordinates inside the 1280x800 .win coordinate space
    const winRect = $('win').getBoundingClientRect();
    const r = btn.getBoundingClientRect();
    const k = winScale || 1;
    const left = (r.left - winRect.left) / k;
    const top = (r.top - winRect.top) / k;
    const width = r.width / k;
    const height = r.height / k;

    portal.style.width = `${Math.max(width, 220)}px`;
    portal.style.left = `${Math.min(left, 1280 - Math.max(width, 220) - 20)}px`;

    // Measure actual menu height to decide smart upward vs downward placement
    const itemCount = sel.options.length;
    const estHeight = Math.min(290, itemCount * 38 + 16);
    const spaceBelow = 800 - (top + height) - 18;
    const spaceAbove = top - 72;

    if (spaceBelow < estHeight && spaceAbove > spaceBelow) {
      portal.classList.add('up');
      portal.style.top = 'auto';
      portal.style.bottom = `${800 - top + 6}px`;
    } else {
      portal.classList.remove('up');
      portal.style.bottom = 'auto';
      portal.style.top = `${top + height + 6}px`;
    }

    portal.classList.add('open');
    const selectedItem = portal.querySelector('.xc-dd-opt.on');
    if (selectedItem) selectedItem.scrollIntoView({ block: 'nearest' });
  }

  function enhanceAll() {
    document.querySelectorAll('.opt-ctrl select').forEach(sel => {
      if (map.has(sel)) { syncButton(sel); return; }
      sel.classList.add('xc-native-select');
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'xc-select-btn';
      btn.setAttribute('data-f', '');
      btn.innerHTML = `
        <span class="xc-select-text"></span>
        <svg class="xc-select-icon" viewBox="0 0 12 12" aria-hidden="true">
          <path d="M2.6 4.4L6 7.8L9.4 4.4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
        </svg>
      `;
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        open(sel);
      });
      sel.insertAdjacentElement('afterend', btn);
      map.set(sel, btn);
      syncButton(sel);
    });
  }

  function syncAll() {
    map.forEach((_, sel) => syncButton(sel));
  }

  return { enhanceAll, syncAll, syncButton, open, close, isOpen: () => !!activeSelect };
})();

XCSelect.enhanceAll();

// ==================== Navigation ====================
function goScreen(name) {
  if (name === 'system') {
    goScreen('settings');
    goSettingsSubtab('system');
    return;
  }
  if (state.playing.running) {
    if (name === 'home') {
      name = 'playing';
    } else if (name === 'saves' || name === 'mods') {
      showToast('Feche o jogo para alterar saves ou mods. Configurações podem ser ajustadas ao vivo!');
      return;
    } else if (name !== 'playing' && name !== 'settings') {
      name = 'playing';
    }
  }
  const changed = state.screen !== name;
  state.screen = name;
  closePresetMenu();
  closeHelpMenu();
  XCSelect.close();
  if (changed && name !== 'playing') AudioEngine.playSfx('open', 0.72);

  document.querySelectorAll('.screen').forEach(s => s.classList.toggle('active', s.dataset.screen === name));
  document.querySelectorAll('.nav-tab').forEach(t => t.classList.toggle('on', t.dataset.nav === (name === 'playing' ? 'home' : name)));
  $('topbar')?.classList.toggle('solid', name !== 'home');

  if (name === 'saves') cmd('saves.list');
  if (name === 'mods') cmd('mods.list');
  if (name === 'settings') {
    cmd('ini.refresh');
    if (state.subtab === 'steam') cmd('steam.list');
    if (state.subtab === 'system') cmd('system.logs');
  }
  clearFocus();
}

function goSettingsSubtab(stab) {
  const changed = state.subtab !== stab;
  state.subtab = stab;
  XCSelect.close();
  if (changed) AudioEngine.playSfx('cursor', 0.75);
  document.querySelectorAll('.stab').forEach(b => b.classList.toggle('on', b.dataset.stab === stab));
  document.querySelectorAll('.spane').forEach(p => p.classList.toggle('on', p.dataset.spane === stab));
  $('settingsFooter')?.classList.toggle('hidden', stab === 'system');
  if (stab === 'steam') cmd('steam.list');
  if (stab === 'system') cmd('system.logs');
}

// Expose for automated visual QA shots (XC2_LAUNCHER_SHOT)
window.__xc2 = {
  go: (s, sub, theme) => {
    closeOnboarding(false);
    closeTour(false);
    if (theme) document.documentElement.dataset.theme = theme;
    goScreen(s);
    if (sub && (s === 'settings' || s === 'system')) goSettingsSubtab(sub);
  },
  theme: (t) => { document.documentElement.dataset.theme = t; },
  openPreset: () => { $('presetPicker')?.classList.add('open'); },
  openSelect: (id) => { const el = $(id); if (el) XCSelect.open(el); },
  openHelp: () => { closeOnboarding(false); closeTour(false); $('helpAnchor')?.classList.add('open'); },
  openOnboarding: (step = 0, opts = {}) => {
    closeTour(false);
    closeHelpMenu();
    state.onboarding.simulateMissing = !!opts.simulateMissing;
    state.onboarding.forceNupView = !!opts.forceNupView;
    openOnboarding(step);
  },
  closeOnboarding: () => closeOnboarding(false),
  startTour: (step = 0) => { closeOnboarding(false); closeHelpMenu(); startTour(step); }
};

// ==================== Render Functions ====================
function renderAll() {
  renderChrome();
  renderHome();
  renderPlaying();
  renderSaves();
  renderMods();
  renderSettings();
  renderSystem();
  if (state.onboarding.open) renderOnboarding();
  if (state.tour.open) positionTourStep();
  AudioEngine.syncBgm();
}

function renderChrome() {
  if (!state.config) return;
  document.documentElement.dataset.theme = state.config.accentTheme || 'aegis';
  const muted = state.config.soundLevel === 'off' || !state.config.musicEnabled || state.playing.running;
  const track = state.bgmTracks.find(t => t.id === (state.config.musicTrack || 'm01'));
  const trackName = track ? track.title : 'Trilha do jogo';

  const btn = $('musicBtn');
  btn.classList.toggle('muted', muted);
  btn.title = muted ? `Música pausada · Clique para tocar (${trackName})` : `${trackName} · Clique para silenciar`;

  const whisper = $('homeTrackWhisper');
  if (whisper) {
    whisper.classList.toggle('muted', muted);
    $('npTrackTitle').textContent = trackName;
  }
}

function renderHome() {
  const save = state.saves?.activeMain;
  const cfg = state.config;
  const plan = state.launchPlan;

  if (save && save.isValid) {
    $('homeRegion').textContent = (save.regionName || 'Alrest').toUpperCase();
    $('homeLandmark').textContent = save.landmarkName;
    const party = save.activeBladeName
      ? `${save.leaderName} & ${save.activeBladeName}`
      : save.leaderName;
    $('homeMeta').textContent = `${party} · Nv. ${save.leaderLevel} · ${save.playTimeFormatted}`;
  } else {
    $('homeRegion').textContent = 'ALREST';
    $('homeLandmark').textContent = 'Nova Aventura';
    $('homeMeta').textContent = 'Pronto para iniciar sua jornada.';
  }

  const playLabel = document.querySelector('#playBtn .play-btn-label');
  if (playLabel && state.install) {
    playLabel.textContent = state.install.isReadyToPlay ? 'Jogar' : 'Instalar (.nup)';
  }

  if (plan) {
    $('playProfileLabel').textContent = state.install && !state.install.isReadyToPlay
      ? 'Aguardando arquivo .nup'
      : plan.profileSummary;
  }

  if (state.presets && cfg) {
    $('presetMenu').innerHTML = state.presets.map(p => `
      <button class="preset-opt ${cfg.preset === p.id ? 'on' : ''}" data-preset="${esc(p.id)}" type="button" title="${esc(p.description)}">
        <span class="preset-opt-main">
          <span class="preset-opt-gem"></span>
          <span>${esc(p.name)}</span>
        </span>
        <small>${esc(p.badge)}</small>
      </button>
    `).join('');

    $('presetMenu').querySelectorAll('.preset-opt').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        closePresetMenu();
        AudioEngine.playSfx('decide', 0.78);
        cmd('preset.set', { id: btn.dataset.preset });
      });
    });
  }
}

function renderPlaying() {
  $('playingProfile').textContent = state.playing.profile || 'Xenoblade Chronicles 2';
  if (state.playing.running) {
    if (!state.playingStartMs) state.playingStartMs = Date.now();
    clearInterval(state.playingTimerInterval);
    state.playingTimerInterval = setInterval(() => {
      const sec = Math.floor((Date.now() - state.playingStartMs) / 1000);
      const h = String(Math.floor(sec / 3600)).padStart(2, '0');
      const m = String(Math.floor((sec % 3600) / 60)).padStart(2, '0');
      const s = String(sec % 60).padStart(2, '0');
      $('playingTimer').textContent = `${h}:${m}:${s}`;
    }, 1000);
  } else {
    clearInterval(state.playingTimerInterval);
    state.playingStartMs = 0;
  }
}

function renderSaves() {
  const s = state.saves;
  if (!s) return;
  const active = s.activeMain;
  const snaps = s.snapshots || [];

  const totalCount = snaps.length + (active && active.isValid && !snaps.some(x => x.isActiveMatch) ? 1 : 0);
  $('savesKicker').textContent = totalCount === 1
    ? '1 arquivo salvo na sua pasta do jogo'
    : `${totalCount} arquivos salvos disponíveis`;

  if (active && active.isValid) {
    const party = active.activeBladeName
      ? `${active.leaderName} & ${active.activeBladeName}`
      : active.leaderName;
    const tornaPart = s.activeTorna ? `<span>Torna <strong>Incluído</strong></span>` : '';

    $('activeSaveCard').innerHTML = `
      <div class="as-head-row">
        <div class="as-head-info">
          <div class="xc-eyebrow">
            <span class="xc-diamond"></span>
            <span>SAVE ATUAL · ${esc((active.regionName || 'Alrest').toUpperCase())}</span>
          </div>
          <div class="as-title">${esc(active.landmarkName)}</div>
          <div class="as-meta">
            <span><strong>${esc(party)}</strong> · Nv. ${active.leaderLevel}</span>
            <span>Tempo <strong>${esc(active.playTimeFormatted)}</strong></span>
            <span>Ouro <strong class="gold-val">${esc(fmtGold(active.gold))}</strong></span>
            ${tornaPart}
          </div>
        </div>
        <img class="as-rex-art" src="/media/art/save_rex.png" alt="" onerror="this.style.display='none'">
      </div>
      <div class="as-backup-row">
        <input type="text" class="text-input" id="backupLabelInput" placeholder="Nome da cópia de segurança (opcional)">
        <button class="xc-btn primary sm" id="createBackupBtn" data-f>Salvar cópia</button>
      </div>
    `;
    $('createBackupBtn').addEventListener('click', () => {
      AudioEngine.playSfx('decide', 0.8);
      cmd('saves.backup', { label: $('backupLabelInput').value.trim() });
    });
  } else {
    $('activeSaveCard').innerHTML = `
      <div class="xc-eyebrow"><span class="xc-diamond"></span><span>SEM SAVE ATIVO</span></div>
      <p class="panel-desc" style="margin-top:8px">Nenhum progresso encontrado ainda. Inicie o jogo ou importe um save ao lado.</p>
    `;
  }

  $('snapshotsCount').textContent = snaps.length === 1 ? '1 cópia' : `${snaps.length} cópias`;
  $('snapshotsList').innerHTML = snaps.length === 0
    ? `<div class="xc-panel"><p class="panel-desc">Nenhuma cópia salva ainda. Use "Salvar cópia" acima para criar um ponto de retorno.</p></div>`
    : snaps.map(sn => {
        const title = cleanSaveLabel(sn.label, sn.info);
        const subParts = [sn.info.landmarkName];
        if (sn.info.regionName && !sn.info.landmarkName.toLowerCase().startsWith(sn.info.regionName.toLowerCase())) {
          subParts.push(sn.info.regionName);
        }
        subParts.push(sn.info.summaryLine);
        return `
          <div class="xc-item ${sn.isActiveMatch ? 'is-active' : ''}">
            <div class="xc-item-body">
              <div class="xc-item-title">${esc(title)}</div>
              <div class="xc-item-sub">${esc(subParts.join(' · '))}</div>
            </div>
            ${sn.isActiveMatch
              ? '<span class="xc-status">Em uso</span>'
              : `<button class="xc-btn sm ghost" data-restore="${esc(sn.filePath)}" data-f>Restaurar</button>`}
          </div>
        `;
      }).join('');

  $('snapshotsList').querySelectorAll('[data-restore]').forEach(btn => {
    btn.addEventListener('click', () => {
      if (btn.dataset.armed !== '1') {
        btn.dataset.armed = '1';
        btn.textContent = 'Confirmar?';
        btn.classList.remove('ghost');
        btn.classList.add('primary');
        setTimeout(() => {
          if (btn.isConnected && btn.dataset.armed === '1') {
            btn.dataset.armed = '';
            btn.textContent = 'Restaurar';
            btn.classList.remove('primary');
            btn.classList.add('ghost');
          }
        }, 4000);
        return;
      }
      cmd('saves.restore', { path: btn.dataset.restore });
    });
  });

  const emus = s.detectedEmulatorDirs || [];
  $('emulatorSavesWrap').innerHTML = emus.length === 0 ? '' :
    emus.map(d => {
      const emuName = detectEmulatorName(d);
      return `<button class="xc-btn sm full" data-import-emu="${esc(d)}" data-f title="${esc(d)}">Importar do ${esc(emuName)}</button>`;
    }).join('');
  $('emulatorSavesWrap').querySelectorAll('[data-import-emu]').forEach(btn => {
    btn.addEventListener('click', () => {
      cmd('saves.restore', { path: btn.dataset.importEmu });
    });
  });
}

function cleanAddonTitle(addon) {
  const tid = String(addon.titleId || '').toLowerCase();
  if (tid.endsWith('9001')) return 'Torna — The Golden Country';
  if (tid.endsWith('9002')) return 'Pacote de Itens Úteis';
  if (tid.endsWith('9003')) return 'Novas Missões & Desafios';
  if (tid.endsWith('9004')) return 'Novas Blades Raras';
  if (tid.endsWith('9063')) return 'Vozes em Japonês';
  return addon.displayName;
}

function addonArtFor(addon) {
  const tid = String(addon.titleId || '').toLowerCase();
  if (tid.endsWith('9001')) return { url: '/media/art/torna_bg.png', cls: 'art-torna' };
  if (tid.endsWith('9002')) return { url: '/media/art/dlc_items.png', cls: 'art-items' };
  if (tid.endsWith('9003')) return { url: '/media/art/dlc_quests.png', cls: 'art-quests' };
  if (tid.endsWith('9004')) return { url: '/media/art/dlc_blades.png', cls: 'art-blades' };
  if (tid.endsWith('9063')) return { url: '/media/art/dlc_crest.png', cls: 'art-crest' };
  return { url: '/media/art/argentum_bg.png', cls: 'art-argentum' };
}

function packArtStyle(pack) {
  if (!pack.id) return { bg: `url('/media/art/keyart_bg.png')`, cls: 'art-keyart' };
  if (/^pt[-_]?br[-_]?jp$/i.test(pack.id)) {
    return { bg: `url('/media/pack-thumb/${encodeURIComponent(pack.id)}.png'), url('/media/art/dlc_blades.png')`, cls: 'art-blades' };
  }
  if (/^pt[-_]?br$/i.test(pack.id)) {
    return { bg: `url('/media/pack-thumb/${encodeURIComponent(pack.id)}.png'), url('/media/art/dlc_battle.png')`, cls: 'art-battle' };
  }
  return { bg: `url('/media/pack-thumb/${encodeURIComponent(pack.id)}.png'), url('/media/art/argentum_bg.png')`, cls: 'art-argentum' };
}

function renderMods() {
  const m = state.mods;
  if (!m) return;

  const items = [
    {
      id: '',
      name: 'Original do Jogo',
      sub: 'Base original',
      active: !m.activeResourcePack
    },
    ...(m.installedPacks || []).map(p => ({
      id: p.id,
      name: p.name,
      sub: `${fmtBytes(p.sizeBytes)}`,
      active: p.isActive
    }))
  ];
  $('activePackStatus').textContent = items.length === 1 ? '1 pacote' : `${items.length} pacotes`;

  $('packsList').innerHTML = items.map(it => {
    const art = packArtStyle(it);
    return `
      <div class="pack-card ${it.active ? 'is-active' : ''}">
        <div class="pack-thumb ${art.cls}" style="background-image:${art.bg}"></div>
        <div class="pack-body">
          <div class="pack-title" title="${esc(it.name)}">${esc(it.name)}</div>
          <div class="pack-foot">
            <div class="pack-sub">${esc(it.sub)}</div>
            ${it.active
              ? '<span class="xc-status">Ativo</span>'
              : `<button class="xc-btn sm ghost" data-pack="${esc(it.id)}" data-f>Ativar</button>`}
          </div>
        </div>
      </div>
    `;
  }).join('');

  $('packsList').querySelectorAll('[data-pack]').forEach(btn => {
    btn.addEventListener('click', () => {
      AudioEngine.playSfx('decide', 0.8);
      cmd('mods.activate', { id: btn.dataset.pack });
    });
  });

  // Hide tiny 576-byte internal license keys so players only see real expansions & voice packs
  const visibleAddons = (m.addons || []).filter(a =>
    a.sizeBytes > 10000 && !/Licen[çc]a Expansion Pass/i.test(a.displayName)
  );
  // Put Torna first as featured full-width card when there is an odd count, keeping 2x2 symmetry below
  const featureFirst = visibleAddons.length % 2 === 1;
  $('addonsCount').textContent = visibleAddons.length > 0 ? `${visibleAddons.length} conteúdos` : '';
  $('addonsGrid').innerHTML = visibleAddons.map((a, idx) => {
    const art = addonArtFor(a);
    const title = cleanAddonTitle(a);
    const isFeatured = featureFirst && idx === 0;
    if (isFeatured) {
      return `
        <div class="dlc-item featured xc-rail-band" style="--feat-art:url('${art.url}')">
          <div class="dlc-feat-art" style="background-image:url('${art.url}')"></div>
          <div class="dlc-content">
            <div class="dlc-item-body">
              <div class="xc-eyebrow"><span class="xc-diamond"></span><span>EXPANSÃO DE HISTÓRIA</span></div>
              <strong title="${esc(title)}">${esc(title)}</strong>
              <span>Campanha completa · 500 anos antes da história principal</span>
            </div>
            <div class="dlc-size">${esc(fmtBytes(a.sizeBytes))}</div>
          </div>
        </div>
      `;
    }
    return `
      <div class="dlc-item">
        <div class="dlc-thumb ${art.cls}" style="background-image:url('${art.url}')"></div>
        <div class="dlc-content">
          <div class="dlc-item-body">
            <strong title="${esc(title)}">${esc(title)}</strong>
            <span>${esc(a.category)}</span>
          </div>
          <div class="dlc-size">${esc(fmtBytes(a.sizeBytes))}</div>
        </div>
      </div>
    `;
  }).join('');
}

function renderSettings() {
  const cfg = state.config;
  const ini = state.ini || {};
  const hw = state.hardware;
  if (!cfg) return;

  $('cfgPreset').innerHTML = (state.presets || []).map(p =>
    `<option value="${esc(p.id)}">${esc(p.name)} — ${esc(p.badge)}</option>`
  ).join('');
  $('cfgPreset').value = cfg.preset;

  setSelect('iniWindowMode', ini['Display.window_mode'] || 'auto');
  setSelect('iniOutputRes', ini['Display.output_resolution'] || 'auto');
  setSelect('iniUpscale', ini['Graphics.upscale'] || 'off');
  setSelect('iniDlssPreset', ini['Graphics.dlss_preset'] || 'auto');
  setSelect('iniResolution', ini['Graphics.resolution'] || '1');
  const jpNames = (ini['Display.resource_pack'] || '').toLowerCase() === 'pt-br-jp';
  const amiiboSel = $('iniAmiibo');
  if (amiiboSel && amiiboSel.options.length >= 3) {
    amiiboSel.options[1].textContent = jpNames ? 'Homura (Novo Estilo + Bônus)' : 'Pyra (Novo Estilo + Bônus)';
    amiiboSel.options[2].textContent = jpNames ? 'Hikari (Novo Estilo + Bônus)' : 'Mythra (Novo Estilo + Bônus)';
  }
  setSelect('iniAmiibo', ini['Input.amiibo'] || '0');
  setSelect('iniAnisotropy', ini['Graphics.anisotropy'] || '0');
  setSelect('iniSharpness', ini['Graphics.upscale_sharpness'] || '0.1');
  setSelect('iniMipBias', ini['Graphics.upscale_mip_bias'] || 'auto');
  setSelect('iniTonemap', ini['Graphics.tonemap'] || 'native');
  setSelect('iniBloomMode', ini['Graphics.bloom_mode'] || 'native');
  setSelect('iniLanguage', ini['Display.language'] || '1');

  $('iniShadows').checked = (ini['Graphics.shadows'] ?? '1') !== '0';
  $('iniVsync').checked = (ini['Graphics.vsync'] ?? '1') !== '0';
  $('iniContactShadows').checked = (ini['Graphics.contact_shadows'] ?? '0') === '1';
  $('iniGtao').checked = (ini['Graphics.gtao'] ?? '0') === '1';
  $('iniEnvShading').checked = (ini['Graphics.env_shading'] ?? '0') === '1';
  $('iniWaterCaustics').checked = (ini['Graphics.water_caustics'] ?? '0') === '1';
  $('iniSsgi').checked = (ini['Graphics.ssgi'] ?? '0') === '1';
  $('iniSsr').checked = (ini['Graphics.ssr'] ?? '0') === '1';
  $('iniShowFps').checked = (ini['Performance.show_fps'] ?? '0') === '1';
  $('iniDocked').checked = (ini['Display.docked'] ?? '1') !== '0';
  $('iniDiscord').checked = (ini['Network.discord_presence'] ?? '1') !== '0';

  const liveFps = ini['Performance.fps_limit'];
  const fpsVal = (liveFps !== undefined && liveFps !== 'game' && /^\d+$/.test(liveFps))
    ? liveFps
    : String(cfg.customXc2Fps ?? 120);
  setSelect('cfgCustomFps', fpsVal);
  $('cfg4kScale').checked = !!cfg.custom4kSelectiveScale;
  $('cfgMinimizeLaunch').checked = !!cfg.minimizeOnLaunch;
  $('cfgCloseLaunch').checked = !!cfg.closeAfterLaunch;
  $('cfgMusicEnabled').checked = !!cfg.musicEnabled;

  if ($('saveSettingsBtn')) {
    $('saveSettingsBtn').textContent = state.playing.running ? 'Aplicar ao vivo no jogo' : 'Salvar alterações';
  }

  if (hw) {
    $('fpsMonitorHint').textContent = hw.monitorRefreshHz >= 100
      ? `Monitor ${hw.screenWidth}×${hw.screenHeight} · ${hw.monitorRefreshHz} Hz (cutscenes em 30 FPS).`
      : `Monitor ${hw.monitorRefreshHz} Hz (cutscenes permanecem em 30 FPS).`;
  }

  const inst = state.install;
  if (inst && $('shaderCacheHint')) {
    const cnt = inst.shaderCatalogCount ? inst.shaderCatalogCount.toLocaleString('pt-BR') : null;
    if (inst.shaderCachePrecompiled && cnt) {
      $('shaderCacheHint').textContent = `Otimizado · ${cnt} shaders pré-compilados para sua placa de vídeo.`;
    } else if (cnt) {
      $('shaderCacheHint').textContent = `Pré-compilação pronta para rodar na próxima abertura (${cnt} shaders).`;
    } else {
      $('shaderCacheHint').textContent = 'Pré-compila os shaders na abertura para gameplay suave sem engasgos.';
    }
  }

  document.querySelectorAll('[data-theme-pick]').forEach(s =>
    s.classList.toggle('on', s.dataset.themePick === (cfg.accentTheme || 'aegis'))
  );
  const soundOrder = ['off', 'low', 'med', 'high'];
  const curSoundIdx = Math.max(0, soundOrder.indexOf(cfg.soundLevel || 'low'));
  document.querySelectorAll('[data-sound]').forEach(s => {
    const idx = soundOrder.indexOf(s.dataset.sound);
    s.classList.toggle('on', idx === curSoundIdx);
    s.classList.toggle('is-lit', idx > 0 && idx <= curSoundIdx);
  });

  const isAudible = cfg.musicEnabled && cfg.soundLevel !== 'off' && !state.playing.running;
  const noteSvg = `<svg class="track-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"></path><circle cx="6" cy="18" r="3"></circle><circle cx="18" cy="16" r="3"></circle></svg>`;
  $('bgmTrackGrid').innerHTML = (state.bgmTracks || []).map(t => {
    const mins = Math.floor(t.durationSec / 60);
    const secs = String(Math.floor(t.durationSec % 60)).padStart(2, '0');
    const isSel = cfg.musicTrack === t.id;
    return `
      <button class="track-card ${isSel ? 'on' : ''}" data-track="${esc(t.id)}" ${t.available ? '' : 'disabled'} data-f>
        <div class="track-lead">
          <span class="track-ico">${noteSvg}</span>
          <div>
            <strong>${esc(t.title)}</strong>
            <em>${esc(t.subtitle)}${t.durationSec > 0 ? ` · ${mins}:${secs}` : ''}</em>
          </div>
        </div>
        ${isSel
          ? `<span class="xc-status">${isAudible ? 'Tocando' : 'Selecionada'}</span>`
          : ''}
      </button>
    `;
  }).join('');

  $('bgmTrackGrid').querySelectorAll('[data-track]').forEach(btn => {
    btn.addEventListener('click', () => {
      AudioEngine.playSfx('decide', 0.78);
      cmd('music.track', { id: btn.dataset.track });
    });
  });

  renderSteamAccounts();
  XCSelect.syncAll();
}

function renderSteamAccounts() {
  const accs = state.steam || [];
  const wrap = $('steamAccountsArea');
  if (accs.length === 0) {
    wrap.innerHTML = `<p class="panel-desc">Nenhuma conta local da Steam foi detectada neste computador.</p>`;
    return;
  }
  const steamRunning = accs.some(a => a.isSteamRunning);
  wrap.innerHTML = (steamRunning
    ? `<div class="xc-status warn">Feche a Steam antes de adicionar ou atualizar o atalho.</div>`
    : `<div class="xc-status good">Steam fechada · pronta para configurar</div>`) +
    accs.map(a => `
      <div class="steam-row">
        <div>
          <strong>${esc(a.personaName)}</strong>
          <div class="sub">${a.hasShortcut ? 'Presente na sua biblioteca Steam' : 'Ainda não adicionado à biblioteca'}</div>
        </div>
        <div style="display:flex;gap:8px">
          <button class="xc-btn primary sm" data-steam-add="${esc(a.userId)}" data-f>${a.hasShortcut ? 'Atualizar na Steam' : 'Adicionar à Steam'}</button>
          ${a.hasShortcut ? `<button class="xc-btn danger sm" data-steam-rem="${esc(a.userId)}" data-f>Remover</button>` : ''}
        </div>
      </div>
    `).join('');

  wrap.querySelectorAll('[data-steam-add]').forEach(btn => {
    btn.addEventListener('click', () => {
      cmd('steam.add', { userId: btn.dataset.steamAdd, launchViaLauncher: true });
    });
  });
  wrap.querySelectorAll('[data-steam-rem]').forEach(btn => {
    btn.addEventListener('click', () => {
      cmd('steam.remove', { userId: btn.dataset.steamRem });
    });
  });
}

function renderSystem() {
  const inst = state.install;
  const hw = state.hardware;
  if (inst) {
    $('sysInstallPath').textContent = inst.installDir;
    const dirs = inst.availableInstallDirs || [];
    $('installSwitcher').innerHTML = dirs.map(d => {
      const label = d.toLowerCase().endsWith('\\work\\test') ? 'Ambiente de Teste'
        : d.toLowerCase().endsWith('\\games') ? 'Instalação Principal' : d;
      const isCur = d.toLowerCase() === inst.installDir.toLowerCase();
      return `<button class="xc-btn sm ${isCur ? 'primary' : 'ghost'}" data-switch-inst="${esc(d)}" data-f>${esc(label)}</button>`;
    }).join('') + `<button class="xc-btn sm ghost" id="pickInstallBtn" data-f>Outra pasta…</button>`;

    $('installSwitcher').querySelectorAll('[data-switch-inst]').forEach(btn => {
      btn.addEventListener('click', () => {
        cmd('system.switchInstall', { path: btn.dataset.switchInst });
      });
    });
    $('pickInstallBtn')?.addEventListener('click', () => cmd('system.pickInstall'));

    const checks = [
      ['Executável', inst.hasExe, inst.hasExe ? 'OK' : 'Ausente'],
      ['Código AOT', inst.hasAotDll, inst.hasAotDll ? 'OK' : 'Ausente'],
      ['Arquivos base', inst.hasRomfs, inst.hasRomfs ? 'OK' : 'Ausente'],
      ['Upscaling', inst.hasDlss, inst.hasDlss ? 'DLSS / DLAA' : 'FSR 3.1'],
      ['Expansões', inst.addons.length > 0, `${inst.addons.length} pacotes`],
      ['Shaders', inst.shaderCacheFiles > 0, fmtBytes(inst.shaderCacheBytes)]
    ];
    $('integrityGrid').innerHTML = checks.map(([name, ok, tag]) => `
      <div class="int-item">
        <span>${esc(name)}</span>
        <span class="xc-status ${ok ? 'good' : 'warn'}">${esc(tag)}</span>
      </div>
    `).join('');
  }

  if (hw) {
    $('hwGrid').innerHTML = `
      <div class="hw-box"><span>GPU</span><strong>${esc(hw.gpuName.replace('NVIDIA GeForce ', ''))} (${hw.gpuVramGb} GB)</strong></div>
      <div class="hw-box"><span>Monitor</span><strong>${hw.screenWidth}×${hw.screenHeight} · ${hw.monitorRefreshHz} Hz</strong></div>
      <div class="hw-box"><span>RAM</span><strong>${hw.totalRamGb} GB</strong></div>
      <div class="hw-box"><span>Disco Livre</span><strong>C: ${hw.freeCDriveGb}G · ${esc(hw.installDriveLetter)} ${hw.freeInstallDriveGb}G</strong></div>
    `;
  }

  renderSetup();
  renderLogs();
}

function renderSetup() {
  const s = state.setup || { running: false, preflight: null };
  const pre = s.preflight;
  const pickBtn = $('setupPickNupBtn');
  const startBtn = $('setupStartBtn');
  const cancelBtn = $('setupCancelBtn');
  const preBox = $('setupPreflightBox');
  const progWrap = $('setupProgressWrap');
  const badge = $('setupBadge');
  if (!pickBtn || !preBox) return;

  pickBtn.disabled = !!s.running;
  if (badge) {
    badge.textContent = s.running ? 'Em andamento' : (pre?.readyToRun ? 'Validado' : 'Pipeline Nativo');
    badge.className = 'xc-status ' + (s.running ? 'warn' : (pre && !pre.readyToRun ? 'warn' : 'good'));
  }

  if (pre && pre.nup) {
    const nup = pre.nup;
    preBox.classList.remove('hidden');
    preBox.innerHTML = `
      <div class="setup-meta-row"><span>Pacote</span><strong>${esc(nup.gameTitle)} ${nup.displayVersion ? 'v' + esc(nup.displayVersion) : ''} (${esc(nup.baseTitleIdHex)})</strong></div>
      <div class="setup-meta-row"><span>Conteúdo</span><strong>${nup.packageCount} pacotes · ${Number(nup.totalFileCount || 0).toLocaleString('pt-BR')} arquivos · est. ${fmtBytes(nup.estimatedOutputBytes)}</strong></div>
      <div class="setup-meta-row"><span>Destino</span><strong>${esc(pre.targetInstallDir)} (livre: ${fmtBytes(pre.freeDiskBytes)})</strong></div>
      ${pre.blockingReason
        ? `<div class="setup-warn">${esc(pre.blockingReason)}</div>`
        : `<label class="setup-check"><input type="checkbox" id="setupForceAot" ${!pre.targetAlreadyHasAot ? 'checked disabled' : ''}> Recompilar código nativo AOT (xenoblade2_code.dll)</label>`}
    `;
    startBtn?.classList.toggle('hidden', !pre.readyToRun || !!s.running);
  } else {
    preBox.classList.add('hidden');
    startBtn?.classList.add('hidden');
  }

  cancelBtn?.classList.toggle('hidden', !s.running);

  if (state.setupProgress && progWrap) {
    const p = state.setupProgress;
    progWrap.classList.remove('hidden');
    $('setupProgStage').textContent = p.stageCount > 0 && p.stageIndex > 0
      ? `[${p.stageIndex}/${p.stageCount}] ${p.stage}`
      : p.stage;
    $('setupProgPct').textContent = `${Math.round(p.percent || 0)}%`;
    $('setupProgFill').style.width = `${Math.max(0, Math.min(100, p.percent || 0))}%`;
    $('setupProgFill').classList.toggle('err', !!p.isError);
    $('setupProgDetail').textContent = p.detail || '';
  }
}

function renderLogs() {
  const txt = state.logs?.[state.logTab] || '(Sem conteúdo)';
  const pre = $('logViewer');
  if (!pre) return;
  pre.textContent = txt;
  pre.scrollTop = pre.scrollHeight;
}

function setSelect(id, val) {
  const el = $(id);
  if (!el) return;
  if ([...el.options].some(o => o.value === val)) el.value = val;
  XCSelect.syncButton(el);
}

// ==================== Interactive Onboarding Wizard ====================
const OB_STEP_COUNT = 5;

function isInstallReadyForOnboarding() {
  return !state.onboarding.simulateMissing && !!state.install?.isReadyToPlay;
}

function openOnboarding(step = 0) {
  closePresetMenu();
  closeHelpMenu();
  XCSelect.close();
  closeTour(false);
  state.onboarding.autoOpenedOnce = true;
  state.onboarding.open = true;
  state.onboarding.step = Math.max(0, Math.min(OB_STEP_COUNT - 1, step));
  state.onboarding.padTestLock = false;
  document.body.classList.add('onboarding-open');
  const ov = $('onboardingOverlay');
  ov?.classList.add('open');
  ov?.setAttribute('aria-hidden', 'false');
  renderOnboarding();
  clearFocus();
}

function closeOnboarding(markComplete = false) {
  if (!state.onboarding.open && !markComplete) return;
  if (state.onboarding.open && !markComplete) AudioEngine.playSfx('cancel', 0.72);
  state.onboarding.open = false;
  state.onboarding.padTestLock = false;
  state.onboarding.forceNupView = false;
  state.onboarding.simulateMissing = false;
  document.body.classList.remove('onboarding-open');
  const ov = $('onboardingOverlay');
  ov?.classList.remove('open');
  ov?.setAttribute('aria-hidden', 'true');
  if (markComplete) cmd('onboarding.complete');
  clearFocus();
}

function setOnboardingStep(idx) {
  const clamped = Math.max(0, Math.min(OB_STEP_COUNT - 1, idx));
  if (clamped !== state.onboarding.step) {
    AudioEngine.playSfx(clamped > state.onboarding.step ? 'decide' : 'cancel', 0.72);
  }
  state.onboarding.step = clamped;
  if (clamped !== 4) state.onboarding.padTestLock = false;
  renderOnboarding();
  clearFocus();
}

function finishOnboarding(andStartTour = false) {
  const shouldMark = isInstallReadyForOnboarding();
  closeOnboarding(shouldMark);
  goScreen('home');
  if (andStartTour) {
    setTimeout(() => startTour(0), 220);
  } else if (shouldMark) {
    showToast('Tudo configurado! Clique em Jogar quando estiver pronto para Alrest.');
  }
}

function renderOnboarding() {
  if (!state.onboarding.open) return;
  const step = state.onboarding.step;

  // Stepper highlights
  const stepBtns = [...document.querySelectorAll('.ob-step')];
  const stepLines = [...document.querySelectorAll('.ob-step-line')];
  stepBtns.forEach((b, i) => {
    b.classList.toggle('on', i === step);
    b.classList.toggle('done', i < step);
  });
  stepLines.forEach((l, i) => l.classList.toggle('done', i < step));

  document.querySelectorAll('.ob-pane').forEach(p => {
    p.classList.toggle('on', Number(p.dataset.obPane) === step);
  });

  renderOnboardingInstall();
  renderOnboardingGraphics();
  renderOnboardingLanguage();
  renderOnboardingSaves();
  renderOnboardingControls();

  // Footer controls
  const isReady = isInstallReadyForOnboarding();
  $('obPrevBtn')?.classList.toggle('hidden', step === 0);
  const skipBtn = $('obSkipBtn');
  if (skipBtn) {
    skipBtn.textContent = state.config?.onboardingCompleted ? 'Fechar guia' : 'Pular guia';
    skipBtn.classList.toggle('hidden', step === OB_STEP_COUNT - 1);
  }
  $('obFinishTourBtn')?.classList.toggle('hidden', step !== OB_STEP_COUNT - 1);

  const nextBtn = $('obNextBtn');
  if (nextBtn) {
    if (step === 0) {
      nextBtn.textContent = isReady ? 'Começar configuração →' : 'Continuar mesmo assim →';
    } else if (step < OB_STEP_COUNT - 1) {
      nextBtn.textContent = 'Próximo passo →';
    } else {
      nextBtn.textContent = 'Concluir e ir para Alrest';
    }
  }
}

function renderOnboardingInstall() {
  const inst = state.install;
  const hw = state.hardware;
  const rec = state.recommendation;
  const isReady = isInstallReadyForOnboarding();
  const showNup = !isReady || state.onboarding.forceNupView;

  $('obInstallReadyView')?.classList.toggle('hidden', showNup);
  $('obInstallNupView')?.classList.toggle('hidden', !showNup);

  if (inst && !showNup) {
    const visibleAddons = (inst.addons || []).filter(a =>
      a.sizeBytes > 10000 && !/Licen[çc]a Expansion Pass/i.test(a.displayName)
    );
    const hasTorna = visibleAddons.some(a => String(a.titleId || '').toLowerCase().endsWith('9001'));
    const hasJpVoice = visibleAddons.some(a => String(a.titleId || '').toLowerCase().endsWith('9063'));
    const addonDetail = [
      hasTorna ? 'Torna' : null,
      hasJpVoice ? 'Vozes JP' : null,
      visibleAddons.length > 2 ? `+${visibleAddons.length - (hasTorna ? 1 : 0) - (hasJpVoice ? 1 : 0)} DLCs` : null
    ].filter(Boolean).join(' · ') || 'Jogo base pronto';

    const gpuShort = hw ? hw.gpuName.replace(/^NVIDIA GeForce\s+/i, '') : 'GPU Vulkan';
    const shaderCnt = inst.shaderCatalogCount ? inst.shaderCatalogCount.toLocaleString('pt-BR') : null;
    const shaderSub = inst.shaderCachePrecompiled && shaderCnt
      ? `${shaderCnt} shaders pré-compilados`
      : shaderCnt
        ? `${shaderCnt} shaders serão compilados na 1ª abertura`
        : 'Preparado automaticamente na 1ª abertura';

    $('obReadyChecklist').innerHTML = `
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Edição do Jogo</strong><span>Switch 2 Edition 3.0.1 · Runtime nativo PC</span></div>
        <span class="xc-status good">Verificado</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Expansões &amp; Áudio</strong><span>${esc(addonDetail)}</span></div>
        <span class="xc-status good">${visibleAddons.length} conteúdos</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Hardware Detectado</strong><span>${esc(gpuShort)}${hw ? ` (${hw.gpuVramGb} GB) · ${hw.screenWidth}×${hw.screenHeight} @ ${hw.monitorRefreshHz} Hz` : ''}</span></div>
        <span class="xc-status">${esc(rec?.upscalerLabel?.split(' ')[0] === 'NVIDIA' ? 'DLSS 4.5' : 'FSR 3.1')}</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Cache de Shaders</strong><span>${esc(shaderSub)}</span></div>
        <span class="xc-status ${inst.shaderCachePrecompiled ? 'good' : ''}">${inst.shaderCachePrecompiled ? 'Otimizado' : 'Pronto'}</span>
      </div>
    `;
    $('obReadyPath').textContent = inst.installDir || '—';
  }

  // NUP View checklist & setup progress
  if (showNup) {
    const simMissing = state.onboarding.simulateMissing;
    const hasExe = !simMissing && !!inst?.hasExe;
    const hasAot = !simMissing && !!inst?.hasAotDll;
    const hasRom = !simMissing && !!inst?.hasRomfs;
    const diskOk = hw ? hw.diskBudgetOk : true;

    const diskText = hw
      ? (String(hw.installDriveLetter || '').toUpperCase().startsWith('C:')
          ? `C: ${hw.freeCDriveGb} GiB livres`
          : `C: ${hw.freeCDriveGb} GiB · ${hw.installDriveLetter} ${hw.freeInstallDriveGb} GiB`)
      : 'Verificando…';

    $('obNupChecklist').innerHTML = `
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Espaço em Disco</strong><span>${diskText}</span></div>
        <span class="xc-status ${diskOk ? 'good' : 'warn'}">${diskOk ? 'OK' : 'Pouco espaço'}</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Executável PC</strong><span>xenoblade2.exe</span></div>
        <span class="xc-status ${hasExe ? 'good' : 'warn'}">${hasExe ? 'Pronto' : 'Pendente'}</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Código Nativo (AOT)</strong><span>xenoblade2_code.dll</span></div>
        <span class="xc-status ${hasAot ? 'good' : 'warn'}">${hasAot ? 'Pronto' : 'Será compilado'}</span>
      </div>
      <div class="ob-check-row">
        <div class="ob-check-row-l"><strong>Arquivos do Jogo</strong><span>Base + Expansões (.romfs)</span></div>
        <span class="xc-status ${hasRom ? 'good' : 'warn'}">${hasRom ? 'Pronto' : 'Aguardando .nup'}</span>
      </div>
    `;

    $('obBackToReadyBtn')?.classList.toggle('hidden', !isReady);

    const s = state.setup || { running: false, preflight: null };
    const pre = s.preflight;
    const pickBtn = $('obPickNupBtn');
    const preBox = $('obNupPreflight');
    const startBtn = $('obStartInstallBtn');
    const cancelBtn = $('obCancelInstallBtn');
    const progWrap = $('obNupProgress');

    if (pickBtn) pickBtn.disabled = !!s.running;
    if (pre && pre.nup) {
      const nup = pre.nup;
      $('obDropTitle').textContent = `Pacote carregado: ${nup.gameTitle}${nup.displayVersion ? ' v' + nup.displayVersion : ''}`;
      $('obDropSub').textContent = 'Clique novamente se quiser selecionar outro arquivo .nup';
      preBox?.classList.remove('hidden');
      preBox.innerHTML = `
        <div class="setup-meta-row"><span>Conteúdo</span><strong>${nup.packageCount} pacotes · ${Number(nup.totalFileCount || 0).toLocaleString('pt-BR')} arquivos · est. ${fmtBytes(nup.estimatedOutputBytes)}</strong></div>
        <div class="setup-meta-row"><span>Pasta de destino</span><strong>${esc(pre.targetInstallDir)} (livre: ${fmtBytes(pre.freeDiskBytes)})</strong></div>
        ${pre.blockingReason
          ? `<div class="setup-warn">${esc(pre.blockingReason)}</div>`
          : `<label class="setup-check"><input type="checkbox" id="obForceAot" ${!pre.targetAlreadyHasAot ? 'checked disabled' : ''}> Recompilar código nativo AOT (xenoblade2_code.dll)</label>`}
      `;
      startBtn?.classList.toggle('hidden', !pre.readyToRun || !!s.running);
    } else {
      $('obDropTitle').textContent = 'Arraste o arquivo .nup aqui ou clique para selecionar';
      $('obDropSub').textContent = 'Aceita o pacote completo Switch 2 Edition 3.0.1 (jogo base + Torna + DLCs + vozes JP)';
      preBox?.classList.add('hidden');
      startBtn?.classList.add('hidden');
    }
    cancelBtn?.classList.toggle('hidden', !s.running);

    if (state.setupProgress && progWrap) {
      const p = state.setupProgress;
      progWrap.classList.remove('hidden');
      $('obProgStage').textContent = p.stageCount > 0 && p.stageIndex > 0
        ? `Etapa ${p.stageIndex} de ${p.stageCount} — ${p.stage}`
        : p.stage;
      $('obProgPct').textContent = `${Math.round(p.percent || 0)}%`;
      $('obProgFill').style.width = `${Math.max(0, Math.min(100, p.percent || 0))}%`;
      $('obProgFill').classList.toggle('err', !!p.isError);
      $('obProgDetail').textContent = p.detail || '';

      document.querySelectorAll('#obProgMilestones [data-ms]').forEach(el => {
        const ms = Number(el.dataset.ms);
        const done = (p.isDone && !p.isError) || (p.stageIndex > ms);
        const active = !p.isDone && p.stageIndex === ms;
        el.classList.toggle('done', done);
        el.classList.toggle('active', active);
      });
    } else {
      progWrap?.classList.add('hidden');
    }
  }
}

function pickOnboardingPresets(allPresets, recId) {
  const byId = new Map((allPresets || []).map(p => [p.id, p]));
  const picked = [];
  const add = (id) => {
    const p = byId.get(id);
    if (p && !picked.some(x => x.id === p.id)) picked.push(p);
  };
  add(recId || '120_1440_dlaa');
  // Choose complementary options: sharper/higher-res + smoother/lighter + Original Switch 2
  const pool = ['120_4k_dlss_qual', '120_1440_dlss_qual', '120_1440_dlaa', '60_1440_dlss_qual', '60_1440_dlaa', '120_1080_dlaa', '60_1080_dlaa', '60_4k_dlaa'];
  for (const id of pool) {
    if (picked.length >= 3) break;
    add(id);
  }
  add('original');
  return picked.slice(0, 4);
}

function adaptPresetCopy(preset, hw, inst) {
  const rtxDlss = !!(hw?.isNvidiaRtx && inst?.hasDlss);
  if (preset.id === 'original') {
    return {
      title: 'Original (Switch 2)',
      desc: 'Visual e ritmo exatos do console: escala 1× nativa, 60 FPS no gameplay e 30 FPS nas cutscenes, sem filtros extras de PC.',
      specs: ['Switch 2 Nativo', '60 FPS', 'Sem filtros PC']
    };
  }
  const title = rtxDlss ? preset.name : preset.name.replace(/DLSS\s*4\.5/gi, 'FSR 3.1').replace(/DLAA/gi, 'FSR 3.1 AA');
  const desc = rtxDlss ? preset.description : preset.description.replace(/DLSS\s*4\.5|DLAA/gi, 'AMD FSR 3.1');
  const upTag = preset.upscale === 'dlss_quality'
    ? (rtxDlss ? 'DLSS 4.5 Qualidade' : 'FSR 3.1 Qualidade')
    : (rtxDlss ? 'NVIDIA DLAA' : 'AMD FSR 3.1 AA');
  const resTag = preset.id.includes('4k') ? '4K' : preset.id.includes('1440') ? '1440p' : '1080p';
  const fpsTag = preset.targetFps > 0 ? `Até ${preset.targetFps} FPS` : '60 FPS';
  return { title, desc, specs: [resTag, fpsTag, upTag] };
}

function renderOnboardingGraphics() {
  const hw = state.hardware;
  const inst = state.install;
  const cfg = state.config;
  const rec = state.recommendation;
  if (!cfg) return;

  if (hw) {
    const gpuShort = hw.gpuName.replace(/^NVIDIA GeForce\s+/i, '');
    $('obHwPills').innerHTML = `
      <span><strong>${esc(gpuShort)}</strong> · ${hw.gpuVramGb} GB</span>
      <i class="xc-diamond"></i>
      <span><strong>${hw.screenWidth}×${hw.screenHeight}</strong> · ${hw.monitorRefreshHz} Hz</span>
      <i class="xc-diamond"></i>
      <span><strong>${hw.totalRamGb} GB</strong> RAM</span>
    `;
  }

  if (rec) {
    $('obRecBanner').innerHTML = `
      <div class="ob-rec-main">
        <img class="ob-rec-crest" src="/media/icon/cristal.png" alt="" onerror="this.style.display='none'">
        <div>
          <strong>Recomendado para seu PC: ${esc(rec.recommendedPresetName)}</strong>
          <span>${esc(rec.reason)}</span>
          ${rec.diskWarning ? `<div class="ob-rec-warn">${esc(rec.diskWarning)}</div>` : ''}
        </div>
      </div>
      <span class="xc-status brass">${esc(rec.tierLabel)}</span>
    `;
  }

  const cards = pickOnboardingPresets(state.presets, rec?.recommendedPresetId);
  $('obPresetGrid').innerHTML = cards.map(p => {
    const isSel = cfg.preset === p.id;
    const isRec = rec && p.id === rec.recommendedPresetId;
    const copy = adaptPresetCopy(p, hw, inst);
    return `
      <button class="ob-preset-card ${isSel ? 'on' : ''} ${isRec ? 'is-rec' : ''}" data-ob-preset="${esc(p.id)}" type="button" data-f>
        ${isRec ? '<div class="ob-preset-ribbon"><i class="xc-diamond"></i><span>Recomendado para sua GPU</span></div>' : ''}
        <div class="ob-preset-inner">
          <div class="ob-preset-top">
            <div class="ob-preset-title">${esc(copy.title)}</div>
            ${!isRec ? `<span class="section-count">${esc(p.badge)}</span>` : ''}
          </div>
          <p class="ob-preset-desc">${esc(copy.desc)}</p>
        </div>
        <div class="ob-preset-foot">
          <span class="ob-preset-specs-line">${esc(copy.specs.join(' · '))}</span>
          ${isSel ? '<span class="xc-status brass">Selecionado</span>' : '<span class="section-count">Clique para usar</span>'}
        </div>
      </button>
    `;
  }).join('');

  $('obPresetGrid').querySelectorAll('[data-ob-preset]').forEach(btn => {
    btn.addEventListener('click', () => {
      cmd('preset.set', { id: btn.dataset.obPreset });
    });
  });
}

function renderOnboardingLanguage() {
  const mods = state.mods;
  const inst = state.install;
  const isReady = isInstallReadyForOnboarding();
  const packs = mods?.installedPacks || [];
  const activeId = (mods?.activeResourcePack || '').toLowerCase();

  const hasPtBr = packs.some(p => p.id.toLowerCase() === 'pt-br');
  const hasPtBrJp = packs.some(p => p.id.toLowerCase() === 'pt-br-jp');

  $('obLangNotReadyBanner')?.classList.toggle('hidden', isReady);

  const activeLabel = activeId === 'pt-br-jp'
    ? 'PT-BR (Nomes Originais JP)'
    : activeId === 'pt-br'
      ? 'PT-BR (Nomes Ocidentais)'
      : 'Original do Jogo';
  if ($('obActivePackBadge')) $('obActivePackBadge').textContent = `Ativo: ${activeLabel}`;

  const options = [
    {
      id: 'pt-br',
      title: 'Português (Nomes Ocidentais)',
      desc: 'Tradução completa em PT-BR mantendo os nomes oficiais do lançamento ocidental.',
      artUrl: '/media/art/dlc_battle.png',
      artCls: 'art-battle',
      available: isReady && hasPtBr,
      active: activeId === 'pt-br',
      samples: [
        ['Blades', 'Pyra · Mythra'],
        ['Personagens', 'Malos · Morag · Azurda'],
        ['Mundo', 'Alrest · Elísio · Torna']
      ]
    },
    {
      id: 'pt-br-jp',
      title: 'Português (Nomes Originais JP)',
      desc: 'Tradução completa em PT-BR usando os nomes originais do roteiro japonês — ideal com áudio JP.',
      artUrl: '/media/art/dlc_blades.png',
      artCls: 'art-blades',
      available: isReady && hasPtBrJp,
      active: activeId === 'pt-br-jp',
      samples: [
        ['Blades', 'Homura · Hikari'],
        ['Personagens', 'Metsu · Meleph · Seiryuu'],
        ['Mundo', 'Alst · Paraíso · Ira']
      ]
    },
    {
      id: '',
      title: 'Original do Jogo',
      desc: 'Mantém os textos originais intactos da Switch 2 Edition (Inglês por padrão).',
      artUrl: '/media/art/keyart_bg.png',
      artCls: 'art-keyart',
      available: isReady,
      active: !activeId,
      samples: [
        ['Idioma', 'Inglês (ou base do sistema)'],
        ['Nomes', 'Padrão oficial Nintendo'],
        ['Pacotes', 'Nenhum overlay ativo']
      ]
    }
  ];

  $('obLangGrid').innerHTML = options.map(opt => `
    <button class="ob-lang-card ${opt.active ? 'on' : ''}" data-ob-pack="${esc(opt.id)}" ${opt.available ? '' : 'disabled'} type="button" data-f>
      <div class="ob-lang-art ${opt.artCls}" style="background-image:url('${opt.artUrl}')"></div>
      <div class="ob-lang-body">
        <div>
          <h3>${esc(opt.title)}</h3>
          <p class="ob-lang-desc">${esc(opt.desc)}</p>
        </div>
        <div class="ob-lang-samples">
          ${opt.samples.map(([k, v]) => `<div><span>${esc(k)}</span><strong>${esc(v)}</strong></div>`).join('')}
        </div>
        <div class="ob-lang-foot">
          <span>${opt.available ? 'Troca segura · sem alterar base' : (isReady ? 'Pacote não instalado' : 'Disponível após instalar')}</span>
          ${opt.active ? '<span class="xc-status good">Ativo</span>' : (opt.available ? '<span class="inline-link">Selecionar</span>' : '')}
        </div>
      </div>
    </button>
  `).join('');

  $('obLangGrid').querySelectorAll('[data-ob-pack]').forEach(btn => {
    btn.addEventListener('click', () => {
      if (btn.disabled) return;
      cmd('mods.activate', { id: btn.dataset.obPack });
    });
  });

  const hasJpVoice = (inst?.addons || []).some(a => String(a.titleId || '').toLowerCase().endsWith('9063'));
  const voiceSvg = `<svg class="ob-voice-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"></polygon><path d="M15.5 8.5a5 5 0 0 1 0 7"></path><path d="M19 5.5a9 9 0 0 1 0 13"></path></svg>`;
  const shieldSvg = `<svg class="ob-voice-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"></path><polyline points="9 12 11 14 15 10"></polyline></svg>`;
  $('obVoiceBar').innerHTML = hasJpVoice
    ? `<div class="ob-voice-lead"><span class="ob-voice-ico">${voiceSvg}</span><div><strong>Pacote de Vozes em Japonês já instalado!</strong> Para alternar entre dublagem em japonês ou inglês, ajuste dentro do jogo em <strong>Sistema › Opções › Idioma de Voz</strong>.</div></div><span class="xc-status good">Áudio JP + EN</span>`
    : `<div class="ob-voice-lead"><span class="ob-voice-ico">${shieldSvg}</span><div><strong>Arquivos originais protegidos:</strong> os pacotes de tradução ficam isolados e podem ser trocados a qualquer momento na aba <strong>Mods</strong>.</div></div><span class="xc-status">100% reversível</span>`;
}

function renderOnboardingSaves() {
  const s = state.saves;
  const active = s?.activeMain;

  if (active && active.isValid) {
    const party = active.activeBladeName
      ? `${active.leaderName} & ${active.activeBladeName}`
      : active.leaderName;
    const tornaPart = s.activeTorna ? `<span>Torna <strong>Incluído</strong></span>` : '';
    $('obActiveSaveCard').innerHTML = `
      <div class="as-head-row">
        <div class="as-head-info">
          <div class="xc-eyebrow"><span class="xc-diamond"></span><span>SAVE ATIVO · ${esc((active.regionName || 'Alrest').toUpperCase())}</span></div>
          <div class="as-title" style="font-size:22px">${esc(active.landmarkName)}</div>
          <div class="as-meta" style="margin-top:6px">
            <span><strong>${esc(party)}</strong> · Nv. ${active.leaderLevel}</span>
            <span>Tempo <strong>${esc(active.playTimeFormatted)}</strong></span>
            <span>Ouro <strong class="gold-val">${esc(fmtGold(active.gold))}</strong></span>
            ${tornaPart}
          </div>
        </div>
        <img class="as-rex-art" src="/media/art/save_rex.png" alt="" onerror="this.style.display='none'">
      </div>
    `;
  } else {
    $('obActiveSaveCard').innerHTML = `
      <div class="xc-eyebrow"><span class="xc-diamond"></span><span>NOVA AVENTURA</span></div>
      <div class="as-title" style="font-size:22px;margin-top:6px">Nenhum save ativo ainda</div>
      <p class="panel-desc" style="margin-top:6px">Você pode começar uma jornada do zero ou importar um save existente de um emulador ou pasta ao lado.</p>
    `;
  }

  const candidates = s?.emulatorSaves || [];
  const fallbackDirs = candidates.length === 0 ? (s?.detectedEmulatorDirs || []) : [];
  const count = candidates.length || fallbackDirs.length;
  $('obEmuCount').textContent = count === 0 ? 'Nenhum encontrado' : (count === 1 ? '1 save encontrado' : `${count} saves encontrados`);

  if (candidates.length > 0) {
    $('obEmuList').innerHTML = candidates.map(c => {
      const sub = `${c.info.landmarkName}${c.info.regionName ? ' · ' + c.info.regionName : ''} · ${c.info.summaryLine}${c.hasTorna ? ' · +Torna' : ''}`;
      return `
        <div class="ob-emu-card">
          <div>
            <strong>${esc(c.emulatorName)}</strong>
            <span>${esc(sub)}</span>
          </div>
          <button class="xc-btn sm primary" data-ob-import-emu="${esc(c.dirPath)}" type="button" data-f>Importar</button>
        </div>
      `;
    }).join('');
  } else if (fallbackDirs.length > 0) {
    $('obEmuList').innerHTML = fallbackDirs.map(d => `
      <div class="ob-emu-card">
        <div>
          <strong>${esc(detectEmulatorName(d))}</strong>
          <span>${esc(d)}</span>
        </div>
        <button class="xc-btn sm primary" data-ob-import-emu="${esc(d)}" type="button" data-f>Importar</button>
      </div>
    `).join('');
  } else {
    $('obEmuList').innerHTML = `<p class="panel-desc">Nenhum save automático de Ryujinx, Yuzu, Sudachi, Citron ou Eden foi encontrado nas pastas padrão deste PC.</p>`;
  }

  const hasActive = !!(active && active.isValid);
  $('obEmuList').querySelectorAll('[data-ob-import-emu]').forEach(btn => {
    btn.addEventListener('click', () => {
      if (hasActive && btn.dataset.armed !== '1') {
        btn.dataset.armed = '1';
        btn.textContent = 'Confirmar?';
        setTimeout(() => {
          if (btn.isConnected && btn.dataset.armed === '1') {
            btn.dataset.armed = '';
            btn.textContent = 'Importar';
          }
        }, 4000);
        return;
      }
      cmd('saves.restore', { path: btn.dataset.obImportEmu });
    });
  });
}

function renderOnboardingControls() {
  const inst = state.install;
  const shaderTip = $('obShaderTip');
  if (shaderTip && inst) {
    const cnt = inst.shaderCatalogCount ? inst.shaderCatalogCount.toLocaleString('pt-BR') : null;
    if (inst.shaderCachePrecompiled && cnt) {
      shaderTip.textContent = `Seu cache com ${cnt} shaders já está pré-compilado para sua placa de vídeo — pronto para rodar sem engasgos desde o primeiro segundo!`;
    } else if (cnt) {
      shaderTip.textContent = `Na 1ª abertura, ${cnt} shaders serão pré-compilados por alguns segundos uma única vez para eliminar travadinhas durante a aventura.`;
    } else {
      shaderTip.textContent = 'Na 1ª abertura, os shaders são pré-compilados uma única vez para garantir gameplay liso sem engasgos.';
    }
  }
  const amiiboTip = $('obAmiiboTip');
  if (amiiboTip) {
    const jpNames = (state.mods?.activeResourcePack || state.ini?.['Display.resource_pack'] || '').toLowerCase() === 'pt-br-jp';
    const pair = jpNames ? 'Homura/Hikari' : 'Pyra/Mythra';
    amiiboTip.innerHTML = `Arraste saves <code>.sav</code> ou mods <code>.zip</code> direto para esta janela. Em <b>Configurações › Vídeo</b> você também pode ativar Amiibos virtuais (${pair}) ao vivo.`;
  }
  const lockBtn = $('obPadLockBtn');
  if (lockBtn) {
    lockBtn.textContent = state.onboarding.padTestLock
      ? 'Modo teste ativo · Clique ou ESC para destravar'
      : 'Travar navegação p/ testar B e D-Pad';
    lockBtn.classList.toggle('primary', state.onboarding.padTestLock);
    lockBtn.classList.toggle('ghost', !state.onboarding.padTestLock);
  }
}

function updateGamepadTester(gp) {
  if (!state.onboarding.open || state.onboarding.step !== 4) return;
  const title = $('obPadTitle');
  const sub = $('obPadSub');
  const badge = $('obPadStateBadge');
  const readout = $('obPadReadout');
  if (!title || !badge || !readout) return;

  if (!gp) {
    title.textContent = 'Aguardando controle…';
    sub.textContent = 'Conecte Xbox, DualSense ou Switch Pro e pressione qualquer botão';
    badge.textContent = 'Sem sinal';
    badge.className = 'xc-status warn';
    return;
  }

  const idLow = String(gp.id || '').toLowerCase();
  const isPs = idLow.includes('dualsense') || idLow.includes('dualshock') || idLow.includes('wireless controller') || idLow.includes('054c');
  const isSwitch = idLow.includes('pro controller') || idLow.includes('nintendo') || idLow.includes('057e');
  const family = isPs ? 'PlayStation / DualSense' : isSwitch ? 'Nintendo Switch Pro' : 'Xbox / XInput';

  title.textContent = `Controle reconhecido (${family})`;
  sub.textContent = gp.id.length > 56 ? gp.id.slice(0, 56) + '…' : gp.id;
  badge.textContent = 'Conectado';
  badge.className = 'xc-status good';

  // Adapt face labels dynamically for PlayStation vs Xbox/Nintendo
  const labels = isPs ? ['✕', '○', '□', '△'] : ['A', 'B', 'X', 'Y'];
  for (let i = 0; i < 4; i++) {
    const t = $(`gpLbl${i}`);
    if (t) t.textContent = labels[i];
  }

  const btnNames = isPs
    ? ['✕', '○', '□', '△', 'L1', 'R1', 'L2', 'R2', 'Create', 'Options', 'L3', 'R3', 'D-Pad ▲', 'D-Pad ▼', 'D-Pad ◀', 'D-Pad ▶']
    : ['A', 'B', 'X', 'Y', 'LB', 'RB', 'LT', 'RT', 'View', 'Menu', 'L3', 'R3', 'D-Pad ▲', 'D-Pad ▼', 'D-Pad ◀', 'D-Pad ▶'];

  const activeNames = [];
  document.querySelectorAll('#obPadSvg [data-gp-btn]').forEach(el => {
    const idx = Number(el.dataset.gpBtn);
    const b = gp.buttons[idx];
    const val = b ? (b.value ?? (b.pressed ? 1 : 0)) : 0;
    const on = !!(b?.pressed || val > 0.2);
    el.classList.toggle('active', on);
    if (on) {
      activeNames.push((idx === 6 || idx === 7) && val < 0.99
        ? `${btnNames[idx]} (${Math.round(val * 100)}%)`
        : btnNames[idx]);
    }
  });

  const lx = gp.axes[0] || 0, ly = gp.axes[1] || 0;
  const rx = gp.axes[2] || 0, ry = gp.axes[3] || 0;
  $('gpStickL')?.setAttribute('cx', (114 + lx * 11).toFixed(1));
  $('gpStickL')?.setAttribute('cy', (108 + ly * 11).toFixed(1));
  $('gpStickR')?.setAttribute('cx', (272 + rx * 11).toFixed(1));
  $('gpStickR')?.setAttribute('cy', (154 + ry * 11).toFixed(1));

  if (Math.hypot(lx, ly) > 0.25) activeNames.push(`Analógico Esq (${Math.round(Math.min(1, Math.hypot(lx, ly)) * 100)}%)`);
  if (Math.hypot(rx, ry) > 0.25) activeNames.push(`Analógico Dir (${Math.round(Math.min(1, Math.hypot(rx, ry)) * 100)}%)`);

  if (activeNames.length > 0) {
    readout.textContent = `Respondendo ao vivo: ${activeNames.join(' · ')}`;
    readout.classList.add('active');
  } else {
    readout.textContent = state.onboarding.padTestLock
      ? 'Modo teste ativo: pressione qualquer botão ou mova os analógicos (ESC para destravar).'
      : 'Controle pronto! Pressione botões ou mova os analógicos para testar a resposta.';
    readout.classList.remove('active');
  }
}

// ==================== Interactive Spotlight Tour ====================
const TOUR_STEPS = [
  {
    screen: 'home',
    target: '.play-area',
    title: 'Jogar e Trocar Perfil em 1 Clique',
    desc: 'Clique em <strong>Jogar</strong> para abrir Xenoblade Chronicles 2. Use as setas ou clique no perfil logo abaixo para trocar resolução e FPS rapidamente.'
  },
  {
    screen: 'saves',
    target: '#activeSaveCard',
    title: 'Seus Saves Sempre Protegidos',
    desc: 'Acompanhe sua região, grupo e tempo de jogo, crie cópias nomeadas ou importe saves de emuladores. <strong>Todo save substituído gera backup automático.</strong>'
  },
  {
    screen: 'mods',
    target: '#packsList',
    title: 'Tradução PT-BR, Nomes JP e Mods',
    desc: 'Alterne entre <strong>PT-BR (Ocidental)</strong>, <strong>PT-BR (Nomes Originais JP)</strong> ou <strong>Original</strong> sem modificar os arquivos base — ou arraste mods <code>.zip</code> para cá.'
  },
  {
    screen: 'settings',
    subtab: 'video',
    target: '#settingsSubtabs',
    title: 'Configurações Ao Vivo e Personalização',
    desc: 'Ajustes de <strong>Vídeo</strong> (upscaling, FPS, iluminação e Amiibo) aplicam <strong>ao vivo com o jogo aberto</strong>. Nas outras abas você muda tema da Aegis, música, Steam e Discord.'
  },
  {
    screen: 'home',
    target: '#helpBtn',
    title: 'Central de Ajuda Sempre à Mão',
    desc: 'Ficou alguma dúvida depois? Clique neste ícone <strong>?</strong> a qualquer momento para refazer o guia inicial, testar seu controle ou repetir este tour.'
  }
];

function startTour(step = 0) {
  if (state.playing.running) {
    showToast('Feche o jogo antes de iniciar o tour interativo pela interface.');
    return;
  }
  closeOnboarding(false);
  closeHelpMenu();
  closePresetMenu();
  XCSelect.close();
  state.tour.open = true;
  state.tour.step = Math.max(0, Math.min(TOUR_STEPS.length - 1, step));
  const ov = $('tourOverlay');
  ov?.classList.add('open');
  ov?.setAttribute('aria-hidden', 'false');
  renderTourStep();
}

function closeTour(returnHome = true) {
  if (!state.tour.open) return;
  AudioEngine.playSfx('cancel', 0.72);
  state.tour.open = false;
  const ov = $('tourOverlay');
  ov?.classList.remove('open');
  ov?.setAttribute('aria-hidden', 'true');
  if (returnHome && !state.playing.running) goScreen('home');
  clearFocus();
}

function renderTourStep() {
  if (!state.tour.open) return;
  const idx = state.tour.step;
  const s = TOUR_STEPS[idx];
  if (!s) { closeTour(); return; }

  goScreen(s.screen);
  if (s.subtab) goSettingsSubtab(s.subtab);

  $('tourStepTag').textContent = `DICA ${idx + 1} DE ${TOUR_STEPS.length}`;
  $('tourTitle').textContent = s.title;
  $('tourDesc').innerHTML = s.desc;
  $('tourPrevBtn').classList.toggle('hidden', idx === 0);
  $('tourNextBtn').textContent = idx === TOUR_STEPS.length - 1 ? 'Concluir tour' : 'Próximo →';
  $('tourDots').innerHTML = TOUR_STEPS.map((_, i) => `<span class="tour-dot ${i === idx ? 'on' : ''}"></span>`).join('');

  requestAnimationFrame(() => positionTourStep());
}

function positionTourStep() {
  if (!state.tour.open) return;
  const s = TOUR_STEPS[state.tour.step];
  const target = s ? document.querySelector(s.target) : null;
  const spot = $('tourSpotlight');
  const card = $('tourCard');
  if (!target || !spot || !card) return;

  const winRect = $('win').getBoundingClientRect();
  const r = target.getBoundingClientRect();
  const k = winScale || 1;
  const pad = 10;
  const x = Math.max(8, (r.left - winRect.left) / k - pad);
  const y = Math.max(8, (r.top - winRect.top) / k - pad);
  const w = Math.min(1264 - x, r.width / k + pad * 2);
  const h = Math.min(784 - y, r.height / k + pad * 2);

  spot.style.left = `${x}px`;
  spot.style.top = `${y}px`;
  spot.style.width = `${w}px`;
  spot.style.height = `${h}px`;

  const cardW = 356;
  const cardH = Math.max(200, card.offsetHeight || 215);
  let cx, cy;

  // Prefer right -> below -> left -> above within 1280x800 bounds
  if (x + w + 18 + cardW <= 1260) {
    cx = x + w + 18;
    cy = Math.max(76, Math.min(776 - cardH, y));
  } else if (y + h + 18 + cardH <= 776) {
    cx = Math.max(24, Math.min(1260 - cardW, x + w - cardW));
    cy = y + h + 18;
  } else if (x - 18 - cardW >= 20) {
    cx = x - 18 - cardW;
    cy = Math.max(76, Math.min(776 - cardH, y));
  } else {
    cx = Math.max(24, Math.min(1260 - cardW, x));
    cy = Math.max(76, y - cardH - 18);
  }

  card.style.left = `${cx}px`;
  card.style.top = `${cy}px`;
}

// ==================== Event Wiring (Silent & Responsive) ====================
$('winMin').addEventListener('click', () => post({ t: 'win', a: 'min' }));
$('winMax').addEventListener('click', () => post({ t: 'win', a: 'max' }));
$('winClose').addEventListener('click', () => post({ t: 'win', a: 'close' }));

// Help Menu & Onboarding / Tour Triggers
$('helpBtn')?.addEventListener('click', (e) => {
  e.stopPropagation();
  closePresetMenu();
  XCSelect.close();
  $('helpAnchor')?.classList.toggle('open');
});
$('helpMenu')?.addEventListener('click', (e) => e.stopPropagation());
$('helpOpenWizard')?.addEventListener('click', () => openOnboarding(0));
$('helpStartTour')?.addEventListener('click', () => startTour(0));
$('helpOpenControls')?.addEventListener('click', () => openOnboarding(4));
$('sysReopenOnboardingBtn')?.addEventListener('click', () => openOnboarding(0));

// Onboarding Wizard controls
document.querySelectorAll('.ob-step').forEach(btn => {
  btn.addEventListener('click', () => setOnboardingStep(Number(btn.dataset.obStep)));
});
$('obPrevBtn')?.addEventListener('click', () => setOnboardingStep(state.onboarding.step - 1));
$('obNextBtn')?.addEventListener('click', () => {
  if (state.onboarding.step < OB_STEP_COUNT - 1) setOnboardingStep(state.onboarding.step + 1);
  else finishOnboarding(false);
});
$('obFinishTourBtn')?.addEventListener('click', () => finishOnboarding(true));
$('obSkipBtn')?.addEventListener('click', () => {
  closeOnboarding(isInstallReadyForOnboarding());
});

$('obShowNupInstallerBtn')?.addEventListener('click', () => {
  state.onboarding.forceNupView = true;
  renderOnboardingInstall();
});
$('obBackToReadyBtn')?.addEventListener('click', () => {
  state.onboarding.forceNupView = false;
  renderOnboardingInstall();
});
$('obPickNupBtn')?.addEventListener('click', () => cmd('setup.pickNup'));
$('obStartInstallBtn')?.addEventListener('click', () => {
  const pre = state.setup?.preflight;
  if (!pre?.nup?.nupPath) return;
  AudioEngine.playSfx('decide', 0.85);
  const forceAot = $('obForceAot')?.checked ?? false;
  cmd('setup.start', {
    nupPath: pre.nup.nupPath,
    targetDir: pre.targetInstallDir,
    forceRecompileAot: forceAot
  });
});
$('obCancelInstallBtn')?.addEventListener('click', () => { AudioEngine.playSfx('cancel', 0.8); cmd('setup.cancel'); });
$('obReadyPickFolderBtn')?.addEventListener('click', () => cmd('system.pickInstall'));
$('obPickExistingFolderBtn')?.addEventListener('click', () => cmd('system.pickInstall'));
$('obImportFileBtn')?.addEventListener('click', () => cmd('saves.import.pickFile'));
$('obImportFolderBtn')?.addEventListener('click', () => cmd('saves.import.pickFolder'));
$('obPadLockBtn')?.addEventListener('click', () => {
  state.onboarding.padTestLock = !state.onboarding.padTestLock;
  renderOnboardingControls();
});

// Spotlight Tour controls
$('tourPrevBtn')?.addEventListener('click', () => {
  state.tour.step = Math.max(0, state.tour.step - 1);
  renderTourStep();
});
$('tourNextBtn')?.addEventListener('click', () => {
  if (state.tour.step < TOUR_STEPS.length - 1) {
    state.tour.step++;
    renderTourStep();
  } else {
    closeTour(true);
  }
});
$('tourSkipBtn')?.addEventListener('click', () => closeTour(true));
window.addEventListener('resize', () => { if (state.tour.open) positionTourStep(); });

document.querySelectorAll('[data-nav]').forEach(el => {
  el.addEventListener('click', () => goScreen(el.dataset.nav));
});

$('playBtn').addEventListener('click', () => {
  closePresetMenu();
  if (state.install && !state.install.isReadyToPlay) {
    AudioEngine.playSfx('open', 0.8);
    state.onboarding.forceNupView = true;
    openOnboarding(0);
    return;
  }
  AudioEngine.playSfx('play', 0.95);
  cmd('play');
});

function stepPreset(delta) {
  if (!state.presets?.length || !state.config) return;
  const idx = state.presets.findIndex(p => p.id === state.config.preset);
  const next = state.presets[((idx < 0 ? 0 : idx) + delta + state.presets.length) % state.presets.length];
  closePresetMenu();
  AudioEngine.playSfx('cursor', 0.7);
  cmd('preset.set', { id: next.id });
}

$('presetPrev').addEventListener('click', (e) => { e.stopPropagation(); stepPreset(-1); });
$('presetNext').addEventListener('click', (e) => { e.stopPropagation(); stepPreset(1); });
$('presetTrigger').addEventListener('click', (e) => {
  e.stopPropagation();
  closeHelpMenu();
  XCSelect.close();
  $('presetPicker').classList.toggle('open');
});
window.addEventListener('click', () => { closePresetMenu(); closeHelpMenu(); XCSelect.close(); });
$('settingsBody')?.addEventListener('scroll', () => XCSelect.close(), { passive: true });

$('focusGameBtn').addEventListener('click', () => cmd('game.focus'));
$('openSettingsWhilePlaying')?.addEventListener('click', () => goScreen('settings'));
$('openLogWhilePlaying').addEventListener('click', () => cmd('open.log'));

$('musicBtn').addEventListener('click', () => cmd('music.toggle'));
$('homeTrackWhisper').addEventListener('click', () => cmd('music.toggle'));

// Saves buttons
$('savesOpenActive').addEventListener('click', () => cmd('open.folder', { what: 'saves' }));
$('savesOpenBackups').addEventListener('click', () => cmd('open.folder', { what: 'backups' }));
$('importSaveFileBtn').addEventListener('click', () => cmd('saves.import.pickFile'));
$('importSaveFolderBtn').addEventListener('click', () => cmd('saves.import.pickFolder'));
$('exportSaveBtn').addEventListener('click', () => cmd('saves.export'));

// Mods buttons
$('modsOpenLoc').addEventListener('click', () => cmd('open.folder', { what: 'localization' }));
$('installModZipBtn').addEventListener('click', () => cmd('mods.install.pickZip'));
$('installModFolderBtn').addEventListener('click', () => cmd('mods.install.pickFolder'));

// Settings subtabs & controls
document.querySelectorAll('[data-stab]').forEach(b => {
  b.addEventListener('click', () => goSettingsSubtab(b.dataset.stab));
});

document.querySelectorAll('[data-theme-pick]').forEach(b => {
  b.addEventListener('click', () => {
    AudioEngine.playSfx('decide', 0.8);
    cmd('theme.set', { accent: b.dataset.themePick });
  });
});

document.querySelectorAll('[data-sound]').forEach(b => {
  b.addEventListener('click', () => {
    const lvl = b.dataset.sound;
    cmd('sound.set', { level: lvl });
    if (lvl !== 'off') setTimeout(() => AudioEngine.playSfx('cursor', 0.85), 40);
  });
});

$('cfgMusicEnabled').addEventListener('change', () => {
  AudioEngine.playSfx('cursor', 0.75);
  cmd('music.toggle');
});

$('saveSettingsBtn').addEventListener('click', () => {
  AudioEngine.playSfx('decide', 0.85);
  cmd('settings.save', {
    ini: {
      'Display.window_mode': $('iniWindowMode').value,
      'Display.output_resolution': $('iniOutputRes').value,
      'Display.language': $('iniLanguage').value,
      'Display.docked': $('iniDocked').checked ? '1' : '0',
      'Graphics.upscale': $('iniUpscale').value,
      'Graphics.dlss_preset': $('iniDlssPreset').value,
      'Graphics.resolution': $('iniResolution').value,
      'Graphics.anisotropy': $('iniAnisotropy').value,
      'Graphics.upscale_sharpness': $('iniSharpness').value,
      'Graphics.upscale_mip_bias': $('iniMipBias').value,
      'Graphics.shadows': $('iniShadows').checked ? '1' : '0',
      'Graphics.vsync': $('iniVsync').checked ? '1' : '0',
      'Graphics.tonemap': $('iniTonemap').value,
      'Graphics.bloom_mode': $('iniBloomMode').value,
      'Graphics.contact_shadows': $('iniContactShadows').checked ? '1' : '0',
      'Graphics.gtao': $('iniGtao').checked ? '1' : '0',
      'Graphics.env_shading': $('iniEnvShading').checked ? '1' : '0',
      'Graphics.water_caustics': $('iniWaterCaustics').checked ? '1' : '0',
      'Graphics.ssgi': $('iniSsgi').checked ? '1' : '0',
      'Graphics.ssr': $('iniSsr').checked ? '1' : '0',
      'Performance.show_fps': $('iniShowFps').checked ? '1' : '0',
      'Input.amiibo': $('iniAmiibo').value,
      'Network.discord_presence': $('iniDiscord').checked ? '1' : '0'
    },
    launcher: {
      preset: $('cfgPreset').value,
      customXc2Fps: parseInt($('cfgCustomFps').value, 10),
      custom4kSelectiveScale: $('cfg4kScale').checked,
      minimizeOnLaunch: $('cfgMinimizeLaunch').checked,
      closeAfterLaunch: $('cfgCloseLaunch').checked
    }
  });
});

$('resetSettingsBtn').addEventListener('click', () => cmd('settings.resetRecommended'));
$('rebuildShadersBtn')?.addEventListener('click', () => cmd('shaders.rebuild'));
$('createDesktopShortcutBtn').addEventListener('click', () => cmd('shortcut.desktop'));

// System controls (inside Settings -> Sistema)
$('sysRefreshBtn').addEventListener('click', () => cmd('refresh'));
$('sysOpenInstallBtn').addEventListener('click', () => cmd('open.folder', { what: 'install' }));
$('sysOpenIniBtn').addEventListener('click', () => cmd('open.ini'));
$('openLogFileBtn').addEventListener('click', () => cmd('open.log'));
$('setupPickNupBtn')?.addEventListener('click', () => cmd('setup.pickNup'));
$('setupStartBtn')?.addEventListener('click', () => {
  const pre = state.setup?.preflight;
  if (!pre?.nup?.nupPath) return;
  AudioEngine.playSfx('decide', 0.85);
  const forceAot = $('setupForceAot')?.checked ?? false;
  cmd('setup.start', {
    nupPath: pre.nup.nupPath,
    targetDir: pre.targetInstallDir,
    forceRecompileAot: forceAot
  });
});
$('setupCancelBtn')?.addEventListener('click', () => { AudioEngine.playSfx('cancel', 0.8); cmd('setup.cancel'); });

$('sysCopyReportBtn').addEventListener('click', async () => {
  const report = [
    '=== Xenoblade Chronicles 2 PC Edition — Diagnóstico ===',
    `Instalação: ${state.install?.installDir} (${state.install?.modeLabel})`,
    `Exe SHA-256: ${state.install?.exeShortHash} | AOT SHA-256: ${state.install?.aotShortHash}`,
    `GPU: ${state.hardware?.gpuName} (${state.hardware?.gpuVramGb} GB) | Monitor: ${state.hardware?.screenWidth}x${state.hardware?.screenHeight}@${state.hardware?.monitorRefreshHz}Hz`,
    `Disco: C:${state.hardware?.freeCDriveGb} GiB | ${state.hardware?.installDriveLetter} ${state.hardware?.freeInstallDriveGb} GiB`,
    `Perfil: ${state.launchPlan?.profileSummary}`,
    `Save: ${state.saves?.activeMain?.landmarkName} (${state.saves?.activeMain?.summaryLine})`
  ].join('\n');
  try {
    await navigator.clipboard.writeText(report);
    showToast('Diagnóstico copiado para a área de transferência.');
  } catch {
    showToast('Não foi possível copiar o diagnóstico.', true);
  }
});

document.querySelectorAll('[data-log]').forEach(btn => {
  btn.addEventListener('click', () => {
    state.logTab = btn.dataset.log;
    document.querySelectorAll('[data-log]').forEach(b => b.classList.toggle('on', b.dataset.log === state.logTab));
    renderLogs();
  });
});

// ==================== Drag & Drop ====================
let dragDepth = 0;
window.addEventListener('dragenter', (e) => {
  e.preventDefault();
  dragDepth++;
  if (state.onboarding.open && state.onboarding.step === 0) {
    $('dropTitle').textContent = 'Solte o pacote .nup para instalar';
    $('dropDesc').textContent = 'Pacote Xenoblade Chronicles 2 Switch 2 Edition 3.0.1 (.nup)';
  } else if (state.onboarding.open && state.onboarding.step === 3) {
    $('dropTitle').textContent = 'Solte o save para importar';
    $('dropDesc').textContent = 'Arquivo bf2savefile.sav ou pasta do save (backup automático garantido)';
  } else {
    $('dropTitle').textContent = 'Solte para importar';
    $('dropDesc').textContent = 'Arquivo de save, pacote .zip de tradução/mod ou pacote .nup do jogo';
  }
  $('dropOverlay').classList.add('on');
});
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('dragleave', () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (dragDepth === 0) $('dropOverlay').classList.remove('on');
});
window.addEventListener('drop', (e) => {
  e.preventDefault();
  dragDepth = 0;
  $('dropOverlay').classList.remove('on');
  if (e.dataTransfer?.files?.length && window.chrome?.webview?.postMessageWithAdditionalObjects) {
    window.chrome.webview.postMessageWithAdditionalObjects(
      JSON.stringify({ t: 'drop', context: state.onboarding.open ? 'onboarding' : state.screen }),
      [...e.dataTransfer.files]
    );
  }
});

// ==================== Keyboard & Gamepad Spatial Navigation ====================
function visibleFocusables() {
  let list;
  if (state.tour.open) {
    list = [...document.querySelectorAll('#tourOverlay [data-f]')];
  } else if (state.onboarding.open) {
    list = [...document.querySelectorAll('#onboardingOverlay [data-f]')];
  } else if ($('helpAnchor')?.classList.contains('open')) {
    list = [...document.querySelectorAll('#helpMenu [data-f]')];
  } else {
    const activeScreen = document.querySelector('.screen.active');
    if (!activeScreen) return [];
    list = [
      ...document.querySelectorAll('.topbar [data-f]'),
      ...activeScreen.querySelectorAll('[data-f]')
    ];
  }
  return list.filter(el => !el.disabled && el.offsetParent !== null && el.getBoundingClientRect().width > 0);
}

function clearFocus() {
  if (state.focusedEl) state.focusedEl.classList.remove('kbd-focus');
  state.focusedEl = null;
}

function setFocus(el) {
  clearFocus();
  if (!el) return;
  state.focusedEl = el;
  el.classList.add('kbd-focus');
  el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

function moveFocus(dir) {
  const items = visibleFocusables();
  if (items.length === 0) return;
  if (!state.focusedEl || !items.includes(state.focusedEl)) {
    const preferred = state.tour.open
      ? $('tourNextBtn')
      : state.onboarding.open
        ? $('obNextBtn')
        : (document.querySelector('.screen.active #playBtn') || items[0]);
    setFocus(preferred && items.includes(preferred) ? preferred : items[0]);
    return;
  }
  const cur = state.focusedEl.getBoundingClientRect();
  const cx = cur.left + cur.width / 2;
  const cy = cur.top + cur.height / 2;

  let best = null, bestScore = Infinity;
  for (const el of items) {
    if (el === state.focusedEl) continue;
    const r = el.getBoundingClientRect();
    const dx = (r.left + r.width / 2) - cx;
    const dy = (r.top + r.height / 2) - cy;
    let primary = 0, perp = 0;
    if (dir === 'right') { if (dx <= 10) continue; primary = dx; perp = Math.abs(dy); }
    if (dir === 'left')  { if (dx >= -10) continue; primary = -dx; perp = Math.abs(dy); }
    if (dir === 'down')  { if (dy <= 10) continue; primary = dy; perp = Math.abs(dx); }
    if (dir === 'up')    { if (dy >= -10) continue; primary = -dy; perp = Math.abs(dx); }
    const score = primary + perp * 2.4;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (best) {
    setFocus(best);
    AudioEngine.playSfx('cursor', 0.55);
  }
}

const navOrder = ['home', 'saves', 'mods', 'settings'];
function cycleNav(delta) {
  if (state.tour.open) {
    const next = Math.max(0, Math.min(TOUR_STEPS.length - 1, state.tour.step + delta));
    if (next !== state.tour.step) { state.tour.step = next; renderTourStep(); }
    return;
  }
  if (state.onboarding.open) {
    setOnboardingStep(state.onboarding.step + delta);
    return;
  }
  if (state.playing.running) return;
  const idx = navOrder.indexOf(state.screen);
  const next = navOrder[(idx + delta + navOrder.length) % navOrder.length];
  goScreen(next);
}

window.addEventListener('mousedown', () => clearFocus());
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (state.tour.open) { closeTour(true); return; }
    if ($('helpAnchor')?.classList.contains('open')) { closeHelpMenu(); return; }
    if ($('presetPicker')?.classList.contains('open') || XCSelect.isOpen()) {
      closePresetMenu();
      XCSelect.close();
      return;
    }
    if (state.onboarding.open) {
      if (state.onboarding.padTestLock) {
        state.onboarding.padTestLock = false;
        renderOnboardingControls();
        return;
      }
      if (state.config?.onboardingCompleted) {
        closeOnboarding(false);
      }
      return;
    }
  }
  if (state.tour.open) {
    if (e.key === 'ArrowRight') { e.preventDefault(); $('tourNextBtn')?.click(); return; }
    if (e.key === 'ArrowLeft' && state.tour.step > 0) { e.preventDefault(); $('tourPrevBtn')?.click(); return; }
  }
  if (document.activeElement && (document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'SELECT')) {
    if (e.key === 'Escape') document.activeElement.blur();
    return;
  }
  if (e.key === 'ArrowRight') { e.preventDefault(); moveFocus('right'); }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); moveFocus('left'); }
  else if (e.key === 'ArrowDown') { e.preventDefault(); moveFocus('down'); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); moveFocus('up'); }
  else if (e.key === 'Enter' && state.focusedEl) { e.preventDefault(); state.focusedEl.click(); }
  else if (e.key === 'Escape' && state.screen !== 'home') {
    goScreen(state.playing.running ? 'playing' : 'home');
  }
});

// Gamepad polling loop (XInput / DualSense / Switch Pro)
(function initGamepadLoop() {
  const prev = {};
  let lastAxisTs = 0;

  function poll() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    const gp = pads && [...pads].find(p => p && p.connected);

    if (state.onboarding.open && state.onboarding.step === 4) {
      updateGamepadTester(gp || null);
    }

    if (gp) {
      if (!state.gamepadMode) {
        state.gamepadMode = true;
        const hint = $('inputHint');
        hint.innerHTML = `<span><kbd>D-Pad</kbd> Navegar</span><span><kbd>A</kbd> Confirmar</span><span><kbd>B</kbd> Voltar</span><span><kbd>LB</kbd><kbd>RB</kbd> Abas</span>`;
        hint.classList.add('on');
      }

      if (state.onboarding.open && state.onboarding.step === 4 && state.onboarding.padTestLock) {
        requestAnimationFrame(poll);
        return;
      }

      const btn = (i) => gp.buttons[i]?.pressed;
      const edge = (k, on) => { const hit = on && !prev[k]; prev[k] = !!on; return hit; };

      const now = performance.now();
      const ax = gp.axes[0] || 0, ay = gp.axes[1] || 0;
      if (edge('up', btn(12)) || (ay < -0.6 && now - lastAxisTs > 210)) { lastAxisTs = now; moveFocus('up'); }
      if (edge('down', btn(13)) || (ay > 0.6 && now - lastAxisTs > 210)) { lastAxisTs = now; moveFocus('down'); }
      if (edge('left', btn(14)) || (ax < -0.6 && now - lastAxisTs > 210)) { lastAxisTs = now; moveFocus('left'); }
      if (edge('right', btn(15)) || (ax > 0.6 && now - lastAxisTs > 210)) { lastAxisTs = now; moveFocus('right'); }
      if (edge('lb', btn(4))) cycleNav(-1);
      if (edge('rb', btn(5))) cycleNav(1);
      if (edge('a', btn(0))) {
        if (state.focusedEl) state.focusedEl.click();
        else moveFocus('down');
      }
      if (edge('b', btn(1))) {
        if (state.tour.open) closeTour(true);
        else if ($('helpAnchor')?.classList.contains('open')) closeHelpMenu();
        else if (state.onboarding.open) {
          if (state.onboarding.step > 0) setOnboardingStep(state.onboarding.step - 1);
          else if (state.config?.onboardingCompleted) closeOnboarding(false);
        } else if (state.screen !== 'home') {
          goScreen(state.playing.running ? 'playing' : 'home');
        }
      }
    }
    requestAnimationFrame(poll);
  }
  requestAnimationFrame(poll);
})();

// ==================== Host Message Receiver ====================
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (ev) => {
    let m;
    try { m = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data; }
    catch { return; }
    if (!m) return;

    if (m.t === 'state') {
      const prevRomfs = lastMediaRomfs;
      for (const k of ['config', 'install', 'hardware', 'recommendation', 'presets', 'launchPlan', 'bgmTracks', 'saves', 'mods', 'ini', 'steam', 'setup', 'playing', 'logs']) {
        if (m[k] !== undefined) state[k] = m[k];
      }
      if (m.playing?.running) {
        closeOnboarding(false);
        closeTour(false);
        if (state.screen !== 'playing' && state.screen !== 'settings') goScreen('playing');
      }
      renderAll();

      const nowRomfs = !!state.install?.hasRomfs;
      if (nowRomfs && !prevRomfs) {
        lastMediaRomfs = true;
        refreshRuntimeMedia();
      } else if (!nowRomfs) {
        lastMediaRomfs = false;
      }

      if (!state.onboarding.autoOpenedOnce && !state.onboarding.open && state.config && state.install && !state.playing.running) {
        state.onboarding.autoOpenedOnce = true;
        if (!state.config.onboardingCompleted || !state.install.isReadyToPlay) {
          openOnboarding(0);
        }
      }
    } else if (m.t === 'setupProgress') {
      state.setupProgress = m;
      renderSetup();
      if (state.onboarding.open) renderOnboardingInstall();
      if (m.isDone && !m.isError) refreshRuntimeMedia();
    } else if (m.t === 'go' && m.s) {
      goScreen(m.s);
    } else if (m.t === 'toast') {
      showToast(m.text, m.error, m.sfx || null);
    } else if (m.t === 'max') {
      $('winMax').innerHTML = m.on
        ? `<svg viewBox="0 0 12 12"><rect x="2" y="3.5" width="6.5" height="6.5" fill="none" stroke="currentColor" stroke-width="1.2"/><path d="M4 3.5V2h6v6H8.5" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`
        : `<svg viewBox="0 0 12 12"><rect x="2.2" y="2.2" width="7.6" height="7.6" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>`;
    }
  });
}

// Signal host that the page is ready
post({ t: 'ready' });
