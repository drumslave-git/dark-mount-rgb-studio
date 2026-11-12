// The whole board laid out for the effects and for drawing: every light's position, the
// measures effects work with, and the outlines of the cases.
//
// The tray app does the same in tray/Renderer.cs (BuildLayout); keep the two in step.

import type { Lamp } from './effects.ts';
import { chassis, fallbackPosition, keySize, lampPosition } from './geometry.ts';
import type { Chassis } from './geometry.ts';
import { FALLBACK_LAMPS } from './layout.ts';

/** A light with its place in the picture: x/y in key units, w/h the size of its keycap. */
export interface BoardLamp extends Lamp {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Board {
  lamps: BoardLamp[]; // indexed by lamp id
  box: { x0: number; y0: number; w: number; h: number }; // around all the lights, in key units
  frame: Chassis;
}

/** The lamp table's "binding" of each lamp id, as stored for when the keyboard cannot be asked. */
export const FALLBACK_BINDINGS = FALLBACK_LAMPS.map((l) => l[2]);

// bindings: the binding of each lamp id. numpadSide: 1 = left of the main block, 2 = right.
export function buildBoard(bindings: number[], numpadSide: number): Board {
  const pts = bindings.map((binding, id) => {
    const fb = FALLBACK_LAMPS[id] ?? [100000, 0];
    const pos = lampPosition(binding, numpadSide) ?? fallbackPosition(fb[0], fb[1]);
    const [w, h] = keySize(binding);
    return { id, binding, x: pos.x, y: pos.y, bar: pos.bar, pad: pos.zone === 'numpad', w, h };
  });
  const x0 = Math.min(...pts.map((p) => p.x)), x1 = Math.max(...pts.map((p) => p.x));
  const y0 = Math.min(...pts.map((p) => p.y)), y1 = Math.max(...pts.map((p) => p.y));
  const w = x1 - x0 || 1, h = y1 - y0 || 1;
  const lamps = pts.map((p) => ({
    ...p,
    u: (p.x - x0) / w,
    v: (p.y - y0) / h,
    cx: (p.x - (x0 + x1) / 2) / h,
    cy: (p.y - y0) / h - 0.5,
    rnd: Math.abs(Math.sin(p.id * 12.9898) * 43758.5453) % 1,
  }));
  return { lamps, box: { x0, y0, w, h }, frame: chassis(numpadSide) };
}

let last: { bindings: number[]; numpadSide: number; board: Board } | null = null;

/** buildBoard, remembering the last answer: the board only changes with its two inputs. */
export function boardFor(bindings: number[], numpadSide: number): Board {
  if (last?.bindings !== bindings || last.numpadSide !== numpadSide) {
    last = { bindings, numpadSide, board: buildBoard(bindings, numpadSide) };
  }
  return last.board;
}

/** Applies the brightness (0..100) to a frame and switches off the lights of zones that are off. */
export function dim(out: Uint8ClampedArray, lamps: Lamp[], brightness: number, zones: { keys: boolean; bars: boolean; numpad: boolean }) {
  const bright = brightness / 100;
  for (let i = 0; i < lamps.length; i++) {
    const l = lamps[i];
    const lit = (l.bar ? zones.bars : zones.keys) && (!l.pad || zones.numpad);
    const k = lit ? bright : 0;
    if (k === 1) continue;
    out[i * 3] *= k;
    out[i * 3 + 1] *= k;
    out[i * 3 + 2] *= k;
  }
}

/** Whole groups of lights that can be added to a layer in one go. `taken`: the ids that are in a layer already. */
export const LIGHT_GROUPS: { name: string; has: (l: Lamp, taken: Set<number>) => boolean }[] = [
  { name: 'Main keys', has: (l) => !l.bar && !l.pad },
  { name: 'F row', has: (l) => !l.bar && !l.pad && l.binding >= 87 && l.binding <= 102 },
  { name: 'Numpad keys', has: (l) => !l.bar && l.pad },
  { name: 'Main edge lights', has: (l) => l.bar && !l.pad },
  { name: 'Numpad edge lights', has: (l) => l.bar && l.pad },
  { name: 'Everything not in a layer yet', has: (l, taken) => !taken.has(l.id) },
];
