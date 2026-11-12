// Settings page for the Dark Mount.
//
// This page only edits settings and draws a preview. The tray app (tray/) owns the
// keyboard's lights and keeps running the chosen effect - or layered preset - after the
// page is closed.

import { EFFECTS, CATEGORIES, PARAMS, PALETTES } from './effects.js';
import { MAX_LAYER_PRESETS, MAX_LAYERS, MAX_NAME, makeLayer, cleanPresets, freeName, buildRuns, runsNeed, renderRuns } from './layers.js';
import { FALLBACK_LAMPS } from './layout.js';
import { lampPosition, fallbackPosition, keySize, chassis, keyIdForCode, ISO_KEY } from './geometry.js';

const $ = (sel) => document.querySelector(sel);
const el = (tag, props = {}, ...kids) => {
  const n = Object.assign(document.createElement(tag), props);
  n.append(...kids);
  return n;
};

const hexToRgb = (h) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const effectById = Object.fromEntries(EFFECTS.map((e) => [e.id, e]));

// ------------------------------------------------------------------ settings
// S mirrors the tray app's settings. Page-only preferences live in localStorage.

const S = {
  enabled: true,
  effect: 'wave',
  params: Object.fromEntries(EFFECTS.map((e) => [e.id, structuredClone(e.defaults)])),
  brightness: 100,
  zones: { keys: true, bars: true, numpad: true },
  numpadSide: 1, // 1 = left of the main block, 2 = right
  paint: {},
  paintPresets: {}, // name -> saved painting
  handBackWhenSilent: true,
  gamma: true, // the tray app corrects colours for the keyboard's LEDs, so they look like this preview
  mode: 'effect', // 'effect' = one effect on every light, 'layers' = the selected layered preset
  layerPreset: 0, // index into layerPresets
  layerPresets: [], // see layers.js
};

const PREFS = 'dark-mount-studio-prefs';
const prefs = Object.assign({ paintColor: '#ff6a13' }, JSON.parse(localStorage.getItem(PREFS) || '{}'));
const savePrefs = () => localStorage.setItem(PREFS, JSON.stringify(prefs));

// ------------------------------------------------------------------ tray app

const tray = { reachable: false, status: null };
let pending = null;
let pushTimer = 0;
let pushing = false;

function adopt(settings) {
  if (!settings) return;
  if (typeof settings.enabled === 'boolean') S.enabled = settings.enabled;
  if (effectById[settings.effect]) S.effect = settings.effect;
  if (Number.isFinite(settings.brightness)) S.brightness = settings.brightness;
  if (settings.zones) Object.assign(S.zones, settings.zones);
  if (settings.numpadSide === 1 || settings.numpadSide === 2) S.numpadSide = settings.numpadSide;
  if (settings.paint) S.paint = { ...settings.paint };
  if (settings.paintPresets) S.paintPresets = structuredClone(settings.paintPresets);
  if (typeof settings.handBackWhenSilent === 'boolean') S.handBackWhenSilent = settings.handBackWhenSilent;
  if (typeof settings.gamma === 'boolean') S.gamma = settings.gamma;
  for (const e of EFFECTS) Object.assign(S.params[e.id], settings.params?.[e.id]);
  if (settings.mode === 'effect' || settings.mode === 'layers') S.mode = settings.mode;
  if (Array.isArray(settings.layerPresets)) S.layerPresets = cleanPresets(settings.layerPresets);
  if (Number.isInteger(settings.layerPreset)) S.layerPreset = settings.layerPreset;
  S.layerPreset = Math.max(0, Math.min(S.layerPreset, S.layerPresets.length - 1));
}

function useStatus(status) {
  tray.status = status ?? null;
  const fromTray = status?.bindings;
  if (Array.isArray(fromTray) && fromTray.length && fromTray.join() !== bindings.join()) {
    bindings = fromTray;
    rebuildLayout();
  }
}

async function pull(adoptSettings) {
  try {
    const asked = performance.now();
    const r = await fetch('/api/state', { cache: 'no-store' });
    const answered = performance.now();
    if (!r.ok) throw new Error(String(r.status));
    const j = await r.json();
    tray.reachable = true;
    syncClock(j.clock, asked, answered);
    useStatus(j.status);
    if (adoptSettings) {
      adopt(j.settings);
    } else if (!pending && !pushing && typeof j.settings?.enabled === 'boolean' && j.settings.enabled !== S.enabled) {
      S.enabled = j.settings.enabled; // toggled from the tray icon's menu
      $('#enabled').checked = S.enabled;
    }
  } catch {
    tray.reachable = false;
    tray.status = null;
  }
  describeTray();
}

// Queues a change for the tray app. `patch` has the same shape as the settings object.
function push(patch) {
  pending ??= {};
  for (const [k, v] of Object.entries(patch)) {
    if (k === 'params') {
      pending.params ??= {};
      for (const [id, p] of Object.entries(v)) pending.params[id] = { ...pending.params[id], ...p };
    } else if (k === 'zones') {
      pending.zones = { ...pending.zones, ...v };
    } else {
      pending[k] = v;
    }
  }
  clearTimeout(pushTimer);
  pushTimer = setTimeout(flush, 120);
}

