// Everything the user can change. Each action updates the page's copy of the settings and
// queues the same change for the tray app.

import { LIGHT_GROUPS, boardFor } from './lib/board.ts';
import { effectOf } from './lib/effects.ts';
import type { StoredParams } from './lib/effects.ts';
import { MAX_LAYERS, MAX_LAYER_PRESETS, freeName, makeLayer } from './lib/layers.ts';
import type { Layer, LayerPreset } from './lib/layers.ts';
import type { Mode, Painting, Settings, SettingsPatch, Zone } from './settings.ts';
import { getState, setState } from './store.ts';
import { push, refreshStatusSoon } from './tray.ts';

const MAX_PAINTINGS = 50;

const settings = () => getState().settings;
const boardLamps = () => boardFor(getState().bindings, settings().numpadSide).lamps;

// `send` is what the tray app is told, when that is less than the whole of `patch`.
function change(patch: Partial<Settings>, send: SettingsPatch = patch) {
  setState({ settings: { ...settings(), ...patch } });
  push(send);
}

// ------------------------------------------------------------------ toolbar

export const setEnabled = (enabled: boolean) => change({ enabled });
export const setBrightness = (brightness: number) => change({ brightness });
export const setGamma = (gamma: boolean) => change({ gamma });
export const setNumpadSide = (numpadSide: number) => change({ numpadSide });
export const setZone = (zone: Zone, on: boolean) => change({ zones: { ...settings().zones, [zone]: on } }, { zones: { [zone]: on } });

export function setMode(mode: Mode) {
  if (settings().mode === mode) return;
  change({ mode });
  refreshStatusSoon();
}

// --------------------------------------------------------------- one effect

export function selectEffect(id: string) {
  change({ effect: id });
  if (effectOf(id).needs) refreshStatusSoon();
}

export const setEffectParams = (id: string, params: StoredParams) =>
  change({ params: { ...settings().params, [id]: params } }, { params: { [id]: params } });

export const setHandBackWhenSilent = (handBackWhenSilent: boolean) => change({ handBackWhenSilent });

// -------------------------------------------------------------------- paint

export const setPaintColor = (paintColor: string) => change({ paintColor });

/** Paints the lights `ids` with the brush colour, or with `erase` takes their colour away. */
export function paintLamps(ids: number[], erase: boolean) {
  const paint = { ...settings().paint };
  for (const id of ids) {
    if (erase) delete paint[id];
    else paint[id] = settings().paintColor;
  }
  change({ paint });
}

export const fillPaint = () => paintLamps(boardLamps().map((l) => l.id), false);
export const clearPaint = () => change({ paint: {} });

/** Saves the painting under `name`, replacing one of that name. False when there is no room for another. */
export function savePainting(name: string) {
  const saved = settings().paintPresets;
  if (!Object.hasOwn(saved, name) && Object.keys(saved).length >= MAX_PAINTINGS) return false;
  change({ paintPresets: { ...saved, [name]: { ...settings().paint } } });
  return true;
}

export const loadPainting = (name: string) => change({ paint: { ...settings().paintPresets[name] } });

export function deletePainting(name: string) {
  const saved: Record<string, Painting> = { ...settings().paintPresets };
  delete saved[name];
  change({ paintPresets: saved });
}

// ------------------------------------------------------------------- layers
// A layered preset runs a different effect on each group of lights (see lib/layers.ts).

// Every change to the layered presets goes through here.
function changeLayers(patch: Partial<Pick<Settings, 'layerPreset' | 'layerPresets'>>, selectedLayer = getState().selectedLayer) {
  const next = { ...settings(), ...patch };
  setState({ settings: next, selectedLayer });
  push({ mode: next.mode, layerPreset: next.layerPreset, layerPresets: next.layerPresets });
}

const selectedPreset = (): LayerPreset | undefined => settings().layerPresets[settings().layerPreset];

// Replaces the selected preset by what `edit` makes of it.
function changePreset(edit: (preset: LayerPreset) => LayerPreset, selectedLayer?: number) {
  const preset = selectedPreset();
  if (!preset) return;
  const edited = edit(preset);
  if (edited === preset) return;
  changeLayers({ layerPresets: settings().layerPresets.map((p) => (p === preset ? edited : p)) }, selectedLayer);
}

// Replaces the selected layer by what `edit` makes of it.
function changeLayer(edit: (layer: Layer, preset: LayerPreset) => Layer) {
  const k = getState().selectedLayer;
  changePreset((preset) => {
    const layer = preset.layers[k];
    const edited = layer ? edit(layer, preset) : layer;
    return edited === layer ? preset : { ...preset, layers: preset.layers.map((l) => (l === layer ? edited : l)) };
  });
}

