import { PALETTES, PARAMS } from '../lib/effects.ts';
import type { Effect, StoredParams } from '../lib/effects.ts';
import { Button, Check, ColorInput, Hint, Line, Select } from './ui.tsx';

const MAX_COLOURS = 8;

/**
 * The sliders, colours and "Random colours" switch of one effect. `params` are that effect's
 * parameters; every edit hands a changed copy to `onChange`.
 */
export function ParamRows({ effect, params, onChange }: { effect: Effect; params: StoredParams; onChange: (params: StoredParams) => void }) {
  const palette = params.palette ?? [];
  const setPalette = (colours: string[]) => onChange({ ...params, palette: colours });

  return (
    <>
      {effect.params.map((key) => {
        const def = PARAMS[key];
        return (
          <label key={key} className="grid grid-cols-[96px_1fr_6ch] items-center gap-2">
            <span>{def.label}</span>
            <input
              type="range"
              className="w-full"
              min={def.min}
              max={def.max}
              step={def.step}
              value={params[key] ?? def.min}
              onChange={(ev) => onChange({ ...params, [key]: Number(ev.target.value) })}
            />
            <output className="text-right text-muted tabular-nums">{params[key]}{def.unit ?? ''}</output>
          </label>
        );
      })}

      {!effect.noPalette && (
        <>
          <Line>
            <span>Colours</span>
            <div className="flex flex-wrap items-center gap-1.5">
              {palette.map((hex, i) => (
                <ColorInput key={i} value={hex} title={`Colour ${i + 1}`} onChange={(colour) => setPalette(palette.with(i, colour))} />
              ))}
              <Button small title="Add a colour" disabled={palette.length >= MAX_COLOURS} onClick={() => setPalette([...palette, palette[palette.length - 1]])}>+</Button>
              <Button small title="Remove the last colour" disabled={palette.length <= 1} onClick={() => setPalette(palette.slice(0, -1))}>−</Button>
            </div>
            <Select title="Colour presets" value="" onChange={(ev) => ev.target.value && setPalette([...PALETTES[ev.target.value]])}>
              <option value="">Presets…</option>
              {Object.keys(PALETTES).map((name) => <option key={name} value={name}>{name}</option>)}
            </Select>
          </Line>
          <Check checked={!!params.random} onChange={(random) => onChange({ ...params, random })}>Random colours</Check>
          {params.random && (
            <Hint>
              Every key press, beat, band or sweep gets a new bright colour. The colours above now only decide how bright each part is, so dark ones keep their gaps dark.
            </Hint>
          )}
        </>
      )}
    </>
  );
}
