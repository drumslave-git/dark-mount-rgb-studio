// Prints one frame of an effect as the page's preview draws it, in the same form and from the
// same fixed inputs as the tray app's "--dump-frame" (described on Program.DumpFrame in
// tray/Program.cs). The two outputs must match byte for byte:
//
//   npm run --silent frame -- wave 1
//   ../tray/publish/DarkMountStudio.exe --dump-frame wave 1
//
// Usage: node scripts/frame.ts <effect | layers> <seconds> [settings JSON] [beats]
// (Node 22.18 or later runs the TypeScript in src/ as it is.)

import { FALLBACK_BINDINGS, buildBoard, dim } from '../src/lib/board.ts';
import { effectOf, findEffect, hexToRgb } from '../src/lib/effects.ts';
import type { Io, Palette, Params } from '../src/lib/effects.ts';
import { buildRuns, renderRuns } from '../src/lib/layers.ts';
import { activePreset, adopt, defaultSettings } from '../src/settings.ts';

const SEED = 1234;
const [effectId, seconds, settingsJson, beats] = process.argv.slice(2);
const layers = effectId === 'layers';
const t = Number(seconds);
if ((!layers && !findEffect(effectId)) || !Number.isFinite(t)) {
  console.error('Usage: node scripts/frame.ts <effect | layers> <seconds> [settings JSON] [beats]');
  process.exit(2);
}

let settings = adopt(defaultSettings(), settingsJson ? JSON.parse(settingsJson) : {});
settings = { ...settings, ...(layers ? { mode: 'layers' } : { mode: 'effect', effect: effectId }), paint: { ...settings.paint, 5: '#ff0000' } };

const { lamps } = buildBoard(FALLBACK_BINDINGS, settings.numpadSide);
const out = new Uint8ClampedArray(lamps.length * 3);
const io: Io = {
  levels: Float32Array.from({ length: 32 }, (_, b) => Math.min(1, Math.max(0, 0.3 + 0.5 * Math.sin(b)))),
  beat: Number(beats) || 0,
  paint: Object.fromEntries(Object.entries(settings.paint).map(([id, hex]) => [id, hexToRgb(hex)])),
  keys: {
    events: [
      { i: 60, t: t - 1.2, r: t - 1.0, n: 0 },
      { i: 95, t: t - 0.4, r: -1, n: 1 },
      { i: 60, t: t - 0.15, r: t - 0.05, n: 2 },
    ],
  },
};
const dt = 1 / 30;

const preset = activePreset(settings);
if (layers && !preset) {
  console.error('layers needs a settings JSON with layerPresets');
  process.exit(2);
}
if (preset) {
  renderRuns(buildRuns(preset, lamps, [], () => SEED), { t, dt, out, io });
} else {
  const effect = effectOf(settings.effect);
  const p = settings.params[effect.id];
  const pal: Palette = (p.palette?.length ? p.palette : ['#ffffff']).map(hexToRgb);
  if (p.random && !effect.noPalette) pal.seed = SEED;
  effect.frame({ t, dt, lamps, out, p: p as Params, pal, st: {}, io });
}
dim(out, lamps, settings.brightness, settings.zones);
console.log(`[${out.join(',')}]`);
