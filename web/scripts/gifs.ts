// Renders effects as looping GIFs of the keyboard picture, drawn the way the page's preview
// draws it (src/preview.ts), for the README. Audio effects get a made-up song and typing effects
// made-up typing, so they show something without the tray app.
//
// Usage: node scripts/gifs.ts <folder> <effect>[:seconds] ...
//   npm run gifs -- ../docs/effects wave aurora:6 keyripple
//
// The end of each GIF fades into its start, so it loops without a jump. GIF has no encoder in
// Node and the page takes no extra packages, so the encoder is here: a median-cut palette per
// frame, unchanged pixels left transparent, and LZW.

import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { FALLBACK_BINDINGS, buildBoard, dim } from '../src/lib/board.ts';
import { effectOf, findEffect, hexToRgb } from '../src/lib/effects.ts';
import type { Io, KeyEvent, Palette, Params, Scratch } from '../src/lib/effects.ts';
import { ISO_KEY } from '../src/lib/geometry.ts';
import { defaultSettings } from '../src/settings.ts';

const FPS = 20; // GIF delays are in 1/100 s, so 20 fps is exact
const SCALE = 20; // pixels per key unit
const MARGIN_U = 0.55; // as in preview.ts
const KEY_GAP = 0.06;
const START = 20; // seconds on the effect clock where the GIF starts
const WARM_UP = 3; // seconds rendered before it, so effects that build up have done so
const BLEND = 0.75; // seconds at the end that fade into the start

const [folder, ...specs] = process.argv.slice(2);
if (!folder || !specs.length || specs.some((s) => !findEffect(s.split(':')[0]))) {
  console.error('Usage: node scripts/gifs.ts <folder> <effect>[:seconds] ...');
  process.exit(2);
}

// ------------------------------------------------------------------ the picture

const settings = defaultSettings();
const board = buildBoard(FALLBACK_BINDINGS, settings.numpadSide);
const { lamps, box, frame } = board;
const W = Math.ceil((box.w + 2 * MARGIN_U) * SCALE), H = Math.ceil((box.h + 2 * MARGIN_U) * SCALE);
const LINE = Math.max(1, SCALE * 0.03);

const rgb = (hex: string) => hexToRgb(hex);
const BACKGROUND = rgb('#0b0c0d'), CASE = rgb('#131517'), OUTLINE = rgb('#2a2d32'), DISPLAY = rgb('#050506');

const clamp01 = (x: number) => (x < 0 ? 0 : x > 1 ? 1 : x);

// Visits the pixels a rounded rectangle (in key units) covers: how much of each its inside
// covers, and how much its outline (LINE pixels wide, centred on the edge) does.
function cover(x0: number, y0: number, x1: number, y1: number, radius: number, visit: (p: number, fill: number, line: number) => void) {
  const X0 = (x0 - box.x0 + MARGIN_U) * SCALE, Y0 = (y0 - box.y0 + MARGIN_U) * SCALE;
  const X1 = (x1 - box.x0 + MARGIN_U) * SCALE, Y1 = (y1 - box.y0 + MARGIN_U) * SCALE;
  const cx = (X0 + X1) / 2, cy = (Y0 + Y1) / 2, r = radius * SCALE;
  const hx = (X1 - X0) / 2 - r, hy = (Y1 - Y0) / 2 - r;
  const pad = LINE + 1;
  for (let y = Math.max(0, Math.floor(Y0 - pad)); y < Math.min(H, Math.ceil(Y1 + pad)); y++) {
    for (let x = Math.max(0, Math.floor(X0 - pad)); x < Math.min(W, Math.ceil(X1 + pad)); x++) {
      const qx = Math.abs(x + 0.5 - cx) - hx, qy = Math.abs(y + 0.5 - cy) - hy;
      const d = Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - r;
      const fill = clamp01(0.5 - d), line = clamp01(LINE / 2 + 0.5 - Math.abs(d));
      if (fill > 0 || line > 0) visit(y * W + x, fill, line);
    }
  }
}

function blend(img: Float32Array, p: number, c: ArrayLike<number>, f: number) {
  img[p * 3] += (c[0] - img[p * 3]) * f;
  img[p * 3 + 1] += (c[1] - img[p * 3 + 1]) * f;
  img[p * 3 + 2] += (c[2] - img[p * 3 + 2]) * f;
}

// Everything that does not light up, drawn once.
const still = new Float32Array(W * H * 3);
for (let p = 0; p < W * H; p++) still.set(BACKGROUND, p * 3);
const shape = (fillColour: number[]) => (p: number, fill: number, line: number) => {
  blend(still, p, fillColour, fill);
  blend(still, p, OUTLINE, line);
};
for (const c of [frame.pad, frame.main]) cover(c.x0 - 0.3, c.y0 - 0.3, c.x1 + 0.3, c.y1 + 0.3, 0.3, shape(CASE));
for (const d of frame.displayKeys) cover(d.x - d.size / 2, d.y - d.size / 2, d.x + d.size / 2, d.y + d.size / 2, 0.14, shape(DISPLAY));