async function flush() {
  if (pushing) {
    pushTimer = setTimeout(flush, 120);
    return;
  }
  const body = pending;
  pending = null;
  if (!body) return;
  pushing = true;
  try {
    const r = await fetch('/api/state', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error(String(r.status));
    tray.reachable = true;
    useStatus((await r.json()).status);
  } catch {
    tray.reachable = false;
  } finally {
    pushing = false;
    describeTray();
  }
}

function describeTray() {
  const s = $('#status');
  const st = tray.status;
  if (!tray.reachable) {
    s.textContent = 'The Dark Mount Studio tray app is not answering — start it to light the keyboard. The preview below still works.';
    s.className = 'status bad';
  } else if (!st?.keyboard) {
    s.textContent = 'Tray app running — keyboard not found. Plug it in and it will be picked up.';
    s.className = 'status bad';
  } else {
    s.textContent = `Tray app running — ${st.lamps} lights connected${S.enabled ? '' : ', effects switched off'}. You can close this tab; the effect keeps running.`;
    s.className = 'status ok';
  }
  const hint = $('#audio-state');
  if (hint) hint.textContent = audioStateText();
  const keysHint = $('#keys-state');
  if (keysHint) keysHint.textContent = keysStateText();
}

function keysStateText() {
  if (!tray.reachable) return 'The tray app is not running, so the keyboard itself will not react.';
  const k = tray.status?.keys;
  if (k === 'watching') return 'The tray app is watching the Dark Mount’s keys.';
  if (k === 'unavailable') return 'The tray app could not start watching key presses.';
  return S.enabled ? 'The tray app starts watching keys when one of these effects is selected.' : 'Effects are switched off.';
}

function audioStateText() {
  if (!tray.reachable) return 'The tray app is not running, so there is no sound to show.';
  const a = tray.status?.audio;
  if (a === 'listening') return 'Listening to what this PC is playing.';
  if (a === 'silent') return 'Nothing is playing right now.';
  return 'The tray app cannot capture this PC’s sound output.';
}

// -------------------------------------------------------------------- layout

let bindings = FALLBACK_LAMPS.map((l) => l[2]);
let lamps = [];
let box = { x0: 0, y0: 0, w: 1, h: 1 };
let frame = chassis(1);
let out = new Uint8ClampedArray(0);

function rebuildLayout() {
  const pts = bindings.map((binding, id) => {
    const fb = FALLBACK_LAMPS[id] ?? [100000, 0];
    const pos = lampPosition(binding, S.numpadSide) ?? fallbackPosition(fb[0], fb[1]);
    const [w, h] = keySize(binding);
    return { id, binding, x: pos.x, y: pos.y, bar: pos.bar, pad: pos.zone === 'numpad', w, h };
  });
  const x0 = Math.min(...pts.map((p) => p.x)), x1 = Math.max(...pts.map((p) => p.x));
  const y0 = Math.min(...pts.map((p) => p.y)), y1 = Math.max(...pts.map((p) => p.y));
  const w = x1 - x0 || 1, h = y1 - y0 || 1;
  lamps = pts.map((p) => ({
    ...p,
    u: (p.x - x0) / w,
    v: (p.y - y0) / h,
    cx: (p.x - (x0 + x1) / 2) / h,
    cy: (p.y - y0) / h - 0.5,
    rnd: Math.abs(Math.sin(p.id * 12.9898) * 43758.5453) % 1,
  }));
  box = { x0, y0, w, h };
  frame = chassis(S.numpadSide);
  out = new Uint8ClampedArray(lamps.length * 3);
  syncRuns();
  resizePreview();
}

// ------------------------------------------------------------------- preview

const canvas = $('#preview');
const pctx = canvas.getContext('2d');
const MARGIN_U = 0.55; // free space around the board, in key units
let scale = 40; // canvas pixels per key unit

function resizePreview() {
  const cssW = canvas.clientWidth || 800;
  const dpr = window.devicePixelRatio || 1;
  const unitsW = box.w + 2 * MARGIN_U, unitsH = box.h + 2 * MARGIN_U;
  canvas.style.height = `${(cssW * unitsH) / unitsW}px`;
  canvas.width = Math.round(cssW * dpr);
  canvas.height = Math.round((cssW * dpr * unitsH) / unitsW);
  scale = canvas.width / unitsW;
}

const px = (x) => (x - box.x0 + MARGIN_U) * scale;
const py = (y) => (y - box.y0 + MARGIN_U) * scale;

function rect(x0, y0, x1, y1, radius) {
  pctx.beginPath();
  pctx.roundRect(px(x0), py(y0), (x1 - x0) * scale, (y1 - y0) * scale, radius * scale);
}

function drawPreview() {
  pctx.fillStyle = '#0b0c0d';
  pctx.fillRect(0, 0, canvas.width, canvas.height);

  // Cases, slightly larger than the ring of edge lights that runs around them.
  pctx.lineWidth = Math.max(1, scale * 0.03);
  for (const c of [frame.pad, frame.main]) {
    rect(c.x0 - 0.3, c.y0 - 0.3, c.x1 + 0.3, c.y1 + 0.3, 0.3);
    pctx.fillStyle = '#131517';
    pctx.fill();
    pctx.strokeStyle = '#2a2d32';
    pctx.stroke();
  }
  for (const d of frame.displayKeys) {
    rect(d.x - d.size / 2, d.y - d.size / 2, d.x + d.size / 2, d.y + d.size / 2, 0.14);
    pctx.fillStyle = '#050506';
    pctx.fill();
    pctx.strokeStyle = '#2a2d32';
    pctx.stroke();
  }

  const GAP = 0.06;
  for (let i = 0; i < lamps.length; i++) {
    const l = lamps[i];
    if (l.binding === ISO_KEY) continue; // hidden under the left Shift on this board
    // A light that is off is drawn black, as it is on the keyboard; the outline keeps the layout readable.
    pctx.fillStyle = `rgb(${out[i * 3]},${out[i * 3 + 1]},${out[i * 3 + 2]})`;
    if (l.bar) rect(l.x - 0.17, l.y - 0.17, l.x + 0.17, l.y + 0.17, 0.07);
    else rect(l.x - l.w / 2 + GAP, l.y - l.h / 2 + GAP, l.x + l.w / 2 - GAP, l.y + l.h / 2 - GAP, 0.13);
    pctx.fill();
    pctx.lineWidth = Math.max(1, scale * 0.03);
    pctx.strokeStyle = '#2a2d32';
    pctx.stroke();
    if (marked.has(l.id)) {
      // The selected layer's lights: a dark line under a white one shows on any colour.
      pctx.lineWidth = scale * 0.11;
      pctx.strokeStyle = '#0b0c0d';
      pctx.stroke();
      pctx.lineWidth = scale * 0.05;
      pctx.strokeStyle = '#ffffff';
      pctx.stroke();
    }
  }
}

// Finds the light under a pointer position (canvas pixels).
function lampAt(cx, cy) {
  const x = cx / scale + box.x0 - MARGIN_U, y = cy / scale + box.y0 - MARGIN_U;
  let best = null, bestD = 0.45;
  for (const l of lamps) {
    if (l.binding === ISO_KEY) continue;
    if (!l.bar && Math.abs(x - l.x) <= l.w / 2 && Math.abs(y - l.y) <= l.h / 2) return l;
    const d = Math.hypot(x - l.x, y - l.y);
    if (l.bar && d < bestD) {
      bestD = d;
      best = l;
    }
  }
  return best;
}

// ------------------------------------------------------------- preview loop

const io = { levels: null, beat: 0, paint: {}, keys: { events: [] } };
let scratch = {};
let palette = [[255, 255, 255]];
let lastTick = performance.now();

// The effects' time in seconds. It runs on the tray app's clock, so the preview shows the same
// moment of an animation as the keyboard does; until the tray app answers it starts at 0.
let clockOffset = -performance.now() / 1000; // effect time = performance.now() / 1000 + clockOffset
const effectClock = (now = performance.now()) => now / 1000 + clockOffset;

// trayNow: the tray app's clock as it answered a request sent at `asked` and received at `answered`.
function syncClock(trayNow, asked, answered) {
  if (!Number.isFinite(trayNow)) return;
  const offset = trayNow - (asked + answered) / 2000;
  const jump = Math.abs(offset - clockOffset);
  // Small differences are measuring noise; a slow answer says little about the time.
  if (jump < 0.01 || (jump < 0.5 && answered - asked > 30)) return;
  clockOffset = offset;
  if (jump < 0.5) return;
  // The time jumped (first answer, or the tray app was restarted): forget what was timed on the old clock.
  scratch = {};
  for (const run of runs) run.st = {};
  io.keys.events = [];
}

function syncPaint() {
  io.paint = {};
  for (const [id, hex] of Object.entries(S.paint)) io.paint[id] = hexToRgb(hex);
}

// With an effect's "Random colours" on, the palette carries a seed (see effects.js). The tray
// app draws its own seed, so the keyboard's colours differ from the preview's - both are random.
// (They do change at the same moments: both run on the tray app's clock and beat count.)
let paletteSeed = Math.floor(Math.random() * 1e6);

function refreshPalette() {
  const p = S.params[S.effect];
  palette = (p.palette?.length ? p.palette : ['#ffffff']).map(hexToRgb);
  if (p.random && !effectById[S.effect].noPalette) palette.seed = paletteSeed;
}

function loop(now) {
  requestAnimationFrame(loop);
  if (now - lastTick < 30 || document.hidden) return;
  const dt = Math.min(0.25, (now - lastTick) / 1000);
  lastTick = now;
  const t = effectClock(now);
  if (activePreset()) {
    renderRuns(runs, { t, dt, out, io });
  } else {
    const eff = effectById[S.effect];
    eff.frame({ t, dt, lamps, out, p: S.params[eff.id], pal: palette, st: scratch, io });
  }
  const bright = S.brightness / 100;
  for (let i = 0; i < lamps.length; i++) {
    const l = lamps[i];
    const lit = (l.bar ? S.zones.bars : S.zones.keys) && (!l.pad || S.zones.numpad);
    const k = lit ? bright : 0;
    if (k === 1) continue;
    out[i * 3] *= k;
    out[i * 3 + 1] *= k;
    out[i * 3 + 2] *= k;
  }
  drawPreview();
}

// The audio effects' preview uses the levels the tray app is hearing.
let levelsBusy = false;
async function pollLevels() {
  if (levelsBusy || document.hidden || !tray.reachable || !needs('audio')) return;
  levelsBusy = true;
  try {
    const r = await fetch('/api/levels', { cache: 'no-store' });
    if (r.ok) {
      const j = await r.json();
      io.levels = Float32Array.from(j.levels ?? []);
      io.beat = Number.isInteger(j.beat) ? j.beat : 0; // the tray app counts the beats it hears
    }
  } catch {
    io.levels = null;
  } finally {
    levelsBusy = false;
  }
}

// ---------------------------------------------------------------- effects UI

let shownCategory = null; // 'all' or a category id; starts on the selected effect's category

function renderEffects() {
  shownCategory ??= CATEGORIES.find((c) => c.effects.includes(S.effect))?.id ?? 'all';

  const chip = (id, label, count) => {
    const b = el('button', { type: 'button', className: 'chip' }, el('span', { textContent: label }), el('small', { textContent: count }));
    b.setAttribute('aria-pressed', String(id === shownCategory));
    b.addEventListener('click', () => {
      shownCategory = id;
      renderEffects();
    });
    return b;
  };
  $('#cats').replaceChildren(
    chip('all', 'All', EFFECTS.length),
    ...CATEGORIES.map((c) => chip(c.id, c.name, c.effects.length)),
  );

  const effectButton = (e) => {
    const b = el('button', { type: 'button' }, el('span', { textContent: e.name }), el('small', { textContent: e.desc }));
    b.setAttribute('aria-pressed', String(e.id === S.effect));
    b.addEventListener('click', () => selectEffect(e.id));
    return b;
  };
  const groups = shownCategory === 'all' ? CATEGORIES : CATEGORIES.filter((c) => c.id === shownCategory);
  $('#effects').replaceChildren(
    ...groups.flatMap((c) => [
      ...(shownCategory === 'all' ? [el('h3', { textContent: c.name })] : []),
      el('div', { className: 'effect-list' }, ...c.effects.filter((id) => effectById[id]).map((id) => effectButton(effectById[id]))),
    ]),
  );
}

function selectEffect(id) {
  S.effect = id;
  scratch = {};
  io.levels = null;
  paletteSeed = Math.floor(Math.random() * 1e6);
  refreshPalette();
  push({ effect: id });
  io.keys.events = [];
  if (effectById[id].needs) refreshStatusSoon();
  renderEffects();
  renderParams();
  updateCanvasMode();
}

// The sliders, colours and "Random colours" switch of one effect, editing `p` in place.
// changed(what) runs after every edit (what = 'random' when that switch was flipped);
// again() redraws the panel these rows are in.
function paramRows(eff, p, changed, again) {
  const rows = [];

  for (const key of eff.params) {
    const def = PARAMS[key];
    const output = el('output');
    const show = () => (output.textContent = `${p[key]}${def.unit ?? ''}`);
    const input = el('input', { type: 'range', min: def.min, max: def.max, step: def.step, value: p[key] });
    input.addEventListener('input', () => {
      p[key] = Number(input.value);
      show();
      changed();
    });
    show();
    rows.push(el('label', { className: 'row' }, el('span', { textContent: def.label }), input, output));
  }

  if (!eff.noPalette) {
    const swatches = el('div', { className: 'swatches' });
    const resized = () => {
      changed();
      redraw();
    };
    const redraw = () => {
      swatches.replaceChildren(
        ...p.palette.map((hex, i) => {
          const c = el('input', { type: 'color', value: hex, title: `Colour ${i + 1}` });
          c.addEventListener('input', () => {
            p.palette[i] = c.value;
            changed();
          });
          return c;
        }),
        el('button', {
          type: 'button', className: 'small', textContent: '+', title: 'Add a colour', disabled: p.palette.length >= 8,
          onclick: () => { p.palette.push(p.palette[p.palette.length - 1]); resized(); },
        }),
        el('button', {
          type: 'button', className: 'small', textContent: '−', title: 'Remove the last colour', disabled: p.palette.length <= 1,
          onclick: () => { p.palette.pop(); resized(); },
        }),
      );
    };
    const preset = el('select', { title: 'Colour presets' }, el('option', { value: '', textContent: 'Presets…' }),
      ...Object.keys(PALETTES).map((n) => el('option', { value: n, textContent: n })));
    preset.addEventListener('change', () => {
      if (!preset.value) return;
      p.palette = [...PALETTES[preset.value]];
      preset.value = '';
      resized();
    });
    redraw();
    rows.push(el('div', { className: 'line' }, el('span', { textContent: 'Colours' }), swatches, preset));

    const random = el('input', { type: 'checkbox', checked: !!p.random });
    random.addEventListener('change', () => {
      p.random = random.checked;
      changed('random');
      again();
    });
    rows.push(el('label', { className: 'line' }, random, el('span', { textContent: 'Random colours' })));
    if (p.random) {
      rows.push(el('p', {
        className: 'hint',
        textContent: 'Every key press, beat, band or sweep gets a new bright colour. The colours above now only decide how bright each part is, so dark ones keep their gaps dark.',
      }));
    }
  }
  return rows;
}

function renderParams() {
  const eff = effectById[S.effect];
  const p = S.params[eff.id];
  $('#params-title').textContent = `${eff.name} settings`;
  const rows = paramRows(eff, p, (what) => {
    if (what === 'random') paletteSeed = Math.floor(Math.random() * 1e6);
    refreshPalette();
    push({ params: { [eff.id]: { ...p } } });
  }, renderParams);

  if (eff.needs === 'audio') {
    const box = el('input', { type: 'checkbox', checked: S.handBackWhenSilent });
    box.addEventListener('change', () => {
      S.handBackWhenSilent = box.checked;
      push({ handBackWhenSilent: box.checked });
    });
    rows.push(el('label', { className: 'line' }, box, el('span', { textContent: 'When nothing is playing, show the keyboard’s own lighting' })));
    rows.push(el('p', { className: 'hint', id: 'audio-state', textContent: audioStateText() }));
  }

  if (eff.needs === 'paint') {
    const color = el('input', { type: 'color', value: prefs.paintColor });
    color.addEventListener('input', () => {
      prefs.paintColor = color.value;
      savePrefs();
    });
    const fill = el('button', { type: 'button', textContent: 'Fill all' });
    fill.addEventListener('click', () => {
      for (const l of lamps) S.paint[l.id] = prefs.paintColor;
      paintChanged();
    });
    const clear = el('button', { type: 'button', textContent: 'Clear all' });
    clear.addEventListener('click', () => {
      S.paint = {};
      paintChanged();
    });
    rows.push(el('div', { className: 'line' }, el('span', { textContent: 'Brush' }), color, fill, clear));
    rows.push(el('p', { className: 'hint', textContent: 'Click or drag over the keyboard to paint. Right-click or Shift-drag erases.' }));

    const name = el('input', { type: 'text', placeholder: 'Name this painting', maxLength: 40 });
    const saveBtn = el('button', { type: 'button', textContent: 'Save' });
    const saveIt = () => {
      const n = name.value.trim();
      if (!n) return name.focus();
      if (!S.paintPresets[n] && Object.keys(S.paintPresets).length >= 50) return;
      S.paintPresets[n] = { ...S.paint };
      push({ paintPresets: S.paintPresets });
      renderParams();
    };
    saveBtn.addEventListener('click', saveIt);
    name.addEventListener('keydown', (ev) => ev.key === 'Enter' && saveIt());
    rows.push(el('div', { className: 'line' }, el('span', { textContent: 'Presets' }), name, saveBtn));

    const names = Object.keys(S.paintPresets).sort((a, b) => a.localeCompare(b));
    if (names.length) {
      rows.push(el('div', { className: 'presets' }, ...names.map((n) => {
        const load = el('button', { type: 'button', className: 'small', textContent: n, title: `Load “${n}”` });
        load.addEventListener('click', () => {
          S.paint = { ...S.paintPresets[n] };
          name.value = n;
          paintChanged();
        });
        const del = el('button', { type: 'button', className: 'small', textContent: '×', title: `Delete “${n}”` });
        del.setAttribute('aria-label', `Delete preset ${n}`);
        del.addEventListener('click', () => {
          delete S.paintPresets[n];
          push({ paintPresets: S.paintPresets });
          renderParams();
        });
        return el('span', { className: 'preset' }, load, del);
      })));
    } else {
      rows.push(el('p', { className: 'hint', textContent: 'No saved paintings yet. Saving under an existing name replaces it.' }));
    }
  }

  if (eff.needs === 'keys') {
    rows.push(el('p', { className: 'hint', textContent: 'Try it here: type, or click keys in the picture above. On the real keyboard the tray app reacts to presses on the Dark Mount only; nothing you type is stored or sent anywhere.' }));
    rows.push(el('p', { className: 'hint', id: 'keys-state', textContent: keysStateText() }));
  }

  if (!rows.length) rows.push(el('p', { className: 'hint', textContent: 'No settings for this effect.' }));
  $('#params').replaceChildren(...rows);
}

// -------------------------------------------------------------------- layers
// A layered preset runs a different effect on each group of lights (see layers.js). In this
// mode the keyboard picture is used to say which lights belong to the selected layer.

let selectedLayer = 0; // index into the selected preset's layers
let runs = []; // the selected preset, ready to draw
let marked = new Set(); // ids of the selected layer's lights, outlined in the picture

const activePreset = () => (S.mode === 'layers' ? S.layerPresets[S.layerPreset] ?? null : null);
const currentLayer = () => activePreset()?.layers[selectedLayer] ?? null;
// Whether anything that is being shown needs 'audio' or 'keys'.
const needs = (what) => (activePreset() ? runsNeed(runs, what) : effectById[S.effect].needs === what);
const refreshStatusSoon = () => setTimeout(() => pull(false), 900); // sound capture and key watching start on demand

function syncRuns() {
  const preset = activePreset();
  runs = preset ? buildRuns(preset, lamps, runs) : [];
  marked = new Set(currentLayer()?.lamps ?? []);
}

// Call after any change to the layered presets.
function layersChanged() {
  syncRuns();
  push({ mode: S.mode, layerPreset: S.layerPreset, layerPresets: S.layerPresets });
}

function renderMode() {
  const layers = S.mode === 'layers';
  for (const b of document.querySelectorAll('[data-mode]')) b.setAttribute('aria-pressed', String(b.dataset.mode === S.mode));
  $('#mode-hint').textContent = layers ? 'A different effect on each group of lights.' : 'The same effect on every light.';
  $('#single').hidden = layers;
  $('#layers').hidden = !layers;
  if (layers) {
    renderLayers();
    renderLayerSettings();
  } else {
    renderEffects();
    renderParams();
  }
  updateCanvasMode();
}

function setMode(mode) {
  if (S.mode === mode) return;
  S.mode = mode;
  io.keys.events = [];
  io.levels = null;
  syncRuns();
  push({ mode });
  refreshStatusSoon();
  renderMode();
}

// The covered ISO light follows the Shift key it sits under.
const withCovered = (lamp) =>
  (lamp.binding === 42 ? [lamp.id, ...lamps.filter((l) => l.binding === ISO_KEY).map((l) => l.id)] : [lamp.id]);

const lightsText = (layer) => {
  const n = layer.lamps.filter((id) => lamps[id]?.binding !== ISO_KEY).length;
  return n === 1 ? '1 light' : `${n} lights`;
};

// Whole groups of lights that can be added to a layer in one go.
const LIGHT_GROUPS = [
  { name: 'Main keys', has: (l) => !l.bar && !l.pad },
  { name: 'F row', has: (l) => !l.bar && !l.pad && l.binding >= 87 && l.binding <= 102 },
  { name: 'Numpad keys', has: (l) => !l.bar && l.pad },
  { name: 'Main edge lights', has: (l) => l.bar && !l.pad },
  { name: 'Numpad edge lights', has: (l) => l.bar && l.pad },
  { name: 'Everything not in a layer yet', has: (l, taken) => !taken.has(l.id) },
];

// Puts lights into a layer, taking them out of the preset's other layers (a light is in one
// layer only), or with `remove` takes them out of the layer. Returns whether anything changed.
function assign(layer, ids, remove = false) {
  const mine = new Set(layer.lamps);
  const before = mine.size;
  for (const id of ids) {
    if (remove) mine.delete(id);
    else mine.add(id);
  }
  if (mine.size === before) return false;
  layer.lamps = [...mine].sort((a, b) => a - b);
  if (!remove) {
    const taken = new Set(ids);
    for (const other of activePreset().layers) {
      if (other !== layer) other.lamps = other.lamps.filter((id) => !taken.has(id));
    }
  }
  return true;
}

// After the lights of a layer changed: redraw what shows the counts, keep the settings panel as it is.
function lightsChanged() {
  layersChanged();
  renderLayers();
  const count = $('#layer-lights');
  if (count && currentLayer()) count.textContent = lightsText(currentLayer());
}

function assignAt(ev) {
  const layer = currentLayer();
  if (!layer) return;
  const ids = strokeLamps(ev).flatMap(withCovered);
  if (ids.length && assign(layer, ids, painting === 2)) lightsChanged();
}

function selectLayer(k) {
  selectedLayer = k;
  syncRuns();
  renderMode();
}

function selectPreset(n) {
  S.layerPreset = n;
  selectedLayer = 0;
  io.keys.events = [];
  layersChanged();
  refreshStatusSoon();
  renderMode();
}

// A new layer starts with the effect (and its settings) chosen under "One effect".
function addLayer(preset) {
  const effect = S.effect === 'paint' ? 'wave' : S.effect;
  preset.layers.push(makeLayer(freeName(preset.layers, 'Layer'), effect, S.params[effect]));
  selectedLayer = preset.layers.length - 1;
}

function newPreset() {
  const preset = { name: freeName(S.layerPresets, 'Layered preset'), layers: [] };
  addLayer(preset);
  S.layerPresets.push(preset);
  selectPreset(S.layerPresets.length - 1);
}

// Left column: the presets, and the layers of the selected one.
function renderLayers() {
  const preset = activePreset();
  const chips = S.layerPresets.map((p, n) => {
    const b = el('button', { type: 'button', className: 'chip' }, el('span', { textContent: p.name }));
    b.setAttribute('aria-pressed', String(n === S.layerPreset));
    b.addEventListener('click', () => selectPreset(n));
    return b;
  });
  const create = el('button', { type: 'button', className: 'chip', textContent: '+ New preset', disabled: S.layerPresets.length >= MAX_LAYER_PRESETS });
  create.addEventListener('click', newPreset);
  const kids = [el('h2', { textContent: 'Layered preset' }), el('div', { className: 'cats' }, ...chips, create)];

  if (!preset) {
    kids.push(el('p', {
      className: 'hint',
      textContent: 'A layered preset shows a different effect on each group of lights — for example sound on the numpad, a rainbow on the main keys, a scanner on the F row and a breathing edge. Until you create one, the effect chosen under “One effect” keeps running.',
    }));
    $('#layers').replaceChildren(...kids);
    return;
  }

  const name = el('input', { type: 'text', value: preset.name, maxLength: MAX_NAME, title: 'Name of this preset' });
  name.setAttribute('aria-label', 'Name of this preset');
  name.addEventListener('input', () => {
    preset.name = name.value;
    chips[S.layerPreset].firstChild.textContent = name.value;
    layersChanged();
  });
  name.addEventListener('change', () => {
    // Only the texts are touched here: rebuilding the column now would swallow the click that
    // took the focus out of this box.
    preset.name = name.value.trim() || freeName(S.layerPresets.filter((p) => p !== preset), 'Layered preset');
    name.value = preset.name;
    chips[S.layerPreset].firstChild.textContent = preset.name;
    layersChanged();
  });
  const drop = el('button', { type: 'button', textContent: 'Delete preset' });
  let armed = 0;
  drop.addEventListener('click', () => {
    if (!armed) {
      // Asks once more: a preset can be a lot of work.
      drop.textContent = 'Click again to delete';
      armed = setTimeout(() => {
        armed = 0;
        drop.textContent = 'Delete preset';
      }, 3000);
      return;
    }
    clearTimeout(armed);
    S.layerPresets.splice(S.layerPreset, 1);
    selectPreset(Math.max(0, Math.min(S.layerPreset, S.layerPresets.length - 1)));
  });
  kids.push(el('div', { className: 'line' }, name, drop));

  kids.push(el('h2', { textContent: 'Layers' }));
  if (preset.layers.length) {
    kids.push(el('div', { className: 'layer-list' }, ...preset.layers.map((layer, k) => {
      const pick = el('button', { type: 'button' },
        el('span', { textContent: layer.name }),
        el('small', { textContent: `${effectById[layer.effect].name} · ${lightsText(layer)}` }));
      pick.setAttribute('aria-pressed', String(k === selectedLayer));
      pick.addEventListener('click', () => selectLayer(k));
      const remove = el('button', { type: 'button', textContent: '×', title: 'Delete this layer' });
      remove.setAttribute('aria-label', `Delete layer ${k + 1}`);
      remove.addEventListener('click', () => {
        preset.layers.splice(k, 1);
        if (selectedLayer > k || selectedLayer >= preset.layers.length) selectedLayer = Math.max(0, selectedLayer - 1);
        layersChanged();
        refreshStatusSoon();
        renderMode();
      });
      return el('div', { className: 'layer' }, pick, remove);
    })));
  }
  const add = el('button', { type: 'button', textContent: '+ Add layer', disabled: preset.layers.length >= MAX_LAYERS });
  add.addEventListener('click', () => {
    addLayer(preset);
    layersChanged();
    renderMode();
  });
  kids.push(el('div', { className: 'line' }, add));
  kids.push(el('p', { className: 'hint', textContent: 'Lights that are in no layer stay off.' }));
  $('#layers').replaceChildren(...kids);
}

// Right column: name, lights and effect of the selected layer.
function renderLayerSettings() {
  const preset = activePreset();
  const layer = currentLayer();
  $('#params-title').textContent = layer ? 'Layer settings' : 'Settings';
  if (!layer) {
    const text = preset ? 'This preset has no layers yet — add one on the left.' : 'Create a layered preset on the left to start.';
    $('#params').replaceChildren(el('p', { className: 'hint', textContent: text }));
    return;
  }
  const eff = effectById[layer.effect];
  const label = (text) => el('span', { className: 'label', textContent: text });
  const rows = [];

  const name = el('input', { type: 'text', value: layer.name, maxLength: MAX_NAME });
  name.setAttribute('aria-label', 'Name of this layer');
  // Shows the name in the list on the left without rebuilding it (see the preset's name box).
  const renamed = () => {
    const shown = document.querySelector('#layers .layer [aria-pressed="true"] span');
    if (shown) shown.textContent = layer.name;
    layersChanged();
  };
  name.addEventListener('input', () => {
    layer.name = name.value;
    renamed();
  });
  name.addEventListener('change', () => {
    layer.name = name.value.trim() || freeName(preset.layers.filter((l) => l !== layer), 'Layer');
    name.value = layer.name;
    renamed();
  });
  rows.push(el('div', { className: 'line' }, label('Name'), name));

  const group = el('select', { title: 'Add a whole group of lights to this layer' },
    el('option', { value: '', textContent: 'Add a group…' }),
    ...LIGHT_GROUPS.map((g, n) => el('option', { value: String(n), textContent: g.name })));
  group.addEventListener('change', () => {
    const picked = LIGHT_GROUPS[group.value];
    group.value = '';
    if (!picked) return;
    const taken = new Set(preset.layers.flatMap((l) => l.lamps));
    if (assign(layer, lamps.filter((l) => picked.has(l, taken)).map((l) => l.id))) lightsChanged();
  });
  const none = el('button', { type: 'button', textContent: 'Remove all' });
  none.addEventListener('click', () => {
    if (assign(layer, layer.lamps, true)) lightsChanged();
  });
  rows.push(el('div', { className: 'line' }, label('Lights'), el('strong', { id: 'layer-lights', textContent: lightsText(layer) }), group, none));
  rows.push(el('p', {
    className: 'hint',
    textContent: 'Click or drag over the keyboard above to add lights to this layer; right-click or Shift-drag takes them out. Its lights are outlined in white. A light is in one layer only, so adding it here takes it from the layer it was in.',
  }));

  const pick = el('select', { title: 'Effect of this layer' }, ...CATEGORIES.map((c) =>
    el('optgroup', { label: c.name }, ...c.effects.filter((id) => effectById[id] && (id !== 'paint' || id === layer.effect))
      .map((id) => el('option', { value: id, textContent: effectById[id].name })))));
  pick.value = layer.effect;
  pick.addEventListener('change', () => {
    // The new effect starts with the settings it has under "One effect".
    Object.assign(layer, makeLayer(layer.name, pick.value, S.params[pick.value], layer.lamps));
    layersChanged();
    refreshStatusSoon();
    renderMode();
  });
  rows.push(el('div', { className: 'line' }, label('Effect'), pick));
  rows.push(el('p', { className: 'hint', textContent: `${eff.desc}. It plays on this layer’s lights as if they were the whole keyboard.` }));

  rows.push(...paramRows(eff, layer.params, layersChanged, renderLayerSettings));

  if (eff.needs === 'audio') rows.push(el('p', { className: 'hint', id: 'audio-state', textContent: audioStateText() }));
  if (eff.needs === 'keys') {
    rows.push(el('p', { className: 'hint', textContent: 'Reacts to the keys that are in this layer. Try it here by typing. On the real keyboard the tray app reacts to presses on the Dark Mount only; nothing you type is stored or sent anywhere.' }));
    rows.push(el('p', { className: 'hint', id: 'keys-state', textContent: keysStateText() }));
  }
  $('#params').replaceChildren(...rows);
}

function paintChanged() {
  syncPaint();
  push({ paint: S.paint });
}

let painting = 0; // 0 = no, 1 = paint, 2 = erase
let lastPoint = null; // where the pointer was at the previous event of this stroke (canvas pixels)

// The lights the pointer passed over since the previous event of this stroke. The path in
// between is walked in small steps, so a quick drag skips no key.
function strokeLamps(ev) {
  const to = canvasPoint(ev), from = lastPoint ?? to;
  lastPoint = to;
  const steps = Math.max(1, Math.ceil(Math.hypot(to[0] - from[0], to[1] - from[1]) / (scale * 0.3)));
  const found = new Set();
  for (let k = 1; k <= steps; k++) {
    const lamp = lampAt(from[0] + ((to[0] - from[0]) * k) / steps, from[1] + ((to[1] - from[1]) * k) / steps);
    if (lamp) found.add(lamp);
  }
  return [...found];
}

function paintAt(ev) {
  const ids = strokeLamps(ev).flatMap(withCovered);
  if (!ids.length) return;
  for (const id of ids) {
    if (painting === 2) delete S.paint[id];
    else S.paint[id] = prefs.paintColor;
  }
  paintChanged();
}
// Key presses for previewing the key-reactive effects (the list is described at keyEvents
// in effects.js). They come from typing on this page and from clicking keys in the picture;
// the real keyboard's presses are handled inside the tray app and never reach this page.
let pressCount = 0;
let clickedLamp = -1;
const usesKeys = () => needs('keys');
const lampForKeyId = (id) => (id == null ? -1 : lamps.findIndex((l) => l.binding === id));

function pressLamp(i) {
  if (io.keys.events.some((e) => e.i === i && e.r < 0)) return; // already held (auto-repeat)
  const now = effectClock();
  io.keys.events = io.keys.events.filter((e) => e.r < 0 || now - e.r < 30).slice(-255);
  io.keys.events.push({ i, t: now, r: -1, n: pressCount++ });
}

function releaseLamp(i) {
  const now = effectClock();
  for (const e of io.keys.events) if (e.r < 0 && (i < 0 || e.i === i)) e.r = now;
}

window.addEventListener('keydown', (ev) => {
  if (!usesKeys() || ev.target.closest?.('input, select, textarea')) return;
  const i = lampForKeyId(keyIdForCode(ev.code));
  if (i < 0) return;
  if (ev.code === 'Space' || ev.code.startsWith('Arrow')) ev.preventDefault(); // keep the page from scrolling
  pressLamp(i);
});
window.addEventListener('keyup', (ev) => {
  const i = lampForKeyId(keyIdForCode(ev.code));
  if (i >= 0) releaseLamp(i);
});
window.addEventListener('blur', () => releaseLamp(-1));

function canvasPoint(ev) {
  const r = canvas.getBoundingClientRect();
  const k = canvas.width / r.width;
  return [(ev.clientX - r.left) * k, (ev.clientY - r.top) * k];
}

canvas.addEventListener('pointerdown', (ev) => {
  if (S.mode === 'layers') {
    // Layers: the picture says which lights the selected layer has.
    if (!currentLayer()) return;
    painting = ev.button === 2 || ev.shiftKey ? 2 : 1;
    canvas.setPointerCapture(ev.pointerId);
    assignAt(ev);
    return;
  }
  if (usesKeys()) {
    const lamp = lampAt(...canvasPoint(ev));
    if (!lamp || lamp.bar) return;
    clickedLamp = lamp.id;
    canvas.setPointerCapture(ev.pointerId);
    pressLamp(clickedLamp);
    return;
  }
  if (S.effect !== 'paint') return;
  painting = ev.button === 2 || ev.shiftKey ? 2 : 1;
  canvas.setPointerCapture(ev.pointerId);
  paintAt(ev);
});
canvas.addEventListener('pointermove', (ev) => painting && (S.mode === 'layers' ? assignAt(ev) : paintAt(ev)));
const pointerDone = () => {
  painting = 0;
  lastPoint = null;
  if (clickedLamp >= 0) releaseLamp(clickedLamp);
  clickedLamp = -1;
};
canvas.addEventListener('pointerup', pointerDone);
canvas.addEventListener('pointercancel', pointerDone);
canvas.addEventListener('contextmenu', (ev) => (S.mode === 'layers' || S.effect === 'paint') && ev.preventDefault());

function updateCanvasMode() {
  const layers = S.mode === 'layers';
  canvas.classList.toggle('painting', layers ? !!currentLayer() : S.effect === 'paint');
  canvas.classList.toggle('pressable', !layers && usesKeys());
}

function syncControls() {
  $('#enabled').checked = S.enabled;
  $('#brightness').value = S.brightness;
  $('#brightness-out').textContent = `${S.brightness}%`;
  $('#gamma').checked = S.gamma;
  $('#numpad-side').value = String(S.numpadSide);
  for (const boxEl of document.querySelectorAll('[data-zone]')) boxEl.checked = S.zones[boxEl.dataset.zone];
  updateCanvasMode();
}

// ---------------------------------------------------------------------- init

function initControls() {
  $('#enabled').addEventListener('change', (ev) => {
    S.enabled = ev.target.checked;
    push({ enabled: S.enabled });
    describeTray();
  });
  $('#brightness').addEventListener('input', (ev) => {
    S.brightness = Number(ev.target.value);
    $('#brightness-out').textContent = `${S.brightness}%`;
    push({ brightness: S.brightness });
  });
  $('#gamma').addEventListener('change', (ev) => {
    S.gamma = ev.target.checked;
    push({ gamma: S.gamma });
  });
  $('#numpad-side').addEventListener('change', (ev) => {
    S.numpadSide = Number(ev.target.value);
    push({ numpadSide: S.numpadSide });
    rebuildLayout();
  });
  for (const boxEl of document.querySelectorAll('[data-zone]')) {
    boxEl.addEventListener('change', () => {
      S.zones[boxEl.dataset.zone] = boxEl.checked;
      push({ zones: { [boxEl.dataset.zone]: boxEl.checked } });
    });
  }

  for (const b of document.querySelectorAll('[data-mode]')) b.addEventListener('click', () => setMode(b.dataset.mode));

  new ResizeObserver(() => resizePreview()).observe(canvas.parentElement);
}

initControls();
rebuildLayout();
await pull(true);
syncPaint();
refreshPalette();
syncControls();
rebuildLayout();
renderMode();
requestAnimationFrame(loop);
setInterval(() => pull(false), 3000);
setInterval(pollLevels, 70);
