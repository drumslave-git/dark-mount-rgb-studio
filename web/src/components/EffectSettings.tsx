import { useRef, useState } from 'react';
import {
  clearPaint, deletePainting, fillPaint, loadPainting, savePainting, setEffectParams, setHandBackWhenSilent, setPaintColor,
} from '../actions.ts';
import { effectOf } from '../lib/effects.ts';
import { useStore } from '../store.ts';
import { ParamRows } from './ParamRows.tsx';
import { AudioState, KeysState } from './TrayState.tsx';
import { Button, Check, ColorInput, Heading, Hint, Line, Panel, TextInput } from './ui.tsx';

/** Right column of "One effect": the settings of the selected effect. */
export function EffectSettings() {
  const effect = effectOf(useStore((s) => s.settings.effect));
  const params = useStore((s) => s.settings.params[effect.id]);
  const handBack = useStore((s) => s.settings.handBackWhenSilent);
  const nothingToSet = !effect.params.length && effect.noPalette && !effect.needs;

  return (
    <div>
      <Heading className="mb-2">{effect.name} settings</Heading>
      <Panel>
        <ParamRows effect={effect} params={params} onChange={(next) => setEffectParams(effect.id, next)} />
        {effect.needs === 'audio' && (
          <>
            <Check checked={handBack} onChange={setHandBackWhenSilent}>When nothing is playing, show the keyboard’s own lighting</Check>
            <AudioState />
          </>
        )}
        {effect.needs === 'paint' && <PaintSettings />}
        {effect.needs === 'keys' && (
          <>
            <Hint>
              Try it here: type, or click keys in the picture above. On the real keyboard the tray app reacts to presses on the Dark Mount only; nothing you type is stored or sent anywhere.
            </Hint>
            <KeysState />
          </>
        )}
        {nothingToSet && <Hint>No settings for this effect.</Hint>}
      </Panel>
    </div>
  );
}

/** The brush and the saved paintings of the Paint effect. */
function PaintSettings() {
  const color = useStore((s) => s.settings.paintColor);
  const saved = useStore((s) => s.settings.paintPresets);
  const [name, setName] = useState('');
  const nameBox = useRef<HTMLInputElement>(null);
  const names = Object.keys(saved).sort((a, b) => a.localeCompare(b));

  const save = () => {
    const trimmed = name.trim();
    if (!trimmed) nameBox.current?.focus();
    else if (savePainting(trimmed)) setName('');
  };

  return (
    <>
      <Line>
        <span>Brush</span>
        <ColorInput value={color} onChange={setPaintColor} />
        <Button onClick={fillPaint}>Fill all</Button>
        <Button onClick={clearPaint}>Clear all</Button>
      </Line>
      <Hint>Click or drag over the keyboard to paint. Right-click or Shift-drag erases.</Hint>
      <Line>
        <span>Presets</span>
        <TextInput
          ref={nameBox}
          placeholder="Name this painting"
          maxLength={40}
          value={name}
          onChange={(ev) => setName(ev.target.value)}
          onKeyDown={(ev) => ev.key === 'Enter' && save()}
        />
        <Button onClick={save}>Save</Button>
      </Line>
      {names.length ? (
        <div className="flex flex-wrap gap-2">
          {names.map((n) => (
            <span key={n} className="inline-flex">
              <Button className="rounded-l-md bg-panel-2 px-2 py-[3px] text-small" title={`Load “${n}”`} onClick={() => { loadPainting(n); setName(n); }}>{n}</Button>
              <Button className="rounded-r-md border-l-0 bg-panel-2 px-2 py-[3px] text-small text-muted" title={`Delete “${n}”`} aria-label={`Delete preset ${n}`} onClick={() => { deletePainting(n); setName(''); }}>×</Button>
            </span>
          ))}
        </div>
      ) : (
        <Hint>No saved paintings yet. Saving under an existing name replaces it.</Hint>
      )}
    </>
  );
}