// Each light's pixels, drawn on every frame over the still picture.
const lampPixels = lamps.map((l) => {
  const pix: number[] = [], fills: number[] = [], lines: number[] = [];
  if (l.binding === ISO_KEY) return { pix, fills, lines };
  const visit = (p: number, fill: number, line: number) => (pix.push(p), fills.push(fill), lines.push(line));
  if (l.bar) cover(l.x - 0.17, l.y - 0.17, l.x + 0.17, l.y + 0.17, 0.07, visit);
  else cover(l.x - l.w / 2 + KEY_GAP, l.y - l.h / 2 + KEY_GAP, l.x + l.w / 2 - KEY_GAP, l.y + l.h / 2 - KEY_GAP, 0.13, visit);
  return { pix, fills, lines };
});

function picture(colours: Float32Array): Uint32Array {
  const img = still.slice();
  for (let i = 0; i < lamps.length; i++) {
    const { pix, fills, lines } = lampPixels[i], c = colours.subarray(i * 3, i * 3 + 3);
    for (let k = 0; k < pix.length; k++) {
      const p = pix[k];
      img.set(still.subarray(p * 3, p * 3 + 3), p * 3);
      blend(img, p, c, fills[k]);
      blend(img, p, OUTLINE, lines[k]);
    }
  }
  const out = new Uint32Array(W * H);
  for (let p = 0; p < W * H; p++) {
    out[p] = (Math.floor(img[p * 3] + 0.5) << 16) | (Math.floor(img[p * 3 + 1] + 0.5) << 8) | Math.floor(img[p * 3 + 2] + 0.5);
  }
  return out;
}

// ------------------------------------------------------------------ made-up inputs

const hash = (k: number, salt: number) => {
  let h = Math.imul(k ^ 0x9e3779b9, 0x85ebca6b) ^ Math.imul(salt, 0xc2b2ae35);
  h = Math.imul(h ^ (h >>> 15), 0x27d4eb2d);
  return ((h ^ (h >>> 13)) >>> 0) / 4294967296;
};

// A song at 124 bpm: a kick on every beat, a hi-hat between them, a wandering middle.
const BEAT = 60 / 124;
function levelsAt(t: number) {
  const phase = (t / BEAT) % 1;
  const kick = Math.exp(-phase * 5), hat = Math.exp(-((phase + 0.5) % 1) * 9);
  const bar = Math.floor(t / (BEAT * 4));
  return Float32Array.from({ length: 32 }, (_, b) => {
    if (b < 6) return clamp01(0.2 + 0.75 * kick * (1 - b / 9));
    if (b < 20) return clamp01(0.18 + 0.3 * (0.5 + 0.5 * Math.sin(t * 2.3 + b * 0.8 + bar)) * (0.6 + 0.4 * Math.sin(t * 0.9 + b * 1.7)) + 0.2 * kick);
    return clamp01(0.08 + 0.5 * hat * (b / 32) + 0.08 * Math.sin(t * 11 + b));
  });
}

// Someone typing: letters and the odd space, a few presses a second.
const typed = [...'qwertyuiopasdfghjklzxcvbnm'];
const letterBinding: Record<string, number> = {};
[...'qwertyuiop'].forEach((ch, k) => (letterBinding[ch] = 16 + k));
[...'asdfghjkl'].forEach((ch, k) => (letterBinding[ch] = 30 + k));
[...'zxcvbnm'].forEach((ch, k) => (letterBinding[ch] = 43 + k));
const lampOf = (binding: number) => lamps.findIndex((l) => l.binding === binding);
const presses: KeyEvent[] = [];
for (let n = 0, t = START - WARM_UP - 2; t < START + 30; n++) {
  const space = hash(n, 1) < 0.15;
  const i = lampOf(space ? 57 : letterBinding[typed[Math.floor(hash(n, 2) * typed.length)]]);
  presses.push({ i, t, r: t + 0.07 + 0.08 * hash(n, 3), n });
  t += 0.12 + 0.3 * hash(n, 4) + (hash(n, 5) < 0.08 ? 0.6 : 0);
}
const keysAt = (t: number) => presses.filter((e) => e.t <= t).map((e) => ({ ...e, r: e.r <= t ? e.r : -1 }));

// ------------------------------------------------------------------ rendering an effect

