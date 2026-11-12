// The editor for layered presets: a different effect on each group of lights (lib/layers.ts).
// In this mode the keyboard picture says which lights belong to the selected layer.

import { useEffect, useState } from 'react';
import {
  addLayer, addLightGroup, deleteLayer, deletePreset, newPreset, removeAllLights, renameLayer, renamePreset, selectLayer, selectPreset,
  setLayerEffect, setLayerParams, tidyLayerName, tidyPresetName,
} from '../actions.ts';
import { LIGHT_GROUPS } from '../lib/board.ts';
import { CATEGORIES, effectOf, findEffect } from '../lib/effects.ts';
import { ISO_KEY } from '../lib/geometry.ts';
import { MAX_LAYERS, MAX_LAYER_PRESETS, MAX_NAME } from '../lib/layers.ts';
import type { Layer } from '../lib/layers.ts';
import { useStore } from '../store.ts';
import { ParamRows } from './ParamRows.tsx';
import { AudioState, KeysState } from './TrayState.tsx';
import { Button, Chip, Heading, Hint, Line, Panel, Select, TextInput, TitledButton } from './ui.tsx';

// How many lights a layer has, in words. The ISO key's light is covered on this board and not counted.
function useLightsText() {
  const bindings = useStore((s) => s.bindings);
  return (layer: Layer) => {
    const n = layer.lamps.filter((id) => bindings[id] !== ISO_KEY).length;
    return n === 1 ? '1 light' : `${n} lights`;
  };
}

/** Left column: the presets, and the layers of the selected one. */
export function LayerList() {
  const presets = useStore((s) => s.settings.layerPresets);
  const selectedPreset = useStore((s) => s.settings.layerPreset);
  const selectedLayer = useStore((s) => s.selectedLayer);
  const lightsText = useLightsText();
  const preset = presets[selectedPreset];

  return (
    <div className="grid gap-3">
      <Heading>Layered preset</Heading>
      <div className="flex flex-wrap gap-1.5">
        {presets.map((p, n) => <Chip key={n} pressed={n === selectedPreset} onClick={() => selectPreset(n)}>{p.name}</Chip>)}
        <Chip disabled={presets.length >= MAX_LAYER_PRESETS} onClick={newPreset}>+ New preset</Chip>
      </div>

      {!preset ? (
        <Hint className="max-w-[60ch]">
          A layered preset shows a different effect on each group of lights — for example sound on the numpad, a rainbow on the main keys, a scanner on the F row and a breathing edge. Until you create one, the effect chosen under “One effect” keeps running.
        </Hint>
      ) : (
        <>
          <Line>
            <TextInput
              value={preset.name}
              maxLength={MAX_NAME}
              title="Name of this preset"
              aria-label="Name of this preset"
              onChange={(ev) => renamePreset(ev.target.value)}
              onBlur={tidyPresetName}
            />
            <DeletePresetButton key={selectedPreset} />
          </Line>

          <Heading>Layers</Heading>
          {preset.layers.length > 0 && (
            <div className="grid gap-2">
              {preset.layers.map((layer, k) => (
                <div key={k} className="flex">
                  <TitledButton
                    clip
                    className="min-w-0 flex-1 rounded-l-md"
                    title={layer.name}
                    detail={`${effectOf(layer.effect).name} · ${lightsText(layer)}`}
                    pressed={k === selectedLayer}
                    onClick={() => selectLayer(k)}
                  />
                  <Button
                    className="-ml-px rounded-r-md bg-panel px-3 py-1.5 text-muted"
                    title="Delete this layer"
                    aria-label={`Delete layer ${k + 1}`}
                    onClick={() => deleteLayer(k)}
                  >
                    ×
                  </Button>
                </div>
              ))}
            </div>
          )}
          <Line>
            <Button disabled={preset.layers.length >= MAX_LAYERS} onClick={addLayer}>+ Add layer</Button>
          </Line>
          <Hint className="max-w-[60ch]">Lights that are in no layer stay off.</Hint>
        </>
      )}
    </div>
  );
}

// Asks once more before it deletes: a preset can be a lot of work.
function DeletePresetButton() {
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    if (!armed) return;
    const timer = setTimeout(() => setArmed(false), 3000);
    return () => clearTimeout(timer);
  }, [armed]);
  return <Button onClick={() => (armed ? deletePreset() : setArmed(true))}>{armed ? 'Click again to delete' : 'Delete preset'}</Button>;
}

const Label = ({ children }: { children: string }) => <span className="min-w-[52px]">{children}</span>;

/** Right column: name, lights and effect of the selected layer. */
export function LayerSettings() {
  const preset = useStore((s) => s.settings.layerPresets[s.settings.layerPreset]);
  const layer = useStore((s) => s.settings.layerPresets[s.settings.layerPreset]?.layers[s.selectedLayer]);
  const lightsText = useLightsText();

  if (!layer) {
    return (
      <div>
        <Heading className="mb-2">Settings</Heading>
        <Panel>
          <Hint>{preset ? 'This preset has no layers yet — add one on the left.' : 'Create a layered preset on the left to start.'}</Hint>
        </Panel>
      </div>
    );
  }

  const effect = effectOf(layer.effect);
  return (
    <div>
      <Heading className="mb-2">Layer settings</Heading>
      <Panel>
        <Line>
          <Label>Name</Label>
          <TextInput
            value={layer.name}
            maxLength={MAX_NAME}
            aria-label="Name of this layer"
            onChange={(ev) => renameLayer(ev.target.value)}
            onBlur={tidyLayerName}
          />
        </Line>

        <Line>
          <Label>Lights</Label>
          <strong className="font-semibold tabular-nums">{lightsText(layer)}</strong>
          <Select title="Add a whole group of lights to this layer" value="" onChange={(ev) => ev.target.value && addLightGroup(Number(ev.target.value))}>
            <option value="">Add a group…</option>
            {LIGHT_GROUPS.map((group, n) => <option key={n} value={n}>{group.name}</option>)}
          </Select>
          <Button onClick={removeAllLights}>Remove all</Button>
        </Line>
        <Hint>
          Click or drag over the keyboard above to add lights to this layer; right-click or Shift-drag takes them out. Its lights are outlined in white. A light is in one layer only, so adding it here takes it from the layer it was in.
        </Hint>

        <Line>
          <Label>Effect</Label>
          <Select title="Effect of this layer" value={layer.effect} onChange={(ev) => setLayerEffect(ev.target.value)}>
            {CATEGORIES.map((c) => (
              <optgroup key={c.id} label={c.name}>
                {/* Painting is for the whole board; a layer that has it keeps it. */}
                {c.effects.filter((id) => findEffect(id) && (id !== 'paint' || id === layer.effect)).map((id) => (
                  <option key={id} value={id}>{effectOf(id).name}</option>
                ))}
              </optgroup>
            ))}
          </Select>
        </Line>
        <Hint>{effect.desc}. It plays on this layer’s lights as if they were the whole keyboard.</Hint>

        <ParamRows effect={effect} params={layer.params} onChange={setLayerParams} />

        {effect.needs === 'audio' && <AudioState />}
        {effect.needs === 'keys' && (
          <>
            <Hint>
              Reacts to the keys that are in this layer. Try it here by typing. On the real keyboard the tray app reacts to presses on the Dark Mount only; nothing you type is stored or sent anywhere.
            </Hint>
            <KeysState />
          </>
        )}
      </Panel>
    </div>
  );
}
