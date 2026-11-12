// Lighting effects. Each effect fills `out` (3 bytes per lamp) for the current time.
//
// frame(ctx) receives:
//   t, dt   seconds
//   lamps   [{ id, u, v, cx, cy, zone, rnd }]  u/v in 0..1, cx/cy centred with equal scale (cy spans about -0.5..0.5)
//           In a layered preset (layers.js) an effect only gets its layer's lights, measured over that layer.
//   out     Uint8ClampedArray(lamps.length * 3)
//   p       the effect's parameters
//   pal     palette as [[r,g,b], ...]
//   st      scratch object that lives as long as the effect stays selected
//   io      { levels, beat, paint, keys } live inputs supplied by the app
//
// The tray app (tray/) runs a C# port of this file to drive the keyboard; this copy draws
// the preview. Keep the two in step when changing an effect.

const TAU = Math.PI * 2;
const frac = (x) => x - Math.floor(x);
const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

function hash3(x, y, z) {
  let h = (Math.imul(x, 374761393) + Math.imul(y, 668265263) + Math.imul(z, 1274126177)) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

function noise3(x, y, z) {
  const xi = Math.floor(x), yi = Math.floor(y), zi = Math.floor(z);
  let xf = x - xi, yf = y - yi, zf = z - zi;
  xf = xf * xf * (3 - 2 * xf);
  yf = yf * yf * (3 - 2 * yf);
  zf = zf * zf * (3 - 2 * zf);
  const lerp = (a, b, f) => a + (b - a) * f;
  const plane = (zz) =>
    lerp(
      lerp(hash3(xi, yi, zz), hash3(xi + 1, yi, zz), xf),
      lerp(hash3(xi, yi + 1, zz), hash3(xi + 1, yi + 1, zz), xf),
      yf,
    );
  return lerp(plane(zi), plane(zi + 1), zf);
}

function put(out, i, c, b = 1) {
  out[i * 3] = c[0] * b;
  out[i * 3 + 1] = c[1] * b;
  out[i * 3 + 2] = c[2] * b;
}

// ---- palettes
// `pal` is an array of [r, g, b] colours. With an effect's "Random colours" option on, it
// also carries pal.seed (a whole number). Every lookup then gives a random bright colour
// instead: colour number k (any whole number - they never repeat) gets its own hue, and
// keeps the brightness of the palette colour it stands in for, so a palette's dark gaps
// stay dark. Effects must go through pick / pickR / palCyc / palLin, never pal[...] directly.
//
// pal.offset (a whole number, set by newColoursOnBeat) moves every lookup on to other colours;
// the audio effects use it to get a fresh set of random colours on every beat.

const RA = new Float64Array(3), RB = new Float64Array(3);
const wrap = (k, n) => ((k % n) + n) % n;
const GOLDEN = 0.61803398875;

function randomStop(pal, k, into) {
  const base = pal[wrap(k, pal.length)];
  const v = Math.max(base[0], base[1], base[2]);
  k += pal.offset || 0;
  // Golden-ratio steps keep neighbouring colours well apart; the hash adds a little jitter.
  const h = frac(k * GOLDEN + hash3(pal.seed, 0, 977) + (hash3(k, pal.seed, 978) - 0.5) * 0.12) * 6;
  const s = Math.floor(h), f = h - s, up = v * f, down = v * (1 - f);
  into[0] = s === 0 || s === 5 ? v : s === 1 ? down : s === 4 ? up : 0;
  into[1] = s === 1 || s === 2 ? v : s === 0 ? up : s === 3 ? down : 0;
  into[2] = s === 3 || s === 4 ? v : s === 2 ? up : s === 5 ? down : 0;
  return into;
}

// Colour number k of the palette; k may be any whole number.
function pick(pal, k) {
  return pal.seed === undefined ? pal[wrap(k, pal.length)] : randomStop(pal, k, [0, 0, 0]);
}

// A palette colour chosen by r in 0..1.
function pickR(pal, r) {
  const n = pal.length, j = Math.floor(r * n) % n;
  return pal.seed === undefined ? pal[j] : randomStop(pal, j + Math.floor(r * 4096) * n, [0, 0, 0]);
}

// Part of a palette (colours from..to-1), keeping its random setting.
function subPalette(pal, from, to) {
  const part = pal.slice(from, to);
  part.seed = pal.seed;
  return part;
}

// Palette lookup that wraps around (last colour blends back into the first). With random
// colours it does not wrap, unless `period` says after how many colours it must repeat
// (needed where the lookup goes once around a circle).
function palCyc(pal, t, out, i, b = 1, period = 0) {
  const n = pal.length;
  let a, c, f;
  if (pal.seed === undefined) {
    const x = frac(t) * n;
    const k = Math.floor(x) % n;
    f = x - Math.floor(x);
    a = pal[k];
    c = pal[(k + 1) % n];
  } else {
    const x = t * n, k = Math.floor(x);
    f = x - k;
    a = randomStop(pal, period > 0 ? wrap(k, period) : k, RA);
    c = randomStop(pal, period > 0 ? wrap(k + 1, period) : k + 1, RB);
  }
  out[i * 3] = (a[0] + (c[0] - a[0]) * f) * b;
  out[i * 3 + 1] = (a[1] + (c[1] - a[1]) * f) * b;
  out[i * 3 + 2] = (a[2] + (c[2] - a[2]) * f) * b;
}

// Palette lookup from first colour (t = 0) to last colour (t = 1).
function palLin(pal, t, out, i, b = 1) {
  const n = pal.length, random = pal.seed !== undefined;
  if (n === 1) return put(out, i, random ? randomStop(pal, 0, RA) : pal[0], b);
  const x = clamp01(t) * (n - 1);
  const k = Math.min(Math.floor(x), n - 2);
  const f = x - k;
  const a = random ? randomStop(pal, k, RA) : pal[k], c = random ? randomStop(pal, k + 1, RB) : pal[k + 1];
  out[i * 3] = (a[0] + (c[0] - a[0]) * f) * b;
  out[i * 3 + 1] = (a[1] + (c[1] - a[1]) * f) * b;
  out[i * 3 + 2] = (a[2] + (c[2] - a[2]) * f) * b;
}

// How far one beat moves a palette of n colours on: at least n (no colour is carried over to
// the next beat), and by a step whose hue turn is at least a quarter of the colour wheel from
// one beat to the next and still clearly different two, three and four beats later - so the
// colours never look like a slightly different shade or swing between two sets.
function beatStep(n) {
  for (let step = n; ; step++) {
    let ok = true;
    // The turn after m beats must stay this close to half the wheel (0.5 = anywhere).
    for (let m = 1; m <= 4 && ok; m++) ok = Math.abs(frac(m * step * GOLDEN) - 0.5) <= (m === 1 ? 0.25 : m === 2 ? 0.3 : 0.38);
    if (ok) return step;
  }
}

// With random colours: gives the palette a fresh set of colours for beat number `beat`.
function newColoursOnBeat(pal, beat) {
  if (pal.seed === undefined) return;
  pal.beatStep ??= beatStep(pal.length);
  pal.offset = beat * pal.beatStep;
}

// Audio bands with fast attack and slow decay. io.levels holds 32 values in 0..1
// (bass first); the result lives in the effect's scratch object. io.beat counts the beats
// heard so far: with random colours, `pal` gets new ones on every beat.
function smoothLevels(io, st, gain, pal) {
  newColoursOnBeat(pal, io.beat || 0);
  if (!st.smooth || st.smooth.length !== 32) st.smooth = new Float32Array(32);
  const lv = io.levels;
  for (let b = 0; b < 32; b++) {
    const target = lv ? clamp01(lv[b] * gain) : 0;
    st.smooth[b] = target > st.smooth[b] ? target : st.smooth[b] * 0.88;
  }
  return st.smooth;
}

// Key presses for the reactive effects. io.keys.events is a list, oldest first, of
//   { i, t, r, n }   i = index of the pressed key's lamp, t = time of the press and
//                    r = time of the release (below 0 while still held), both in seconds on
//                    the same clock as ctx.t, n = running count of presses.
const keyEvents = (io) => io.keys?.events ?? [];

// Sums light in floating point and writes it to `out` once, so overlapping presses add up
// the same way whatever order they are drawn in.
function accumulator(st, out) {
  if (!st.acc || st.acc.length !== out.length) st.acc = new Float64Array(out.length);
  st.acc.fill(0);
  return st.acc;
}

// ---- small helpers shared by the effects below
// (No Math.round or Math.random here: the tray app's C# port must give the same numbers.)

const rand = (a, b, c) => hash3(Math.floor(a), Math.floor(b), Math.floor(c)); // repeatable 0..1 from three whole numbers
const tri = (x) => {
  const f = frac(x) * 2;
  return f < 1 ? f : 2 - f; // 0 -> 1 -> 0
};
const gauss = (x) => Math.exp(-x * x);
const TMP = new Float64Array(3);

function mix(out, i, a, c, f, b = 1) {
  out[i * 3] = (a[0] + (c[0] - a[0]) * f) * b;
  out[i * 3 + 1] = (a[1] + (c[1] - a[1]) * f) * b;
  out[i * 3 + 2] = (a[2] + (c[2] - a[2]) * f) * b;
}

function add(acc, i, c, b) {
  acc[i * 3] += c[0] * b;
  acc[i * 3 + 1] += c[1] * b;
  acc[i * 3 + 2] += c[2] * b;
}

const avg = (levels, from, to) => {
  let s = 0;
  for (let b = from; b < to; b++) s += levels[b];
  return s / (to - from);
};

// Key row of a light, 0 (F row) to 5 (space bar row). The board's v runs over the case height,
// 7.84 key units, with the F row 1.96 units below the top edge. (Inside a layer, l.v is measured
// over the layer's own lights and l.bv is the board's.)
const rowOf = (l) => {
  const y = (l.bv ?? l.v) * 7.84 - 1.96;
  return y < 0.6 ? 0 : Math.min(5, Math.max(1, 1 + Math.floor(y - 0.7)));
};

// Largest projection of any light onto a direction, for effects that span the whole board.
function spanAlong(lamps, dx, dy) {
  let span = 1e-6;
  for (let i = 0; i < lamps.length; i++) span = Math.max(span, Math.abs(lamps[i].cx * dx + lamps[i].cy * dy));
  return span;
}

const WASD_KEYS = [17, 30, 31, 32, 62, 65, 66, 69]; // key ids of W A S D and the four arrows

export const PALETTES = {
  Rainbow: ['#ff0000', '#ffd000', '#00ff40', '#00e5ff', '#2040ff', '#ff00d0'],
  Sunset: ['#ff3d00', '#ff9100', '#ff2d78', '#6a1b9a'],
  Ocean: ['#001a66', '#0066ff', '#00e5ff', '#e0ffff'],
  Forest: ['#0b3d0b', '#1faa00', '#b2ff59', '#ffd54f'],
  Fire: ['#000000', '#7a0c00', '#ff3d00', '#ffb300', '#fff3c4'],
  Ice: ['#00102e', '#0b63ce', '#7fdbff', '#ffffff'],
  Aurora: ['#000814', '#00c853', '#00e5ff', '#7c4dff'],
  Candy: ['#ff4fa3', '#ffd166', '#06d6a0', '#4cc9f0'],
  Mono: ['#ffffff'],
  'be quiet!': ['#ff5a00', '#ffb000'],
};

export const PARAMS = {
  speed: { label: 'Speed', min: 0, max: 3, step: 0.05 },
  scale: { label: 'Size', min: 0.2, max: 4, step: 0.05 },
  angle: { label: 'Direction', min: 0, max: 360, step: 5, unit: '°' },
  density: { label: 'Density', min: 0.05, max: 1, step: 0.05 },
  width: { label: 'Width', min: 0.05, max: 1, step: 0.05 },
  arms: { label: 'Arms', min: 1, max: 6, step: 1 },
  gain: { label: 'Sensitivity', min: 0.3, max: 4, step: 0.1 },
  fade: { label: 'Fade time', min: 0.1, max: 5, step: 0.1, unit: ' s' },
};

export const EFFECTS = [
  {
    id: 'wave',
    name: 'Wave',
    desc: 'Palette scrolling across the board',
    params: ['speed', 'scale', 'angle'],
    defaults: { speed: 0.8, scale: 1, angle: 0, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      const a = (p.angle * Math.PI) / 180, dx = Math.cos(a), dy = Math.sin(a);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palCyc(pal, (l.cx * dx + l.cy * dy) * p.scale * 0.3 - t * p.speed * 0.25, out, i);
      }
    },
  },
  {
    id: 'spiral',
    name: 'Spiral',
    desc: 'Palette twisting around the centre',
    params: ['speed', 'scale', 'arms'],
    defaults: { speed: 0.8, scale: 1, arms: 2, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const ang = Math.atan2(l.cy, l.cx) / TAU;
        palCyc(pal, ang * p.arms + Math.hypot(l.cx, l.cy) * p.scale * 0.4 - t * p.speed * 0.3, out, i, 1, p.arms * pal.length);
      }
    },
  },
  {
    id: 'rings',
    name: 'Rings',
    desc: 'Colour rings expanding from the centre',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palCyc(pal, Math.hypot(l.cx, l.cy) * p.scale * 0.6 - t * p.speed * 0.3, out, i);
      }
    },
  },
  {
    id: 'plasma',
    name: 'Plasma',
    desc: 'Slow, blobby colour mixing',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Sunset },
    frame({ t, lamps, out, p, pal }) {
      const s = p.scale, w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const v =
          Math.sin(l.cx * s * 3 + w) +
          Math.sin(l.cy * s * 4 - w * 1.3) +
          Math.sin((l.cx + l.cy) * s * 2 + w * 0.7) +
          Math.sin(Math.hypot(l.cx, l.cy) * s * 5 - w * 1.7);
        palCyc(pal, v / 8 + 0.5 + w * 0.02, out, i);
      }
    },
  },
  {
    id: 'aurora',
    name: 'Aurora',
    desc: 'Drifting curtains of light',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Aurora },
    frame({ t, lamps, out, p, pal }) {
      const s = p.scale, w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const n = noise3(l.cx * s * 1.3 + w * 0.15, l.cy * s * 2.2, w * 0.2);
        const glow = noise3(l.cx * s * 0.8 - w * 0.1, l.cy * s + 7, w * 0.12);
        palLin(pal, clamp01(n * 1.5 - 0.25), out, i, 0.35 + 0.65 * clamp01(glow * 1.6 - 0.2));
      }
    },
  },
  {
    id: 'fire',
    name: 'Fire',
    desc: 'Flames rising from the front edge',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: PALETTES.Fire },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const n = noise3(l.cx * p.scale * 3, l.cy * 3 + w * 2, w * 0.5);
        palLin(pal, n * 1.7 * (0.25 + l.v) - 0.12, out, i);
      }
    },
  },
  {
    id: 'lava',
    name: 'Lava lamp',
    desc: 'Soft blobs floating around',
    params: ['speed', 'scale'],
    defaults: { speed: 0.6, scale: 1, palette: ['#14002e', '#b3005e', '#ff5a00', '#ffd166'] },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed;
      const blobs = [0, 1, 2, 3, 4].map((k) => [
        Math.sin(w * (0.31 + k * 0.07) + k * 1.9) * 1.4,
        Math.cos(w * (0.43 + k * 0.05) + k * 2.7) * 0.45,
      ]);
      const r2 = 0.06 * p.scale * p.scale;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        let f = 0;
        for (const b of blobs) f += r2 / ((l.cx - b[0]) ** 2 + (l.cy - b[1]) ** 2 + 0.01);
        palLin(pal, f * 0.55, out, i);
      }
    },
  },
  {
    id: 'matrix',
    name: 'Matrix rain',
    desc: 'Drops falling down the columns',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.6, palette: ['#00ff66', '#d9ffe6'] },
    frame({ dt, lamps, out, p, pal, st }) {
      const COLS = 30;
      if (!st.cols) st.cols = Array.from({ length: COLS }, () => ({ y: Math.random() * 2 - 1, sp: 0.5 + Math.random() }));
      for (const c of st.cols) {
        c.y += dt * c.sp * p.speed * 0.9;
        if (c.y > 1.8 && Math.random() < p.density * 0.2) {
          c.y = -0.1 - Math.random() * 0.4;
          c.sp = 0.5 + Math.random();
        }
      }
      const head = pick(pal, pal.length - 1), tail = pick(pal, 0);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const d = st.cols[Math.min(COLS - 1, Math.floor(l.u * COLS))].y - l.v;
        if (d < 0 || d > 0.7) put(out, i, tail, 0);
        else if (d < 0.08) put(out, i, head);
        else put(out, i, tail, (1 - d / 0.7) ** 2);
      }
    },
  },
  {
    id: 'twinkle',
    name: 'Twinkle',
    desc: 'Random sparkles',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.5, palette: PALETTES.Ice },
    frame({ t, lamps, out, p, pal }) {
      const sharp = 2 + (1 - p.density) * 14;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const b = Math.max(0, Math.sin(t * p.speed * (1 + l.rnd * 2.5) + l.rnd * 50)) ** sharp;
        palCyc(pal, l.rnd * 7.3, out, i, b);
      }
    },
  },
  {
    id: 'ripples',
    name: 'Ripples',
    desc: 'Rings spreading from random spots',
    params: ['speed', 'density', 'width'],
    defaults: { speed: 1, density: 0.5, width: 0.3, palette: PALETTES.Ocean },
    frame({ t, dt, lamps, out, p, pal, st }) {
      if (!st.rip) st.rip = [];
      if (Math.random() < dt * p.density * 4) {
        st.rip.push({ x: (Math.random() - 0.5) * 3.2, y: Math.random() - 0.5, t0: t, c: Math.random() });
      }
      st.rip = st.rip.filter((r) => t - r.t0 < 4);
      const w = 0.04 + p.width * 0.25;
      const tmp = new Uint8ClampedArray(3);
      out.fill(0);
      for (const r of st.rip) {
        const age = t - r.t0, rad = age * p.speed * 0.8, fade = (1 - age / 4) ** 2;
        palCyc(pal, r.c, tmp, 0);
        for (let i = 0; i < lamps.length; i++) {
          const l = lamps[i];
          const d = (Math.hypot(l.cx - r.x, l.cy - r.y) - rad) / w;
          if (d > 2.5 || d < -2.5) continue;
          const b = Math.exp(-d * d) * fade;
          out[i * 3] += tmp[0] * b;
          out[i * 3 + 1] += tmp[1] * b;
          out[i * 3 + 2] += tmp[2] * b;
        }
      }
    },
  },
  {
    id: 'scanner',
    name: 'Scanner',
    desc: 'A bar sweeping side to side',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.3, palette: ['#ff1a00'] },
    frame({ t, lamps, out, p, pal }) {
      const ph = frac(t * p.speed * 0.35) * 2;
      const pos = ph < 1 ? ph : 2 - ph;
      const w = 0.02 + p.width * 0.2;
      for (let i = 0; i < lamps.length; i++) {
        const d = (lamps[i].u - pos) / w;
        palCyc(pal, lamps[i].u * 0.999, out, i, Math.exp(-d * d));
      }
    },
  },
  {
    id: 'radar',
    name: 'Radar',
    desc: 'A beam rotating around the centre',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.5, palette: ['#00ff66'] },
    frame({ t, lamps, out, p, pal }) {
      const ang = t * p.speed * 0.25;
      const sharp = 1 + (1 - p.width) * 10;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const d = frac(ang - Math.atan2(l.cy, l.cx) / TAU);
        palCyc(pal, ang, out, i, (1 - d) ** sharp);
      }
    },
  },
  {
    id: 'breathing',
    name: 'Breathing',
    desc: 'Fades in and out, stepping through the palette',
    params: ['speed'],
    defaults: { speed: 1, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      const ph = t * p.speed * 0.25;
      const b = 0.5 - 0.5 * Math.cos(ph * TAU);
      const c = pick(pal, Math.floor(ph));
      for (let i = 0; i < lamps.length; i++) put(out, i, c, b);
    },
  },
  {
    id: 'strobe',
    name: 'Alternating',
    desc: 'Left and right halves flash in turn',
    params: ['speed'],
    defaults: { speed: 1, palette: ['#ff0000', '#0030ff'] },
    frame({ t, lamps, out, p, pal }) {
      const ph = frac(t * p.speed * 1.2);
      const side = ph < 0.5 ? 0 : 1;
      const flash = frac(ph * 4) < 0.6 ? 1 : 0; // double flash per side
      const c = pick(pal, (side % pal.length) + Math.floor(t * p.speed * 1.2) * pal.length);
      for (let i = 0; i < lamps.length; i++) {
        const mine = (lamps[i].cx < 0 ? 0 : 1) === side;
        put(out, i, c, mine ? flash : 0);
      }
    },
  },
  {
    id: 'audio',
    name: 'Audio spectrum',
    desc: 'Equaliser bars driven by sound',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#00e5ff', '#7c4dff', '#ff2d78'] },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const level = sm[Math.min(31, Math.floor(l.u * 32))];
        const h = 1 - l.v; // 0 at the front edge, 1 at the back
        const b = clamp01((level - h * 0.92) * 8);
        palLin(pal, h, out, i, b);
      }
    },
  },
  {
    id: 'audiomirror',
    name: 'Audio mirrored',
    desc: 'Bass in the middle, treble at both ends',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#ff6a13', '#ff2d78', '#7c4dff'] },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const level = sm[Math.min(31, Math.floor(Math.abs(l.u - 0.5) * 2 * 32))];
        const h = 1 - l.v;
        const b = clamp01((level - h * 0.92) * 8);
        palLin(pal, h, out, i, b);
      }
    },
  },
  {
    id: 'audiopulse',
    name: 'Audio pulse',
    desc: 'The whole board glows with the bass',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#1a0033', '#7c4dff', '#ff2d78', '#ffd166'] },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      let bass = 0;
      for (let b = 0; b < 8; b++) bass += sm[b];
      bass /= 8;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const local = sm[Math.min(31, Math.floor(l.u * 32))];
        palLin(pal, clamp01(bass * 0.8 + local * 0.4), out, i, clamp01(0.08 + bass * 1.1));
      }
    },
  },
  {
    id: 'keypress',
    name: 'Key press',
    desc: 'Only the keys you press light up',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 0.6, palette: ['#ff6a13', '#ffd166', '#00e5ff', '#7c4dff'] },
    frame({ t, lamps, out, p, pal, io }) {
      out.fill(0);
      for (const ev of keyEvents(io)) {
        if (ev.i >= lamps.length) continue;
        const b = ev.r < 0 ? 1 : clamp01(1 - (t - ev.r) / p.fade) ** 2;
        if (b > 0) put(out, ev.i, pick(pal, ev.n), b);
      }
    },
  },
  {
    id: 'keyripple',
    name: 'Key ripple',
    desc: 'A ring spreads from every key you press',
    needs: 'keys',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.3, palette: ['#00e5ff', '#7c4dff', '#ff2d78'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 2.5;
      const acc = accumulator(st, out);
      const w = 0.03 + p.width * 0.2;
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], c = pick(pal, ev.n);
        const rad = age * p.speed * 1.6, fade = (1 - age / LIFE) ** 2;
        for (let i = 0; i < lamps.length; i++) {
          const d = (Math.hypot(lamps[i].cx - src.cx, lamps[i].cy - src.cy) - rad) / w;
          if (d > 2.5 || d < -2.5) continue;
          const b = Math.exp(-d * d) * fade;
          acc[i * 3] += c[0] * b;
          acc[i * 3 + 1] += c[1] * b;
          acc[i * 3 + 2] += c[2] * b;
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keysplash',
    name: 'Key splash',
    desc: 'A burst of light around every key you press',
    needs: 'keys',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: ['#ff6a13', '#ffd166', '#ff2d78'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 1.2;
      const acc = accumulator(st, out);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], c = pick(pal, ev.n);
        const radius = p.scale * 0.45 * (1 - Math.exp(-age * p.speed * 6));
        if (radius <= 0) continue;
        const fade = (1 - age / LIFE) ** 2;
        for (let i = 0; i < lamps.length; i++) {
          const d = Math.hypot(lamps[i].cx - src.cx, lamps[i].cy - src.cy);
          if (d >= radius) continue;
          const b = (1 - d / radius) * fade;
          acc[i * 3] += c[0] * b;
          acc[i * 3 + 1] += c[1] * b;
          acc[i * 3 + 2] += c[2] * b;
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keybeam',
    name: 'Key beam',
    desc: 'A streak shoots along the row from every key you press',
    needs: 'keys',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.4, palette: ['#00ff9d', '#00e5ff'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 1.5;
      const acc = accumulator(st, out);
      const tail = 0.1 + p.width * 0.6;
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], c = pick(pal, ev.n);
        const head = age * p.speed * 3, fade = 1 - age / LIFE;
        for (let i = 0; i < lamps.length; i++) {
          if (Math.abs(lamps[i].cy - src.cy) > 0.06) continue; // same row only
          const dx = Math.abs(lamps[i].cx - src.cx);
          if (dx > head) continue;
          const b = Math.exp(-(head - dx) / tail) * fade;
          acc[i * 3] += c[0] * b;
          acc[i * 3 + 1] += c[1] * b;
          acc[i * 3 + 2] += c[2] * b;
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keyheat',
    name: 'Typing heat',
    desc: 'Keys warm up the more you use them, then cool down',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 4, palette: ['#001a66', '#00e5ff', '#ffd166', '#ff2d00'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      if (!st.heat || st.heat.length !== lamps.length) st.heat = new Float64Array(lamps.length);
      st.heat.fill(0);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age >= 0 && ev.i < lamps.length) st.heat[ev.i] += 0.3 * Math.exp(-age / p.fade);
      }
      for (let i = 0; i < lamps.length; i++) {
        const h = clamp01(st.heat[i]);
        palLin(pal, h, out, i, clamp01(h * 4));
      }
    },
  },
  // ------------------------------------------------------------------ static
  {
    id: 'solid',
    name: 'Solid colour',
    desc: 'One colour everywhere',
    params: [],
    defaults: { palette: ['#ff6a13'] },
    frame({ lamps, out, pal }) {
      const c = pick(pal, 0);
      for (let i = 0; i < lamps.length; i++) put(out, i, c);
    },
  },
  {
    id: 'gradient',
    name: 'Gradient',
    desc: 'A still blend across the board',
    params: ['angle'],
    defaults: { angle: 0, palette: PALETTES.Sunset },
    frame({ lamps, out, p, pal }) {
      const a = (p.angle * Math.PI) / 180, dx = Math.cos(a), dy = Math.sin(a);
      const span = spanAlong(lamps, dx, dy);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palLin(pal, ((l.cx * dx + l.cy * dy) / span + 1) / 2, out, i);
      }
    },
  },
  {
    id: 'rows',
    name: 'Rows',
    desc: 'Every key row in its own colour',
    params: [],
    defaults: { palette: PALETTES.Rainbow },
    frame({ lamps, out, pal }) {
      for (let i = 0; i < lamps.length; i++) put(out, i, pick(pal, rowOf(lamps[i])));
    },
  },
  {
    id: 'zones',
    name: 'Keys and edges',
    desc: 'Separate colours for keys, edge bars and numpad keys',
    params: [],
    defaults: { palette: ['#ffffff', '#ff6a13', '#00e5ff'] },
    frame({ lamps, out, pal }) {
      const keys = pick(pal, 0), bars = pick(pal, 1), numpad = pick(pal, 2);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        put(out, i, l.bar ? bars : l.pad ? numpad : keys);
      }
    },
  },
  {
    id: 'wasd',
    name: 'WASD and arrows',
    desc: 'The gaming keys stand out from the rest',
    params: [],
    defaults: { palette: ['#ff6a13', '#141a2e'] },
    frame({ lamps, out, pal }) {
      const hot = pick(pal, 0), rest = pick(pal, 1);
      for (let i = 0; i < lamps.length; i++) {
        put(out, i, WASD_KEYS.includes(lamps[i].binding) && !lamps[i].bar ? hot : rest);
      }
    },
  },
  {
    id: 'confetti',
    name: 'Confetti',
    desc: 'Every light gets its own colour',
    params: [],
    defaults: { palette: PALETTES.Candy },
    frame({ lamps, out, pal }) {
      for (let i = 0; i < lamps.length; i++) put(out, i, pickR(pal, lamps[i].rnd));
    },
  },

  // ------------------------------------------------------------------ motion
  {
    id: 'stripes',
    name: 'Stripes',
    desc: 'Hard-edged bands marching across',
    params: ['speed', 'scale', 'angle'],
    defaults: { speed: 0.8, scale: 1, angle: 30, palette: ['#ff6a13', '#000000'] },
    frame({ t, lamps, out, p, pal }) {
      const a = (p.angle * Math.PI) / 180, dx = Math.cos(a), dy = Math.sin(a), n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        put(out, i, pick(pal, Math.floor(((l.cx * dx + l.cy * dy) * p.scale * 0.8 - t * p.speed * 0.2) * n)));
      }
    },
  },
  {
    id: 'pinwheel',
    name: 'Pinwheel',
    desc: 'Coloured wedges turning around the centre',
    params: ['speed', 'arms'],
    defaults: { speed: 0.8, arms: 3, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      const n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const x = frac(Math.atan2(l.cy, l.cx) / TAU + t * p.speed * 0.12) * p.arms * n;
        put(out, i, pick(pal, Math.floor(x)));
      }
    },
  },
  {
    id: 'checker',
    name: 'Checkerboard',
    desc: 'Squares that swap colours',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: ['#ff2d78', '#00e5ff'] },
    frame({ t, lamps, out, p, pal }) {
      const ph = t * p.speed * 0.6, step = Math.floor(ph), f = clamp01((frac(ph) - 0.7) / 0.3), n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const ix = Math.floor(l.cx * p.scale * 3 + 100), iy = Math.floor(l.cy * p.scale * 3 + 100);
        // ix + iy picks the palette colour; the multiple of n changes nothing for a normal
        // palette but gives every square its own number, so random colours differ per square.
        const cell = ix + iy + (ix * 31 + iy * 17) * n;
        palCyc(pal, (cell + step + f) / n, out, i);
      }
    },
  },
  {
    id: 'edgechase',
    name: 'Edge chase',
    desc: 'Lights race around the edge bars',
    params: ['speed', 'arms', 'width'],
    defaults: { speed: 1, arms: 2, width: 0.4, palette: ['#ff6a13', '#ffd166'] },
    frame({ t, lamps, out, p, pal }) {
      const sharp = 1 + (1 - p.width) * 6, n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        if (!l.bar) {
          out[i * 3] = out[i * 3 + 1] = out[i * 3 + 2] = 0;
          continue;
        }
        const x = (t * p.speed * 0.15 - Math.atan2(l.cy, l.cx) / TAU) * p.arms;
        put(out, i, pick(pal, Math.floor(x)), (1 - frac(x)) ** sharp);
      }
    },
  },
  {
    id: 'bounce',
    name: 'Bouncing ball',
    desc: 'A glowing ball bounces around the board',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      const bx = (tri(t * p.speed * 0.11) - 0.5) * 2.9, by = (tri(t * p.speed * 0.17 + 0.3) - 0.5) * 0.9;
      const r = 0.1 + p.scale * 0.14;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palCyc(pal, t * 0.07, out, i, gauss(Math.hypot(l.cx - bx, l.cy - by) / r));
      }
    },
  },
  {
    id: 'curtain',
    name: 'Curtain',
    desc: 'Each colour opens from the middle outwards',
    params: ['speed'],
    defaults: { speed: 0.8, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      const ph = t * p.speed * 0.35, k = Math.floor(ph), front = frac(ph) * 1.1;
      const old = pick(pal, k), next = pick(pal, k + 1);
      for (let i = 0; i < lamps.length; i++) {
        const d = Math.abs(lamps[i].u - 0.5) * 2;
        mix(out, i, old, next, clamp01((front - d) / 0.08));
      }
    },
  },
  {
    id: 'tunnel',
    name: 'Tunnel',
    desc: 'Rings rushing towards the centre',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: ['#000000', '#7c4dff', '#00e5ff', '#ffffff'] },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palCyc(pal, Math.log(Math.hypot(l.cx, l.cy) + 0.08) * p.scale * 0.9 + t * p.speed * 0.3, out, i);
      }
    },
  },
  {
    id: 'kaleido',
    name: 'Kaleidoscope',
    desc: 'Mirrored colour patterns that keep changing',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const nz = noise3(Math.abs(l.cx) * p.scale * 2.2 + w * 0.2, Math.abs(l.cy) * p.scale * 2.2 - w * 0.15, w * 0.1);
        palCyc(pal, nz * 2, out, i);
      }
    },
  },
  {
    id: 'conveyor',
    name: 'Conveyor',
    desc: 'Neighbouring rows slide in opposite directions',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], row = rowOf(l);
        palCyc(pal, l.cx * p.scale * 0.3 + (row % 2 ? 1 : -1) * t * p.speed * 0.25 + row * 0.13, out, i);
      }
    },
  },
  {
    id: 'snake',
    name: 'Snake',
    desc: 'A snake winds its way through the rows',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.4, palette: ['#00ff66', '#ffd166'] },
    frame({ t, lamps, out, p, pal }) {
      const len = 0.04 + p.width * 0.25, head = frac(t * p.speed * 0.07);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], row = rowOf(l);
        const q = (row + (row % 2 ? 1 - l.u : l.u)) / 6;
        const d = frac(head - q);
        palCyc(pal, q * 3, out, i, d < len ? (1 - d / len) ** 2 : 0);
      }
    },
  },
  {
    id: 'crossfire',
    name: 'Crossfire',
    desc: 'Two bars sweep through each other',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.3, palette: ['#ff2d00', '#00a2ff'] },
    frame({ t, lamps, out, p, pal }) {
      const pos = tri(t * p.speed * 0.18), w = 0.02 + p.width * 0.2;
      const pass = Math.floor(t * p.speed * 0.18) * pal.length; // new random colours on every pass
      const c0 = pick(pal, pass), c1 = pick(pal, pass + 1);
      for (let i = 0; i < lamps.length; i++) {
        const b0 = gauss((lamps[i].u - pos) / w), b1 = gauss((lamps[i].u - (1 - pos)) / w);
        out[i * 3] = c0[0] * b0 + c1[0] * b1;
        out[i * 3 + 1] = c0[1] * b0 + c1[1] * b1;
        out[i * 3 + 2] = c0[2] * b0 + c1[2] * b1;
      }
    },
  },
  {
    id: 'wipe',
    name: 'Colour wipe',
    desc: 'Each colour sweeps in over the last',
    params: ['speed', 'angle'],
    defaults: { speed: 0.8, angle: 0, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      const a = (p.angle * Math.PI) / 180, dx = Math.cos(a), dy = Math.sin(a);
      const span = spanAlong(lamps, dx, dy);
      const ph = t * p.speed * 0.3, k = Math.floor(ph), front = frac(ph) * 1.1;
      const old = pick(pal, k), next = pick(pal, k + 1);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const x = ((l.cx * dx + l.cy * dy) / span + 1) / 2;
        mix(out, i, old, next, clamp01((front - x) / 0.06));
      }
    },
  },

  // ------------------------------------------------------------------ nature
  {
    id: 'ocean',
    name: 'Ocean',
    desc: 'Rolling waves',
    params: ['speed', 'scale'],
    defaults: { speed: 0.8, scale: 1, palette: PALETTES.Ocean },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const h = Math.sin(l.cx * 3 * p.scale + w) * 0.5 + Math.sin(l.cx * 5 * p.scale - w * 0.7 + l.cy * 4) * 0.3;
        const nz = noise3(l.cx * 2 * p.scale, l.cy * 3 + w * 0.2, w * 0.3);
        palLin(pal, 0.45 + 0.3 * h + (nz - 0.5) * 0.5, out, i);
      }
    },
  },
  {
    id: 'rain',
    name: 'Rain',
    desc: 'Drops falling towards you',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.6, palette: ['#0a2a66', '#4fc3f7', '#e1f5fe'] },
    frame({ t, lamps, out, p, pal }) {
      const COLS = 36;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], col = Math.min(COLS - 1, Math.floor(l.u * COLS));
        let b = 0;
        for (let j = 0; j < 2; j++) {
          const ph = t * p.speed * 0.7 * (0.6 + rand(col, j, 11) * 0.8) + rand(col, j, 12);
          if (rand(col * 2 + j, Math.floor(ph), 13) >= p.density) continue;
          const d = frac(ph) * 1.4 - 0.2 - l.v;
          if (d >= 0 && d < 0.35) b = Math.max(b, 1 - d / 0.35);
        }
        palLin(pal, b, out, i, b);
      }
    },
  },
  {
    id: 'snow',
    name: 'Snow',
    desc: 'Flakes drifting down',
    params: ['speed', 'density'],
    defaults: { speed: 0.6, density: 0.5, palette: ['#0b1a33', '#ffffff'] },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed, thr = 0.8 - p.density * 0.25;
      const sky = pick(pal, 0), flake = pick(pal, pal.length - 1);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const nz = noise3(l.cx * 7 + Math.sin(w * 0.4) * 0.6, l.cy * 7 - w * 0.9, 3.3);
        const f = clamp01((nz - thr) * 10);
        mix(out, i, sky, flake, f, 0.12 + 0.88 * f);
      }
    },
  },
  {
    id: 'storm',
    name: 'Thunderstorm',
    desc: 'Dark clouds with flashes of lightning',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.5, palette: ['#020417', '#0b1b4d', '#2a3a7c', '#ffffff'] },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed, slot = Math.floor(w * 0.8), fp = frac(w * 0.8);
      const strike = rand(slot, 0, 21) < p.density * 0.7 ? Math.exp(-fp * 7) * (0.6 + 0.4 * Math.sin(fp * 70)) : 0;
      const x0 = rand(slot, 1, 21);
      const n = pal.length;
      const sky = n > 1 ? subPalette(pal, 0, n - 1) : pal;
      const bolt = pick(pal, n - 1 + slot * n); // with random colours, every strike gets its own
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        palLin(sky, noise3(l.cx * 1.5 + w * 0.1, l.cy * 2, w * 0.15) * 0.9, TMP, 0);
        mix(out, i, TMP, bolt, clamp01(strike * (0.25 + 0.75 * gauss((l.u - x0) * 3.5))));
      }
    },
  },
  {
    id: 'fireflies',
    name: 'Fireflies',
    desc: 'Little lights wandering and blinking',
    params: ['speed', 'density', 'scale'],
    defaults: { speed: 0.8, density: 0.5, scale: 1, palette: ['#c6ff00', '#ffd166'] },
    frame({ t, lamps, out, p, pal, st }) {
      const acc = accumulator(st, out);
      const count = 4 + Math.floor(p.density * 14), r = 0.07 + p.scale * 0.07, w = t * p.speed;
      for (let k = 0; k < count; k++) {
        const fx = Math.sin(w * (0.21 + rand(k, 0, 31) * 0.3) + rand(k, 1, 31) * TAU) * 1.45;
        const fy = Math.sin(w * (0.27 + rand(k, 2, 31) * 0.3) + rand(k, 3, 31) * TAU) * 0.45;
        const blink = (0.5 + 0.5 * Math.sin(w * (1 + rand(k, 4, 31) * 2) + k)) ** 2;
        const c = pick(pal, k);
        for (let i = 0; i < lamps.length; i++) {
          const d = Math.hypot(lamps[i].cx - fx, lamps[i].cy - fy) / r;
          if (d <= 2.5) add(acc, i, c, gauss(d) * blink);
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'embers',
    name: 'Embers',
    desc: 'Sparks rising from a glowing front edge',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.6, palette: ['#1a0500', '#ff3d00', '#ffb300'] },
    frame({ t, lamps, out, p, pal }) {
      const COLS = 36;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], col = Math.min(COLS - 1, Math.floor(l.u * COLS));
        let b = clamp01((l.v - 0.7) * 2.2) * 0.5;
        for (let j = 0; j < 2; j++) {
          const ph = t * p.speed * 0.5 * (0.6 + rand(col, j, 14) * 0.8) + rand(col, j, 15);
          if (rand(col * 2 + j, Math.floor(ph), 16) >= p.density) continue;
          const d = l.v - (1.2 - frac(ph) * 1.4);
          if (d >= 0 && d < 0.18) b = Math.max(b, (1 - d / 0.18) * (1 - frac(ph)));
        }
        palLin(pal, b, out, i, clamp01(b * 1.5));
      }
    },
  },
  {
    id: 'daycycle',
    name: 'Day and night',
    desc: 'Sky colours drifting from dawn to dusk',
    params: ['speed'],
    defaults: { speed: 1, palette: ['#050a30', '#ff7e5f', '#87ceeb', '#fceabb', '#ff6a88'] },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) palCyc(pal, t * p.speed * 0.02 + (1 - lamps[i].v) * 0.12, out, i);
    },
  },
  {
    id: 'candle',
    name: 'Candlelight',
    desc: 'A warm, gentle flicker',
    params: ['speed'],
    defaults: { speed: 1, palette: ['#ff6a00', '#ffb347', '#ffd9a0'] },
    frame({ t, lamps, out, p, pal }) {
      const w = t * p.speed, glow = 0.55 + 0.45 * noise3(w * 3, 0.5, 0.5);
      for (let i = 0; i < lamps.length; i++) {
        const nz = noise3(lamps[i].cx * 1.3, lamps[i].cy * 1.3, w * 2.2);
        palLin(pal, nz, out, i, clamp01(glow * (0.6 + 0.4 * nz)));
      }
    },
  },
  {
    id: 'meteor',
    name: 'Meteor shower',
    desc: 'Streaks shooting diagonally across',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.6, palette: ['#ffffff', '#9ad0ff', '#ffd166'] },
    frame({ t, lamps, out, p, pal, st }) {
      const acc = accumulator(st, out);
      const DX = 0.6, DY = 0.8, TAIL = 0.55;
      for (let k = 0; k < 7; k++) {
        const ph = t * p.speed * 0.35 * (0.6 + rand(k, 0, 41) * 0.8) + rand(k, 1, 41);
        const cyc = Math.floor(ph), f = frac(ph);
        if (rand(k, cyc, 42) >= p.density) continue;
        const hx = rand(k, cyc, 43) * 3.4 - 2 + f * 1.9 * DX, hy = -0.75 + f * 1.9 * DY;
        const c = pick(pal, (k % pal.length) + (cyc * 7 + k) * pal.length); // random: a new colour per meteor
        for (let i = 0; i < lamps.length; i++) {
          const rx = lamps[i].cx - hx, ry = lamps[i].cy - hy;
          const along = rx * DX + ry * DY;
          if (along > 0.03 || along < -TAIL) continue;
          add(acc, i, c, Math.min(1, 1 + along / TAIL) * gauss((rx * DY - ry * DX) / 0.09));
        }
      }
      out.set(acc);
    },
  },

  // ------------------------------------------------------------------- pulse
  {
    id: 'heartbeat',
    name: 'Heartbeat',
    desc: 'A double thump from the centre',
    params: ['speed'],
    defaults: { speed: 1, palette: ['#ff0033'] },
    frame({ t, lamps, out, p, pal }) {
      const ph = frac(t * p.speed * 1.1);
      const beat = Math.max(gauss((ph - 0.1) / 0.05), 0.7 * gauss((ph - 0.32) / 0.06));
      const c = pick(pal, Math.floor(t * p.speed * 1.1) * pal.length); // random: a new colour per heartbeat
      for (let i = 0; i < lamps.length; i++) {
        put(out, i, c, clamp01(0.06 + beat * (1 - 0.35 * Math.hypot(lamps[i].cx, lamps[i].cy))));
      }
    },
  },
  {
    id: 'colorcycle',
    name: 'Colour cycle',
    desc: 'The whole board drifts through the colours',
    params: ['speed'],
    defaults: { speed: 1, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) palCyc(pal, t * p.speed * 0.08, out, i);
    },
  },
  {
    id: 'flash',
    name: 'Strobe',
    desc: 'The whole board flashes',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.3, palette: ['#ffffff'] },
    frame({ t, lamps, out, p, pal }) {
      const ph = t * p.speed * 2, on = frac(ph) < 0.05 + p.width * 0.45 ? 1 : 0;
      const c = pick(pal, Math.floor(ph));
      for (let i = 0; i < lamps.length; i++) put(out, i, c, on);
    },
  },
  {
    id: 'sparkle',
    name: 'Sparkle',
    desc: 'Quick flashes popping up at random',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.5, palette: ['#ffffff', '#ffd166', '#00e5ff'] },
    frame({ t, lamps, out, p, pal }) {
      const n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], x = t * p.speed * 5 + l.rnd * 97, slot = Math.floor(x);
        const on = rand(l.id, slot, 51) < p.density * 0.3;
        put(out, i, pickR(pal, rand(l.id, slot, 52)), on ? (1 - frac(x)) ** 2 : 0);
      }
    },
  },
  {
    id: 'disco',
    name: 'Disco',
    desc: 'Blocks jump to a new colour on every beat',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal }) {
      const ph = t * p.speed * 2, beat = Math.floor(ph), glow = 0.45 + 0.55 * Math.exp(-frac(ph) * 3), n = pal.length;
      for (let i = 0; i < lamps.length; i++) {
        const cell = Math.floor(lamps[i].u * 8 * p.scale) + Math.floor(lamps[i].v * 3 * p.scale) * 31;
        put(out, i, pickR(pal, rand(cell, beat, 61)), glow);
      }
    },
  },
  {
    id: 'glitch',
    name: 'Glitch',
    desc: 'Rows flicker to another colour for an instant',
    params: ['speed', 'density'],
    defaults: { speed: 1, density: 0.5, palette: ['#00e5ff', '#ff2d78', '#ffffff'] },
    frame({ t, lamps, out, p, pal }) {
      const slot = Math.floor(t * p.speed * 9), n = pal.length, base = pick(pal, 0);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], row = rowOf(l);
        const hit = rand(row, slot, 71) < p.density * 0.35 && frac(l.u * 3 + rand(row, slot, 72)) < 0.5;
        const r = rand(row, slot, 73);
        // Colour 1..n-1 of the palette; with random colours, a new one for every flicker.
        if (hit) put(out, i, pick(pal, ((1 + Math.floor(r * (n - 1 || 1))) % n) + Math.floor(r * 4096) * n));
        else put(out, i, base, 0.3);
      }
    },
  },
  {
    id: 'pulsewave',
    name: 'Pulse wave',
    desc: 'Brightness rolls across a fixed gradient',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: PALETTES.Sunset },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        const u = lamps[i].u;
        palLin(pal, u, out, i, 0.12 + 0.88 * (0.5 - 0.5 * Math.cos(TAU * (t * p.speed * 0.4 - u * p.scale))));
      }
    },
  },
  {
    id: 'mosaic',
    name: 'Mosaic',
    desc: 'Every light drifts through the colours at its own pace',
    params: ['speed'],
    defaults: { speed: 1, palette: PALETTES.Candy },
    frame({ t, lamps, out, p, pal }) {
      for (let i = 0; i < lamps.length; i++) {
        palCyc(pal, lamps[i].rnd * 7 + t * p.speed * 0.08 * (0.5 + lamps[i].rnd), out, i);
      }
    },
  },

  // ------------------------------------------------------------------- audio
  {
    id: 'audiovu',
    name: 'VU meter',
    desc: 'One bar that grows with the volume',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#00ff66', '#ffd166', '#ff2d00'] },
    frame({ lamps, out, p, pal, io, st }) {
      const level = clamp01(avg(smoothLevels(io, st, p.gain, pal), 0, 32) * 1.4);
      for (let i = 0; i < lamps.length; i++) palLin(pal, lamps[i].u, out, i, clamp01((level - lamps[i].u) * 12));
    },
  },
  {
    id: 'audioglow',
    name: 'Spectrum glow',
    desc: 'Each column glows with its own pitch',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: PALETTES.Rainbow },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      for (let i = 0; i < lamps.length; i++) {
        const u = lamps[i].u;
        palLin(pal, u, out, i, clamp01(sm[Math.min(31, Math.floor(u * 32))] * 1.3));
      }
    },
  },
  {
    id: 'audiobloom',
    name: 'Audio bloom',
    desc: 'A circle swells from the centre with the bass',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#ffffff', '#ff2d78', '#7c4dff'] },
    frame({ lamps, out, p, pal, io, st }) {
      const radius = 0.08 + avg(smoothLevels(io, st, p.gain, pal), 0, 8) * 1.9;
      for (let i = 0; i < lamps.length; i++) {
        const d = Math.hypot(lamps[i].cx, lamps[i].cy);
        palLin(pal, d / 1.6, out, i, clamp01((radius - d) * 8));
      }
    },
  },
  {
    id: 'audiofire',
    name: 'Audio flames',
    desc: 'Flames that leap with the music',
    needs: 'audio',
    params: ['gain', 'speed'],
    defaults: { gain: 1.5, speed: 1, palette: PALETTES.Fire },
    frame({ t, lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal), w = t * p.speed;
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], lv = sm[Math.min(31, Math.floor(l.u * 32))];
        const nz = noise3(l.cx * 4, l.cy * 4 + w * 2.5, w * 0.6);
        palLin(pal, (lv * 1.25 - (1 - l.v)) * 2.2 + (nz - 0.5) * 0.6 + 0.15 * l.v, out, i);
      }
    },
  },
  {
    id: 'audiohalo',
    name: 'Spectrum and halo',
    desc: 'Bars on the keys, bass on the edge lights',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#00e5ff', '#7c4dff', '#ff2d78'] },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal), bass = clamp01(avg(sm, 0, 8) * 1.4);
      const halo = pick(pal, pal.length - 1);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        if (l.bar) {
          put(out, i, halo, bass);
          continue;
        }
        const h = 1 - l.v;
        palLin(pal, h, out, i, clamp01((sm[Math.min(31, Math.floor(l.u * 32))] - h * 0.92) * 8));
      }
    },
  },
  {
    id: 'audiocentre',
    name: 'Centre bars',
    desc: 'Bars grow up and down from the middle row',
    needs: 'audio',
    params: ['gain'],
    defaults: { gain: 1.5, palette: ['#ffd166', '#ff6a13', '#ff2d78'] },
    frame({ lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i], h = Math.abs(l.v - 0.58) * 2;
        palLin(pal, h, out, i, clamp01((sm[Math.min(31, Math.floor(l.u * 32))] - h * 0.95) * 8));
      }
    },
  },
  {
    id: 'audiosparkle',
    name: 'Audio sparkle',
    desc: 'Sparkles that get busier as it gets louder',
    needs: 'audio',
    params: ['gain', 'speed'],
    defaults: { gain: 1.5, speed: 1, palette: PALETTES.Ice },
    frame({ t, lamps, out, p, pal, io, st }) {
      const sm = smoothLevels(io, st, p.gain, pal);
      for (let i = 0; i < lamps.length; i++) {
        const l = lamps[i];
        const tw = Math.max(0, Math.sin(t * p.speed * (2 + l.rnd * 4) + l.rnd * 50)) ** 3;
        palCyc(pal, l.rnd * 7.3, out, i, clamp01(tw * sm[Math.min(31, Math.floor(l.u * 32))] * 2.2));
      }
    },
  },

  // ---------------------------------------------------------------- reactive
  {
    id: 'keycross',
    name: 'Key cross',
    desc: 'Streaks shoot along the row and the column',
    needs: 'keys',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.4, palette: ['#ff2d78', '#7c4dff'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 1.5;
      const acc = accumulator(st, out);
      const tail = 0.1 + p.width * 0.6;
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], c = pick(pal, ev.n);
        const head = age * p.speed * 3, fade = 1 - age / LIFE;
        for (let i = 0; i < lamps.length; i++) {
          const dx = Math.abs(lamps[i].cx - src.cx), dy = Math.abs(lamps[i].cy - src.cy);
          if (dy <= 0.06 && dx <= head) add(acc, i, c, Math.exp(-(head - dx) / tail) * fade);
          else if (dx <= 0.07 && dy <= head) add(acc, i, c, Math.exp(-(head - dy) / tail) * fade);
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keyfireworks',
    name: 'Key fireworks',
    desc: 'Sparks fly out from every key you press',
    needs: 'keys',
    params: ['speed', 'scale'],
    defaults: { speed: 1, scale: 1, palette: ['#ffd166', '#ff2d78', '#00e5ff', '#7c4dff'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 1.3, SPARKS = 9, R = 0.06;
      const acc = accumulator(st, out);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], fade = (1 - age / LIFE) ** 2;
        for (let j = 0; j < SPARKS; j++) {
          const ang = (TAU * (j + rand(ev.n, j, 81))) / SPARKS;
          const dist = (0.35 + rand(ev.n, j, 82) * 0.5) * p.scale * (1 - Math.exp(-age * p.speed * 3));
          const sx = src.cx + Math.cos(ang) * dist, sy = src.cy + Math.sin(ang) * dist + age * age * 0.12;
          const c = pick(pal, ev.n + j + (ev.n * SPARKS + j) * pal.length); // random: every spark its own colour
          for (let i = 0; i < lamps.length; i++) {
            const d = Math.hypot(lamps[i].cx - sx, lamps[i].cy - sy) / R;
            if (d <= 2.5) add(acc, i, c, gauss(d) * fade);
          }
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keyshadow',
    name: 'Key shadow',
    desc: 'A lit board where every press punches a dark hole',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 1.2, palette: PALETTES.Sunset },
    frame({ t, lamps, out, p, pal, io, st }) {
      if (!st.mul || st.mul.length !== lamps.length) st.mul = new Float64Array(lamps.length);
      st.mul.fill(1);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= p.fade || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], k = (1 - age / p.fade) ** 2;
        for (let i = 0; i < lamps.length; i++) {
          const d = Math.hypot(lamps[i].cx - src.cx, lamps[i].cy - src.cy) / 0.2;
          if (d <= 2.5) st.mul[i] *= 1 - k * gauss(d);
        }
      }
      for (let i = 0; i < lamps.length; i++) palCyc(pal, lamps[i].cx * 0.25 + t * 0.04, out, i, st.mul[i]);
    },
  },
  {
    id: 'keydrip',
    name: 'Key drip',
    desc: 'Light runs down to the front from every key you press',
    needs: 'keys',
    params: ['speed', 'width'],
    defaults: { speed: 1, width: 0.4, palette: ['#00e5ff', '#2040ff'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const LIFE = 1.5;
      const acc = accumulator(st, out);
      const tail = 0.1 + p.width * 0.6;
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= LIFE || ev.i >= lamps.length) continue;
        const src = lamps[ev.i], c = pick(pal, ev.n);
        const head = age * p.speed * 1.5, fade = 1 - age / LIFE;
        for (let i = 0; i < lamps.length; i++) {
          if (Math.abs(lamps[i].cx - src.cx) > 0.07) continue;
          const dy = lamps[i].cy - src.cy;
          if (dy < -0.01 || dy > head) continue;
          add(acc, i, c, Math.exp(-(head - dy) / tail) * fade);
        }
      }
      out.set(acc);
    },
  },
  {
    id: 'keyedge',
    name: 'Edge flash',
    desc: 'The edge bars flash with every key press',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 0.5, palette: ['#ff6a13', '#ffd166', '#00e5ff'] },
    frame({ t, lamps, out, p, pal, io }) {
      let b = 0, c = pick(pal, 0);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= p.fade) continue;
        const k = (1 - age / p.fade) ** 2;
        if (k >= b) {
          b = k;
          c = pick(pal, ev.n);
        }
      }
      for (let i = 0; i < lamps.length; i++) put(out, i, c, lamps[i].bar ? b : 0);
      for (const ev of keyEvents(io)) if (ev.r < 0 && ev.i < lamps.length) put(out, ev.i, pick(pal, ev.n));
    },
  },
  {
    id: 'keytrail',
    name: 'Key trail',
    desc: 'Pressed keys stay lit and fade slowly, walking through the colours',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 2.5, palette: PALETTES.Rainbow },
    frame({ t, lamps, out, p, pal, io }) {
      out.fill(0);
      for (const ev of keyEvents(io)) {
        if (ev.i >= lamps.length) continue;
        const b = ev.r < 0 ? 1 : clamp01(1 - (t - ev.r) / p.fade);
        if (b <= 0) continue;
        if (pal.seed === undefined) palCyc(pal, ev.n * 0.045, out, ev.i, b * b);
        else put(out, ev.i, pick(pal, ev.n), b * b); // random: every press its own colour
      }
    },
  },
  {
    id: 'keyspeed',
    name: 'Typing speed',
    desc: 'The whole board heats up the faster you type',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 1.5, palette: ['#001a66', '#00e5ff', '#ffd166', '#ff2d00'] },
    frame({ t, lamps, out, p, pal, io }) {
      let rate = 0;
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age >= 0) rate += 0.14 * Math.exp(-age / p.fade);
      }
      rate = clamp01(rate);
      for (let i = 0; i < lamps.length; i++) palLin(pal, rate, out, i, 0.12 + 0.88 * rate);
    },
  },
  {
    id: 'keyspot',
    name: 'Key spotlight',
    desc: 'A soft glow follows the last key you pressed',
    needs: 'keys',
    params: ['scale', 'fade'],
    defaults: { scale: 1, fade: 1.5, palette: ['#ffffff', '#ffd166'] },
    frame({ t, lamps, out, p, pal, io }) {
      const events = keyEvents(io), ev = events[events.length - 1];
      out.fill(0);
      if (!ev || ev.i >= lamps.length) return;
      const b = ev.r < 0 ? 1 : clamp01(1 - (t - ev.r) / p.fade) ** 2;
      const src = lamps[ev.i], r = 0.12 + p.scale * 0.22, c = pick(pal, ev.n);
      for (let i = 0; i < lamps.length; i++) {
        put(out, i, c, gauss(Math.hypot(lamps[i].cx - src.cx, lamps[i].cy - src.cy) / r) * b);
      }
    },
  },
  {
    id: 'keyrow',
    name: 'Row flash',
    desc: 'The whole row lights up when you press a key in it',
    needs: 'keys',
    params: ['fade'],
    defaults: { fade: 0.6, palette: ['#00ff9d', '#00e5ff', '#7c4dff'] },
    frame({ t, lamps, out, p, pal, io, st }) {
      const acc = accumulator(st, out);
      for (const ev of keyEvents(io)) {
        const age = t - ev.t;
        if (age < 0 || age >= p.fade || ev.i >= lamps.length) continue;
        const row = rowOf(lamps[ev.i]), c = pick(pal, ev.n), k = (1 - age / p.fade) ** 2;
        for (let i = 0; i < lamps.length; i++) {
          if (!lamps[i].bar && rowOf(lamps[i]) === row) add(acc, i, c, k);
        }
      }
      out.set(acc);
    },
  },

  {
    id: 'paint',
    name: 'Paint',
    desc: 'Pick a colour for every light yourself',
    needs: 'paint',
    params: [],
    defaults: {},
    noPalette: true,
    frame({ lamps, out, io }) {
      for (let i = 0; i < lamps.length; i++) {
        const c = io.paint[lamps[i].id];
        if (c) put(out, i, c);
        else put(out, i, [0, 0, 0]);
      }
    },
  },
];