function render(effectId: string, seconds: number): Float32Array[] {
  const effect = effectOf(effectId);
  const p = settings.params[effect.id];
  const pal: Palette = (p.palette?.length ? p.palette : ['#ffffff']).map(hexToRgb);
  if (p.random && !effect.noPalette) pal.seed = 1234;
  const st: Scratch = {};
  const out = new Uint8ClampedArray(lamps.length * 3);
  const dt = 1 / FPS, n = Math.round(seconds * FPS), blendN = Math.min(Math.round(BLEND * FPS), n >> 2);
  // Frame k of the stream is at START + (k - first) * dt; the GIF is frames first..first+n-1.
  const first = Math.round(WARM_UP * FPS);
  const stream: Float32Array[] = [];
  for (let k = 0; k < first + n; k++) {
    const t = START + (k - first) * dt;
    const io: Io = { levels: effect.needs === 'audio' ? levelsAt(t) : null, beat: Math.floor(t / BEAT), paint: {}, keys: { events: keysAt(t) } };
    effect.frame({ t, dt, lamps, out, p: p as Params, pal, st, io });
    dim(out, lamps, settings.brightness, settings.zones);
    stream.push(Float32Array.from(out));
  }
  // The last frames fade into the ones just before the start, so the loop carries straight on.
  return Array.from({ length: n }, (_, k) => {
    const a = stream[first + k];
    const fade = (k - (n - blendN) + 1) / (blendN + 1);
    if (fade <= 0) return a;
    const b = stream[first + k - n];
    return a.map((v, j) => v + (b[j] - v) * fade);
  });
}

// ------------------------------------------------------------------ GIF encoding

class Bytes {
  buf = new Uint8Array(1 << 20);
  len = 0;
  push(...bytes: number[]) {
    if (this.len + bytes.length > this.buf.length) {
      const bigger = new Uint8Array(this.buf.length * 2 + bytes.length);
      bigger.set(this.buf);
      this.buf = bigger;
    }
    for (const b of bytes) this.buf[this.len++] = b;
  }
  word(v: number) {
    this.push(v & 255, v >> 8);
  }
}

// Up to `max` colours that stand in for the given ones (median cut, splitting the box with the
// most error each time). Returns the colours and, for every input colour, its stand-in.
function quantise(counts: Map<number, number>, max: number) {
  const cols = [...counts].map(([c, n]) => ({ c: [c >> 16, (c >> 8) & 255, c & 255], n }));
  if (!cols.length) return { palette: [], index: new Map<number, number>() };
  interface Box { lo: number; hi: number; axis: number; error: number }
  const measure = (lo: number, hi: number): Box => {
    let best = 0, axis = 0;
    for (let a = 0; a < 3; a++) {
      let sum = 0, sq = 0, total = 0;
      for (let k = lo; k < hi; k++) {
        const v = cols[k].c[a], n = cols[k].n;
        sum += v * n;
        sq += v * v * n;
        total += n;
      }
      const error = sq - (sum * sum) / total;
      if (error > best) [best, axis] = [error, a];
    }
    return { lo, hi, axis, error: hi - lo > 1 ? best : 0 };
  };
  const boxes = [measure(0, cols.length)];
  while (boxes.length < max) {
    const bi = boxes.reduce((b, x, k) => (x.error > boxes[b].error ? k : b), 0);
    const { lo, hi, axis, error } = boxes[bi];
    if (error <= 0) break;
    const part = cols.slice(lo, hi).sort((x, y) => x.c[axis] - y.c[axis]);
    cols.splice(lo, hi - lo, ...part);
    let total = 0, run = 0, mid = lo + 1;
    for (let k = lo; k < hi; k++) total += cols[k].n;
    for (let k = lo; k < hi - 1; k++) {
      run += cols[k].n;
      mid = k + 1;
      if (run >= total / 2) break;
    }
    boxes.splice(bi, 1, measure(lo, mid), measure(mid, hi));
  }
  const palette: number[][] = [], index = new Map<number, number>();
  boxes.forEach(({ lo, hi }, b) => {
    const s = [0, 0, 0];
    let total = 0;
    for (let k = lo; k < hi; k++) {
      for (let a = 0; a < 3; a++) s[a] += cols[k].c[a] * cols[k].n;
      total += cols[k].n;
      index.set((cols[k].c[0] << 16) | (cols[k].c[1] << 8) | cols[k].c[2], b);
    }
    palette.push(s.map((v) => Math.floor(v / total + 0.5)));
  });
  return { palette, index };
}

