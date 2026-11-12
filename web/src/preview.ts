// The keyboard picture: draws what the selected effect - or layered preset - looks like, and
// takes the pointer and key presses that paint lights, pick a layer's lights and try out the
// typing effects. The real keyboard is not involved: the tray app renders for it on its own.

import { assignLamps, paintLamps } from './actions.ts';
import { effectClock, onClockJump } from './clock.ts';
import { boardFor, dim } from './lib/board.ts';
import type { BoardLamp } from './lib/board.ts';
import { effectOf, hexToRgb } from './lib/effects.ts';
import type { Io, Need, Palette, Params, Scratch } from './lib/effects.ts';
import { ISO_KEY, keyIdForCode } from './lib/geometry.ts';
import { buildRuns, renderRuns, runsNeed } from './lib/layers.ts';
import type { Run } from './lib/layers.ts';
import { activePreset } from './settings.ts';
import { getState, subscribe } from './store.ts';

const MARGIN_U = 0.55; // free space around the board, in key units
const KEY_GAP = 0.06; // between neighbouring keycaps, in key units
const FRAME_MS = 30;
const LEVELS_MS = 70;

const newSeed = () => Math.floor(Math.random() * 1e6);

/** Starts drawing on `canvas`. Returns the function that stops it again. */
export function attachPreview(canvas: HTMLCanvasElement): () => void {
  const pctx = canvas.getContext('2d')!;
  let seen = getState();
  let board = boardFor(seen.bindings, seen.settings.numpadSide);
  let out = new Uint8ClampedArray(board.lamps.length * 3);
  let scale = 40; // canvas pixels per key unit

  // What the effects get besides the time: sound levels, the painting, key presses.
  const io: Io & { keys: NonNullable<Io['keys']> } = { levels: null, beat: 0, paint: {}, keys: { events: [] } };
  let scratch: Scratch = {};
  let palette: Palette = [[255, 255, 255]];
  // With an effect's "Random colours" on, the palette carries a seed (see lib/effects.ts). The tray
  // app draws its own seed, so the keyboard's colours differ from the preview's - both are random.
  // (They do change at the same moments: both run on the tray app's clock and beat count.)
  let paletteSeed = newSeed();
  let runs: Run[] = []; // the selected layered preset, ready to draw
  let marked = new Set<number>(); // ids of the selected layer's lights, outlined in the picture

  const settings = () => seen.settings;
  const currentLayer = () => activePreset(settings())?.layers[seen.selectedLayer] ?? null;
  // Whether anything that is being shown needs 'audio' or 'keys'.
  const needs = (what: Need) => (activePreset(settings()) ? runsNeed(runs, what) : effectOf(settings().effect).needs === what);

  // ------------------------------------------------- following the settings

  function syncPaint() {
    io.paint = {};
    for (const [id, hex] of Object.entries(settings().paint)) io.paint[id] = hexToRgb(hex);
  }

  function refreshPalette() {
    const s = settings(), p = s.params[s.effect];
    palette = (p.palette?.length ? p.palette : ['#ffffff']).map(hexToRgb);
    if (p.random && !effectOf(s.effect).noPalette) palette.seed = paletteSeed;
  }

  function syncRuns() {
    const preset = activePreset(settings());
    runs = preset ? buildRuns(preset, board.lamps, runs) : [];
    marked = new Set(currentLayer()?.lamps ?? []);
  }

  function resize() {
    const cssW = canvas.clientWidth || 800;
    const dpr = window.devicePixelRatio || 1;
    const unitsW = board.box.w + 2 * MARGIN_U, unitsH = board.box.h + 2 * MARGIN_U;
    canvas.style.height = `${(cssW * unitsH) / unitsW}px`;
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round((cssW * dpr * unitsH) / unitsW);
    scale = canvas.width / unitsW;
  }

  function storeChanged() {
    const was = seen;
    seen = getState();
    const a = seen.settings, b = was.settings;
    const moved = seen.bindings !== was.bindings || a.numpadSide !== b.numpadSide;
    if (moved) {
      board = boardFor(seen.bindings, a.numpadSide);
      out = new Uint8ClampedArray(board.lamps.length * 3);
      resize();
    }
    if (a.effect !== b.effect) {
      scratch = {}; // scratch lives as long as the effect stays selected
      paletteSeed = newSeed();
    } else if (!!a.params[a.effect].random !== !!b.params[b.effect].random) {
      paletteSeed = newSeed();
    }
    if (a.effect !== b.effect || a.mode !== b.mode) io.levels = null;
    if (a.effect !== b.effect || a.mode !== b.mode || a.layerPreset !== b.layerPreset) io.keys.events = [];
    if (a.effect !== b.effect || a.params !== b.params) refreshPalette();
    if (a.paint !== b.paint) syncPaint();
    if (moved || a.mode !== b.mode || a.layerPreset !== b.layerPreset || a.layerPresets !== b.layerPresets || seen.selectedLayer !== was.selectedLayer) {
      syncRuns();
    }
  }

  // The time jumped: forget what was timed on the old clock.
  function clockJumped() {
    scratch = {};
    for (const run of runs) run.st = {};
    io.keys.events = [];
  }

  // --------------------------------------------------------------- drawing

  const px = (x: number) => (x - board.box.x0 + MARGIN_U) * scale;
  const py = (y: number) => (y - board.box.y0 + MARGIN_U) * scale;

  function rect(x0: number, y0: number, x1: number, y1: number, radius: number) {
    pctx.beginPath();
    pctx.roundRect(px(x0), py(y0), (x1 - x0) * scale, (y1 - y0) * scale, radius * scale);
  }

  function draw() {
    const { lamps, frame } = board;
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

    for (let i = 0; i < lamps.length; i++) {
      const l = lamps[i];
      if (l.binding === ISO_KEY) continue; // hidden under the left Shift on this board
      // A light that is off is drawn black, as it is on the keyboard; the outline keeps the layout readable.
      pctx.fillStyle = `rgb(${out[i * 3]},${out[i * 3 + 1]},${out[i * 3 + 2]})`;
      if (l.bar) rect(l.x - 0.17, l.y - 0.17, l.x + 0.17, l.y + 0.17, 0.07);
      else rect(l.x - l.w / 2 + KEY_GAP, l.y - l.h / 2 + KEY_GAP, l.x + l.w / 2 - KEY_GAP, l.y + l.h / 2 - KEY_GAP, 0.13);
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

  let lastTick = performance.now();
  let frameRequest = 0;

  function loop(now: number) {
    frameRequest = requestAnimationFrame(loop);
    if (!seen.loaded || now - lastTick < FRAME_MS || document.hidden) return;
    const dt = Math.min(0.25, (now - lastTick) / 1000);
    lastTick = now;
    const t = effectClock(now);
    const s = settings();
    if (activePreset(s)) {
      renderRuns(runs, { t, dt, out, io });
    } else {
      const effect = effectOf(s.effect);
      effect.frame({ t, dt, lamps: board.lamps, out, p: s.params[effect.id] as Params, pal: palette, st: scratch, io });
    }
    dim(out, board.lamps, s.brightness, s.zones);
    draw();
  }

  // The audio effects' preview uses the levels the tray app is hearing.
  let levelsBusy = false;
  async function pollLevels() {
    if (levelsBusy || document.hidden || !seen.reachable || !needs('audio')) return;
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

  // ----------------------------------------------------- pointer and keys

  let painting = 0; // 0 = no, 1 = paint or add, 2 = erase or take out
  let lastPoint: [number, number] | null = null; // where the pointer was at the previous event of this stroke (canvas pixels)
  let clickedLamp = -1;
  let pressCount = 0;

  function canvasPoint(ev: PointerEvent): [number, number] {
    const r = canvas.getBoundingClientRect();
    const k = canvas.width / r.width;
    return [(ev.clientX - r.left) * k, (ev.clientY - r.top) * k];
  }

  // Finds the light under a pointer position (canvas pixels).
  function lampAt(cx: number, cy: number) {
    const x = cx / scale + board.box.x0 - MARGIN_U, y = cy / scale + board.box.y0 - MARGIN_U;
    let best: BoardLamp | null = null, bestD = 0.45;
    for (const l of board.lamps) {
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

  // The lights the pointer passed over since the previous event of this stroke. The path in
  // between is walked in small steps, so a quick drag skips no key.
  function strokeLamps(ev: PointerEvent) {
    const to = canvasPoint(ev), from = lastPoint ?? to;
    lastPoint = to;
    const steps = Math.max(1, Math.ceil(Math.hypot(to[0] - from[0], to[1] - from[1]) / (scale * 0.3)));
    const found = new Set<BoardLamp>();
    for (let k = 1; k <= steps; k++) {
      const lamp = lampAt(from[0] + ((to[0] - from[0]) * k) / steps, from[1] + ((to[1] - from[1]) * k) / steps);
      if (lamp) found.add(lamp);
    }
    return [...found];
  }

  // The covered ISO light follows the Shift key it sits under.
  const withCovered = (lamp: BoardLamp) =>
    (lamp.binding === 42 ? [lamp.id, ...board.lamps.filter((l) => l.binding === ISO_KEY).map((l) => l.id)] : [lamp.id]);

  // One step of a stroke: in a layered preset the picture says which lights the selected layer
  // has, with the Paint effect it is painted on.
  function stroke(ev: PointerEvent) {
    const ids = strokeLamps(ev).flatMap(withCovered);
    if (!ids.length) return;
    if (settings().mode === 'layers') assignLamps(ids, painting === 2);
    else paintLamps(ids, painting === 2);
  }

  // Key presses for previewing the key-reactive effects (the list is described at keyEvents
  // in lib/effects.ts). They come from typing on this page and from clicking keys in the picture;
  // the real keyboard's presses are handled inside the tray app and never reach this page.
  const lampForKeyId = (id: number | null) => (id == null ? -1 : board.lamps.findIndex((l) => l.binding === id));

  function pressLamp(i: number) {
    if (io.keys.events.some((e) => e.i === i && e.r < 0)) return; // already held (auto-repeat)
    const now = effectClock();
    io.keys.events = io.keys.events.filter((e) => e.r < 0 || now - e.r < 30).slice(-255);
    io.keys.events.push({ i, t: now, r: -1, n: pressCount++ });
  }

  function releaseLamp(i: number) {
    const now = effectClock();
    for (const e of io.keys.events) if (e.r < 0 && (i < 0 || e.i === i)) e.r = now;
  }

  function keyDown(ev: KeyboardEvent) {
    if (!needs('keys') || (ev.target as Element | null)?.closest?.('input, select, textarea')) return;
    const i = lampForKeyId(keyIdForCode(ev.code));
    if (i < 0) return;
    if (ev.code === 'Space' || ev.code.startsWith('Arrow')) ev.preventDefault(); // keep the page from scrolling
    pressLamp(i);
  }

  function keyUp(ev: KeyboardEvent) {
    const i = lampForKeyId(keyIdForCode(ev.code));
    if (i >= 0) releaseLamp(i);
  }

  const windowBlurred = () => releaseLamp(-1);

  function pointerDown(ev: PointerEvent) {
    const erase = ev.button === 2 || ev.shiftKey;
    if (settings().mode === 'layers') {
      if (!currentLayer()) return;
    } else if (needs('keys')) {
      const lamp = lampAt(...canvasPoint(ev));
      if (!lamp || lamp.bar) return;
      clickedLamp = lamp.id;
      canvas.setPointerCapture(ev.pointerId);
      pressLamp(clickedLamp);
      return;
    } else if (settings().effect !== 'paint') {
      return;
    }
    painting = erase ? 2 : 1;
    canvas.setPointerCapture(ev.pointerId);
    stroke(ev);
  }

  function pointerMove(ev: PointerEvent) {
    if (painting) stroke(ev);
  }

  function pointerDone() {
    painting = 0;
    lastPoint = null;
    if (clickedLamp >= 0) releaseLamp(clickedLamp);
    clickedLamp = -1;
  }

  function contextMenu(ev: MouseEvent) {
    if (settings().mode === 'layers' || settings().effect === 'paint') ev.preventDefault();
  }

  // ------------------------------------------------------------------ start

  syncPaint();
  refreshPalette();
  syncRuns();
  resize();

  const stopWatching = [subscribe(storeChanged), onClockJump(clockJumped)];
  const resized = new ResizeObserver(resize);
  resized.observe(canvas.parentElement ?? canvas);
  const levelsTimer = setInterval(pollLevels, LEVELS_MS);
  frameRequest = requestAnimationFrame(loop);

  window.addEventListener('keydown', keyDown);
  window.addEventListener('keyup', keyUp);
  window.addEventListener('blur', windowBlurred);
  canvas.addEventListener('pointerdown', pointerDown);
  canvas.addEventListener('pointermove', pointerMove);
  canvas.addEventListener('pointerup', pointerDone);
  canvas.addEventListener('pointercancel', pointerDone);
  canvas.addEventListener('contextmenu', contextMenu);

  return () => {
    for (const stop of stopWatching) stop();
    resized.disconnect();
    clearInterval(levelsTimer);
    cancelAnimationFrame(frameRequest);
    window.removeEventListener('keydown', keyDown);
    window.removeEventListener('keyup', keyUp);
    window.removeEventListener('blur', windowBlurred);
    canvas.removeEventListener('pointerdown', pointerDown);
    canvas.removeEventListener('pointermove', pointerMove);
    canvas.removeEventListener('pointerup', pointerDone);
    canvas.removeEventListener('pointercancel', pointerDone);
    canvas.removeEventListener('contextmenu', contextMenu);
  };
}
