import { useRef, useState } from 'react';
import type { DragEvent } from 'react';
import { forgetDisplayKey, removeDisplayKeyPicture, setDisplayKeyColor, setDisplayKeyPicture } from '../actions.ts';
import { DISPLAY_KEY_SIZE } from '../picture.ts';
import { DISPLAY_KEY_COUNT } from '../settings.ts';
import type { DisplayKey } from '../settings.ts';
import { useStore } from '../store.ts';
import { Button, ColorInput, Heading, Hint, cx } from './ui.tsx';

const KEYS = Array.from({ length: DISPLAY_KEY_COUNT }, (_, i) => i + 1);

/** The eight keys with screens on the numpad module: a picture and a backlight colour for each. */
export function DisplayKeys() {
  const problem = useStore((s) => (s.reachable ? s.status?.displayKeysProblem : ''));
  return (
    <section className="mt-6">
      <Heading className="mb-2">Display keys</Heading>
      <div className="grid gap-3 rounded-lg border border-line bg-panel px-4 py-3">
        <Hint className="max-w-[80ch]">
          The eight keys with screens on the numpad module, in the same places. Choose a picture for a key (or drop one on it): it is
          fitted into {DISPLAY_KEY_SIZE} × {DISPLAY_KEY_SIZE} pixels, and the backlight colour shows wherever it is transparent.
          The tray app writes the pictures to the keyboard, which keeps them on its own.
        </Hint>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(150px,1fr))] gap-3 min-[720px]:grid-cols-4">
          {KEYS.map((n) => <KeyTile key={n} n={n} />)}
        </div>
        {problem && <p className="text-hint text-danger">{problem}</p>}
      </div>
    </section>
  );
}

const STATES: Record<string, [string, string]> = {
  waiting: ['Waiting to send', 'text-muted'],
  sending: ['Sending…', 'text-muted'],
  done: ['On the keyboard', 'text-ok'],
  failed: ['Picture unusable', 'text-danger'],
};

function KeyTile({ n }: { n: number }) {
  const key: DisplayKey | undefined = useStore((s) => s.settings.displayKeys[n]);
  const state = useStore((s) => (s.reachable ? s.status?.displayKeys?.[n - 1] : undefined));
  const files = useRef<HTMLInputElement>(null);
  const [error, setError] = useState('');

  const take = (file: File | undefined) => {
    if (!file) return;
    setDisplayKeyPicture(n, file).then(
      () => setError(''),
      () => setError('This browser cannot read that file as a picture.'),
    );
  };
  const drop = (ev: DragEvent) => {
    ev.preventDefault();
    take(ev.dataTransfer.files[0]);
  };

  const [stateText, stateColour] = key ? STATES[state ?? ''] ?? ['', ''] : ['Not set', 'text-muted'];
  return (
    <div className="grid content-start gap-2 rounded-md border border-line bg-panel-2 p-2" onDragOver={(ev) => ev.preventDefault()} onDrop={drop}>
      <div className="flex items-baseline justify-between gap-2 text-small">
        <span className="font-semibold">Key {n}</span>
        <span className={stateColour}>{stateText}</span>
      </div>
      <button
        type="button"
        title="Choose a picture"
        aria-label={`Choose a picture for display key ${n}`}
        onClick={() => files.current?.click()}
        className={cx(
          'mx-auto grid size-[120px] cursor-pointer place-items-center overflow-hidden rounded-md border',
          key ? 'border-line' : 'border-dashed border-line-hover bg-stage',
        )}
        style={key ? { backgroundColor: key.color } : undefined}
      >
        {key?.image ? (
          <img src={key.image} alt="" width={DISPLAY_KEY_SIZE} height={DISPLAY_KEY_SIZE} className="block size-full" />
        ) : (
          !key && <span className="px-2 text-small text-muted">Keeps its own picture</span>
        )}
      </button>
      <input
        ref={files}
        type="file"
        accept="image/png,image/*"
        className="hidden"
        onChange={(ev) => {
          take(ev.target.files?.[0]);
          ev.target.value = '';
        }}
      />
      <div className="flex flex-wrap items-center gap-1.5">
        <ColorInput value={key?.color ?? '#000000'} onChange={(color) => setDisplayKeyColor(n, color)} title="Backlight colour" aria-label={`Backlight colour of display key ${n}`} />
        <Button small onClick={() => files.current?.click()}>Picture…</Button>
        {key?.image && <Button small title="Leave the backlight colour alone on the key" onClick={() => removeDisplayKeyPicture(n)}>No picture</Button>}
        {key && <Button small title="Stop changing this key: it keeps the picture it shows now" onClick={() => forgetDisplayKey(n)}>Forget</Button>}
      </div>
      {error && <p className="text-small text-danger">{error}</p>}
    </div>
  );
}