// LZW with 8-bit pixels, written in sub-blocks of up to 255 bytes.
function lzw(gif: Bytes, pixels: Uint8Array) {
  const CLEAR = 256, END = 257;
  gif.push(8);
  const block: number[] = [];
  let bits = 0, nbits = 0;
  const flush = () => {
    gif.push(block.length, ...block);
    block.length = 0;
  };
  let size = 9;
  const emit = (code: number) => {
    bits |= code << nbits;
    nbits += size;
    while (nbits >= 8) {
      block.push(bits & 255);
      bits >>>= 8;
      nbits -= 8;
      if (block.length === 255) flush();
    }
  };
  let table = new Map<number, number>(), next = END + 1;
  emit(CLEAR);
  let code = pixels[0];
  for (let k = 1; k < pixels.length; k++) {
    const key = (code << 8) | pixels[k], found = table.get(key);
    if (found !== undefined) {
      code = found;
      continue;
    }
    emit(code);
    if (next === 4096) {
      emit(CLEAR);
      table = new Map();
      next = END + 1;
      size = 9;
    } else {
      if (next >= 1 << size) size++;
      table.set(key, next++);
    }
    code = pixels[k];
  }
  emit(code);
  emit(END);
  if (nbits > 0) block.push(bits & 255);
  if (block.length) flush();
  gif.push(0);
}

const TRANSPARENT = 255;
const TOLERANCE = 3; // a pixel this close to what is shown already (in every channel) is left as it is

const near = (a: number, b: number) =>
  Math.abs((a >> 16) - (b >> 16)) <= TOLERANCE && Math.abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= TOLERANCE && Math.abs((a & 255) - (b & 255)) <= TOLERANCE;

function encode(frames: Uint32Array[]): Uint8Array {
  const gif = new Bytes();
  gif.push(...[...'GIF89a'].map((ch) => ch.charCodeAt(0)));
  gif.word(W);
  gif.word(H);
  gif.push(0, 0, 0); // no global colour table
  gif.push(0x21, 0xff, 11, ...[...'NETSCAPE2.0'].map((ch) => ch.charCodeAt(0)), 3, 1, 0, 0, 0); // loop forever

  let shown: Uint32Array | null = null;
  for (const img of frames) {
    // Only the rectangle around the pixels that changed is stored; unchanged ones in it stay transparent.
    let x0 = W, y0 = H, x1 = -1, y1 = -1;
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        if (shown && near(shown[y * W + x], img[y * W + x])) continue;
        x0 = Math.min(x0, x);
        x1 = Math.max(x1, x);
        y0 = Math.min(y0, y);
        y1 = Math.max(y1, y);
      }
    }
    if (x1 < 0) [x0, y0, x1, y1] = [0, 0, 0, 0];
    const w = x1 - x0 + 1, h = y1 - y0 + 1;
    const changed = (x: number, y: number) => !shown || !near(shown[y * W + x], img[y * W + x]);
    const counts = new Map<number, number>();
    for (let y = y0; y <= y1; y++) {
      for (let x = x0; x <= x1; x++) if (changed(x, y)) counts.set(img[y * W + x], (counts.get(img[y * W + x]) ?? 0) + 1);
    }
    const { palette, index } = quantise(counts, 255);
    const pixels = new Uint8Array(w * h);
    for (let y = y0; y <= y1; y++) {
      for (let x = x0; x <= x1; x++) pixels[(y - y0) * w + x - x0] = changed(x, y) ? index.get(img[y * W + x])! : TRANSPARENT;
    }

    gif.push(0x21, 0xf9, 4, (1 << 2) | 1); // keep the previous frame underneath; index 255 is transparent
    gif.word(100 / FPS);
    gif.push(TRANSPARENT, 0);
    gif.push(0x2c);
    gif.word(x0);
    gif.word(y0);
    gif.word(w);
    gif.word(h);
    gif.push(0x87); // a local colour table of 256 entries
    for (let k = 0; k < 256; k++) gif.push(...(palette[k] ?? [0, 0, 0]));
    lzw(gif, pixels);

    // The picture as it should look now; the next frame stores only what differs from it.
    const now: Uint32Array = shown ? shown.slice() : new Uint32Array(W * H);
    for (let y = y0; y <= y1; y++) {
      for (let x = x0; x <= x1; x++) if (changed(x, y)) now[y * W + x] = img[y * W + x];
    }
    shown = now;
  }
  gif.push(0x3b);
  return gif.buf.subarray(0, gif.len);
}

// ------------------------------------------------------------------ go

mkdirSync(folder, { recursive: true });
for (const spec of specs) {
  const [id, secs] = spec.split(':');
  const frames = render(id, Number(secs) || 4).map(picture);
  const bytes = encode(frames);
  const file = join(folder, `${id}.gif`);
  writeFileSync(file, bytes);
  console.log(`${file}: ${W}x${H}, ${frames.length} frames, ${Math.round(bytes.length / 1024)} KB`);
}
