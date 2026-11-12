// Layered presets: several effects at once, each on its own group of lights.
//
// A preset is { name, layers: [{ name, effect, params, lamps }] }: `effect` is an effect id,
// `params` that effect's parameters (the layer's own copy) and `lamps` the ids of the layer's
// lights, ascending. A light belongs to one layer at most; lights in no layer stay off.
//
// Every layer's effect only gets that layer's lights, laid out over the layer's own extent,
// so it plays on them as if they were a whole keyboard: a spectrum on the numpad shows all
// its bands, a scanner on the F row sweeps exactly that row.
//
// The tray app does the same in tray/Renderer.cs (LayerLayout and the passes in Render);
// keep the two in step.

import { effectOf, findEffect, hexToRgb } from './effects.ts';
import type { Effect, Io, KeyEvent, Lamp, Need, Palette, Params, Scratch, StoredParams } from './effects.ts';

export const MAX_LAYER_PRESETS = 20;
export const MAX_LAYERS = 16;
export const MAX_NAME = 40;

const MAX_ASPECT = 4; // a layer is never treated as wider than this many times its height

export interface Layer {
  name: string;
  effect: string;
  params: StoredParams;
  lamps: number[];
}

export interface LayerPreset {
  name: string;
  layers: Layer[];
}

/** One layer, ready to draw. */
export interface Run {
  effect: Effect;
  p: StoredParams;
  lamps: Lamp[];
  out: Uint8ClampedArray;
  localOf: Map<number, number>; // lamp id -> index in `lamps`
  st: Scratch;
  random: boolean;
  seed: number;
  pal: Palette;
}

// The lights `ids` of the board as a layer's effect sees them: u/v run 0..1 over the layer,
// cx/cy are centred on it with equal scale, and bv keeps the board's v (for the key rows).
// A layer that is a single row or column sits in the middle of the missing direction.
export function layerLamps(lamps: Lamp[], ids: number[]): Lamp[] {
  const mine: Lamp[] = [];
  for (const id of ids) if (lamps[id]) mine.push(lamps[id]);
  if (!mine.length) return mine;
  let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
  for (const l of mine) {
    x0 = Math.min(x0, l.cx); x1 = Math.max(x1, l.cx);
    y0 = Math.min(y0, l.cy); y1 = Math.max(y1, l.cy);
  }
  const w = x1 - x0, h = y1 - y0;
  const unit = Math.max(h, w / MAX_ASPECT) || 1;
  const mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
  return mine.map((l) => ({
    ...l,
    bv: l.v,
    u: w > 0 ? (l.cx - x0) / w : 0.5,
    v: h > 0 ? (l.cy - y0) / h : 0.5,
    cx: (l.cx - mx) / unit,
    cy: (l.cy - my) / unit,
  }));
}

// A layer with every parameter of its effect filled in. `params` may be partial.
export function makeLayer(name: string, effect: string, params?: StoredParams | null, lamps: number[] = []): Layer {
  const eff = effectOf(effect);
  const p: StoredParams = { ...structuredClone(eff.defaults), ...structuredClone(params ?? {}) };
  if (!eff.noPalette) p.random = !!p.random;
  return { name, effect: eff.id, params: p, lamps: [...lamps].sort((a, b) => a - b) };
}

// Presets as they arrive from the tray app, made safe to edit.
export function cleanPresets(list: unknown): LayerPreset[] {
  if (!Array.isArray(list)) return [];
  return list.slice(0, MAX_LAYER_PRESETS).map((preset, n) => ({
    name: String(preset?.name ?? `Layered preset ${n + 1}`),
    layers: (Array.isArray(preset?.layers) ? (preset.layers as any[]) : [])
      .filter((l) => findEffect(l?.effect))
      .slice(0, MAX_LAYERS)
      .map((l, k) => makeLayer(
        String(l.name ?? `Layer ${k + 1}`), l.effect, l.params,
        (Array.isArray(l.lamps) ? (l.lamps as unknown[]) : []).filter((id): id is number => Number.isInteger(id) && (id as number) >= 0),
      )),
  }));
}

// Lowest "Prefix N" that none of the items is called yet.
export function freeName(items: { name: string }[], prefix: string) {
  for (let n = 1; ; n++) {
    const name = `${prefix} ${n}`;
    if (!items.some((item) => item.name === name)) return name;
  }
}

// Everything needed to draw a preset's layers on `lamps` (the whole board). Pass the previous
// result to keep each layer's scratch state and random colours while it keeps its effect.
// `seed` supplies the seed for a layer's "Random colours".
export function buildRuns(
  preset: LayerPreset | null | undefined,
  lamps: Lamp[],
  previous: Run[] = [],
  seed = () => Math.floor(Math.random() * 1e6),
): Run[] {
  return (preset?.layers ?? []).map((layer, k) => {
    const effect = effectOf(layer.effect);
    const random = !!layer.params.random && !effect.noPalette;
    const old = previous[k]?.effect === effect ? previous[k] : null;
    const mine = layerLamps(lamps, layer.lamps);
    const pal: Palette = (layer.params.palette?.length ? layer.params.palette : ['#ffffff']).map(hexToRgb);
    const run: Run = {
      effect,
      p: layer.params,
      lamps: mine,
      out: new Uint8ClampedArray(mine.length * 3),
      localOf: new Map(mine.map((l, i) => [l.id, i])),
      st: old ? old.st : {},
      random,
      seed: old && old.random === random ? old.seed : seed(),
      pal,
    };
    if (random) pal.seed = run.seed;
    return run;
  });
}

export const runsNeed = (runs: Run[], what: Need) => runs.some((run) => run.lamps.length && run.effect.needs === what);

// Draws one frame of every layer into `out` (3 bytes per lamp id of the whole board).
export function renderRuns(runs: Run[], { t, dt, out, io }: { t: number; dt: number; out: Uint8ClampedArray; io: Io }) {
  out.fill(0); // lights that are in no layer stay off
  for (const run of runs) {
    if (!run.lamps.length) continue;
    let layerIo = io;
    if (run.effect.needs === 'keys') {
      // A layer reacts to its own keys; each press is re-addressed to the layer's lamp list.
      const events: KeyEvent[] = [];
      for (const ev of io.keys?.events ?? []) {
        const i = run.localOf.get(ev.i);
        if (i !== undefined) events.push({ ...ev, i });
      }
      layerIo = { ...io, keys: { events } };
    }
    run.effect.frame({ t, dt, lamps: run.lamps, out: run.out, p: run.p as Params, pal: run.pal, st: run.st, io: layerIo });
    for (let k = 0; k < run.lamps.length; k++) {
      const at = run.lamps[k].id * 3;
      out[at] = run.out[k * 3];
      out[at + 1] = run.out[k * 3 + 1];
      out[at + 2] = run.out[k * 3 + 2];
    }
  }
}