// How the page groups the effects. Every effect id appears exactly once.
export const CATEGORIES = [
  { id: 'static', name: 'Static', effects: ['paint', 'solid', 'gradient', 'rows', 'zones', 'wasd', 'confetti'] },
  {
    id: 'motion',
    name: 'Motion',
    effects: ['wave', 'spiral', 'rings', 'plasma', 'stripes', 'pinwheel', 'checker', 'conveyor', 'kaleido', 'tunnel',
      'curtain', 'wipe', 'scanner', 'crossfire', 'radar', 'edgechase', 'snake', 'bounce', 'matrix'],
  },
  {
    id: 'nature',
    name: 'Nature',
    effects: ['aurora', 'fire', 'embers', 'candle', 'lava', 'ocean', 'ripples', 'rain', 'snow', 'storm', 'meteor',
      'twinkle', 'fireflies', 'daycycle'],
  },
  {
    id: 'pulse',
    name: 'Pulse',
    effects: ['breathing', 'heartbeat', 'colorcycle', 'pulsewave', 'mosaic', 'sparkle', 'disco', 'glitch', 'strobe', 'flash'],
  },
  {
    id: 'audio',
    name: 'Audio',
    effects: ['audio', 'audiomirror', 'audiocentre', 'audiohalo', 'audioglow', 'audiofire', 'audiovu', 'audiobloom',
      'audiopulse', 'audiosparkle'],
  },
  {
    id: 'reactive',
    name: 'Typing',
    effects: ['keypress', 'keytrail', 'keyripple', 'keysplash', 'keyfireworks', 'keybeam', 'keycross', 'keydrip', 'keyrow',
      'keyedge', 'keyspot', 'keyshadow', 'keyheat', 'keyspeed'],
  },
];
