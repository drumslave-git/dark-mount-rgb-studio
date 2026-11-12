import { setBrightness, setEnabled, setGamma, setMode, setNumpadSide, setZone } from '../actions.ts';
import type { Mode, Zone } from '../settings.ts';
import { useStore } from '../store.ts';
import { Button, Check, Select, cx } from './ui.tsx';

const ZONES: [Zone, string][] = [['keys', 'Keys'], ['bars', 'Edge bars'], ['numpad', 'Numpad module']];

/** The settings that apply whatever is being shown. */
export function Toolbar() {
  const enabled = useStore((s) => s.settings.enabled);
  const brightness = useStore((s) => s.settings.brightness);
  const gamma = useStore((s) => s.settings.gamma);
  const zones = useStore((s) => s.settings.zones);
  const numpadSide = useStore((s) => s.settings.numpadSide);

  return (
    <div className="mt-3 mb-4 flex flex-wrap items-center gap-x-6 gap-y-3 rounded-lg border border-line bg-panel px-4 py-3">
      <Check checked={enabled} onChange={setEnabled} className="font-semibold">Effects on</Check>
      <label className="flex items-center gap-2 text-muted">
        Brightness
        <input type="range" className="w-[140px]" min={5} max={100} step={5} value={brightness} onChange={(ev) => setBrightness(Number(ev.target.value))} />
        <output className="min-w-[4.5ch] text-ink tabular-nums">{brightness}%</output>
      </label>
      <Check
        checked={gamma}
        onChange={setGamma}
        title="The keyboard’s LEDs show dark colours much brighter than a screen does, and mixed colours paler. With this on, colours are corrected before they are sent, so the keyboard looks like the preview. Off sends them as they are."
      >
        Match preview colours
      </Check>
      <fieldset className="flex gap-3 text-muted">
        <legend className="float-left mr-3">Light up</legend>
        {ZONES.map(([zone, label]) => (
          <label key={zone} className="text-ink">
            <input type="checkbox" checked={zones[zone]} onChange={(ev) => setZone(zone, ev.target.checked)} /> {label}
          </label>
        ))}
      </fieldset>
      <label className="flex items-center gap-2 text-muted">
        Numpad is on the
        <Select value={numpadSide} onChange={(ev) => setNumpadSide(Number(ev.target.value))}>
          <option value={1}>left</option>
          <option value={2}>right</option>
        </Select>
      </label>
    </div>
  );
}

const MODES: [Mode, string, string, string][] = [
  ['effect', 'One effect', 'rounded-l-md', 'The same effect on every light.'],
  ['layers', 'Layers', '-ml-px rounded-r-md', 'A different effect on each group of lights.'],
];

/** Chooses between one effect on every light and a layered preset. */
export function ModeSwitch() {
  const mode = useStore((s) => s.settings.mode);
  return (
    <div className="mb-4 flex flex-wrap items-center" role="group" aria-label="What the lights show">
      {MODES.map(([id, label, shape]) => (
        <Button key={id} pressed={id === mode} className={cx('px-4 py-1.5', shape, id !== mode && 'bg-panel')} onClick={() => setMode(id)}>
          {label}
        </Button>
      ))}
      <span className="ml-4 text-hint text-muted">{MODES.find(([id]) => id === mode)?.[3]}</span>
    </div>
  );
}
