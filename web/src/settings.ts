// The tray app's settings as the page sees them (tray/Settings.cs holds the real ones).

import { EFFECTS, findEffect } from './lib/effects.ts';
import type { StoredParams } from './lib/effects.ts';
import { cleanPresets } from './lib/layers.ts';
import type { LayerPreset } from './lib/layers.ts';

export type Zone = 'keys' | 'bars' | 'numpad';
export type Mode = 'effect' | 'layers';
/** A painting: lamp id -> colour as #rrggbb. */
export type Painting = Record<string, string>;

export interface Settings {
  enabled: boolean;
  effect: string;
  params: Record<string, StoredParams>; // effect id -> its parameters
  brightness: number;
  zones: Record<Zone, boolean>;
  numpadSide: number; // 1 = left of the main block, 2 = right
  paint: Painting;
  paintPresets: Record<string, Painting>; // name -> saved painting
  paintColor: string; // the colour to paint with next
  handBackWhenSilent: boolean;
  gamma: boolean; // the tray app corrects colours for the keyboard's LEDs, so they look like the preview
  mode: Mode; // 'effect' = one effect on every light, 'layers' = the selected layered preset
  layerPreset: number; // index into layerPresets
  layerPresets: LayerPreset[]; // see lib/layers.ts
}

/** A change to send to the tray app: any part of the settings. */
export type SettingsPatch = Partial<Omit<Settings, 'zones'>> & { zones?: Partial<Record<Zone, boolean>> };

export const defaultSettings = (): Settings => ({
  enabled: true,
  effect: 'wave',
  params: Object.fromEntries(EFFECTS.map((e) => [e.id, structuredClone(e.defaults)])),
  brightness: 100,
  zones: { keys: true, bars: true, numpad: true },
  numpadSide: 1,
  paint: {},
  paintPresets: {},
  paintColor: '#ff6a13',
  handBackWhenSilent: true,
  gamma: true,
  mode: 'effect',
  layerPreset: 0,
  layerPresets: [],
});

// `current` with everything usable from `incoming` (settings as the tray app sends them) taken over.
export function adopt(current: Settings, incoming: unknown): Settings {
  if (!incoming || typeof incoming !== 'object') return current;
  const from = incoming as { [K in keyof Settings]?: unknown };
  const s: Settings = { ...current };
  if (typeof from.enabled === 'boolean') s.enabled = from.enabled;
  if (findEffect(from.effect)) s.effect = from.effect as string;
  if (Number.isFinite(from.brightness)) s.brightness = from.brightness as number;
  if (from.zones) s.zones = { ...s.zones, ...(from.zones as Partial<Record<Zone, boolean>>) };
  if (from.numpadSide === 1 || from.numpadSide === 2) s.numpadSide = from.numpadSide;
  if (from.paint) s.paint = { ...(from.paint as Painting) };
  if (from.paintPresets) s.paintPresets = structuredClone(from.paintPresets as Record<string, Painting>);
  if (typeof from.paintColor === 'string' && /^#[0-9a-f]{6}$/i.test(from.paintColor)) s.paintColor = from.paintColor;
  if (typeof from.handBackWhenSilent === 'boolean') s.handBackWhenSilent = from.handBackWhenSilent;
  if (typeof from.gamma === 'boolean') s.gamma = from.gamma;
  const params = (from.params ?? {}) as Record<string, StoredParams | undefined>;
  s.params = Object.fromEntries(EFFECTS.map((e) => [e.id, { ...current.params[e.id], ...params[e.id] }]));
  if (from.mode === 'effect' || from.mode === 'layers') s.mode = from.mode;
  if (Array.isArray(from.layerPresets)) s.layerPresets = cleanPresets(from.layerPresets);
  if (Number.isInteger(from.layerPreset)) s.layerPreset = from.layerPreset as number;
  s.layerPreset = Math.max(0, Math.min(s.layerPreset, s.layerPresets.length - 1));
  return s;
}

/** The layered preset that is being shown, or null while a single effect is. */
export const activePreset = (s: Settings): LayerPreset | null => (s.mode === 'layers' ? s.layerPresets[s.layerPreset] ?? null : null);