// A new layer starts with the effect (and its settings) chosen under "One effect".
function newLayer(others: Layer[]) {
  const effect = settings().effect === 'paint' ? 'wave' : settings().effect;
  return makeLayer(freeName(others, 'Layer'), effect, settings().params[effect]);
}

export function selectPreset(n: number) {
  changeLayers({ layerPreset: n }, 0);
  refreshStatusSoon();
}

export function newPreset() {
  const presets = settings().layerPresets;
  if (presets.length >= MAX_LAYER_PRESETS) return;
  const preset = { name: freeName(presets, 'Layered preset'), layers: [newLayer([])] };
  changeLayers({ layerPresets: [...presets, preset], layerPreset: presets.length }, 0);
  refreshStatusSoon();
}

export function deletePreset() {
  const presets = settings().layerPresets.filter((p) => p !== selectedPreset());
  changeLayers({ layerPresets: presets, layerPreset: Math.max(0, Math.min(settings().layerPreset, presets.length - 1)) }, 0);
  refreshStatusSoon();
}

export const renamePreset = (name: string) => changePreset((preset) => ({ ...preset, name }));

/** Once its name box is left: a preset is never left without a name. */
export function tidyPresetName() {
  const others = settings().layerPresets.filter((p) => p !== selectedPreset());
  changePreset((preset) => {
    const name = preset.name.trim() || freeName(others, 'Layered preset');
    return name === preset.name ? preset : { ...preset, name };
  });
}

export const selectLayer = (k: number) => setState({ selectedLayer: k });

export function addLayer() {
  const preset = selectedPreset();
  if (!preset || preset.layers.length >= MAX_LAYERS) return;
  changePreset((p) => ({ ...p, layers: [...p.layers, newLayer(p.layers)] }), preset.layers.length);
}

export function deleteLayer(k: number) {
  const preset = selectedPreset();
  if (!preset?.layers[k]) return;
  const layers = preset.layers.filter((_, j) => j !== k);
  let selected = getState().selectedLayer;
  if (selected > k || selected >= layers.length) selected = Math.max(0, selected - 1);
  changePreset((p) => ({ ...p, layers }), selected);
  refreshStatusSoon();
}

export const renameLayer = (name: string) => changeLayer((layer) => ({ ...layer, name }));

/** Once its name box is left: a layer is never left without a name. */
export const tidyLayerName = () =>
  changeLayer((layer, preset) => {
    const name = layer.name.trim() || freeName(preset.layers.filter((l) => l !== layer), 'Layer');
    return name === layer.name ? layer : { ...layer, name };
  });

export function setLayerEffect(effect: string) {
  // The new effect starts with the settings it has under "One effect".
  changeLayer((layer) => makeLayer(layer.name, effect, settings().params[effect], layer.lamps));
  refreshStatusSoon();
}

export const setLayerParams = (params: StoredParams) => changeLayer((layer) => ({ ...layer, params }));

/**
 * Puts lights into the selected layer, taking them out of the preset's other layers (a light is
 * in one layer only), or with `remove` takes them out of the layer.
 */
export function assignLamps(ids: number[], remove = false) {
  const k = getState().selectedLayer;
  changePreset((preset) => {
    const layer = preset.layers[k];
    if (!layer) return preset;
    const mine = new Set(layer.lamps);
    const before = mine.size;
    for (const id of ids) {
      if (remove) mine.delete(id);
      else mine.add(id);
    }
    if (mine.size === before) return preset;
    const taken = new Set(ids);
    return {
      ...preset,
      layers: preset.layers.map((other) => {
        if (other === layer) return { ...layer, lamps: [...mine].sort((a, b) => a - b) };
        return remove || !other.lamps.some((id) => taken.has(id)) ? other : { ...other, lamps: other.lamps.filter((id) => !taken.has(id)) };
      }),
    };
  });
}

/** Adds one of LIGHT_GROUPS to the selected layer. */
export function addLightGroup(n: number) {
  const group = LIGHT_GROUPS[n];
  const preset = selectedPreset();
  if (!group || !preset) return;
  const taken = new Set(preset.layers.flatMap((l) => l.lamps));
  assignLamps(boardLamps().filter((l) => group.has(l, taken)).map((l) => l.id));
}

export const removeAllLights = () => assignLamps(selectedPreset()?.layers[getState().selectedLayer]?.lamps ?? [], true);
