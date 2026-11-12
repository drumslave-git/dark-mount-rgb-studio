// What the tray app is doing, in words.

import { useStore } from '../store.ts';
import { Hint, cx } from './ui.tsx';

/** The line under the page's title. */
export function StatusLine() {
  const loaded = useStore((s) => s.loaded);
  const reachable = useStore((s) => s.reachable);
  const status = useStore((s) => s.status);
  const enabled = useStore((s) => s.settings.enabled);

  let text = 'Looking for the tray app…';
  let ok: boolean | null = null;
  if (loaded) {
    ok = reachable && !!status?.keyboard;
    if (!reachable) text = 'The Dark Mount Studio tray app is not answering — start it to light the keyboard. The preview below still works.';
    else if (!status?.keyboard) text = 'Tray app running — keyboard not found. Plug it in and it will be picked up.';
    else text = `Tray app running — ${status.lamps} lights connected${enabled ? '' : ', effects switched off'}. You can close this tab; the effect keeps running.`;
  }
  return <p className={cx('mt-0.5 text-hint', ok === null ? 'text-muted' : ok ? 'text-ok' : 'text-danger')}>{text}</p>;
}

/** Whether the tray app hears anything, for the audio effects. */
export function AudioState() {
  const reachable = useStore((s) => s.reachable);
  const audio = useStore((s) => s.status?.audio);
  return (
    <Hint>
      {!reachable ? 'The tray app is not running, so there is no sound to show.'
        : audio === 'listening' ? 'Listening to what this PC is playing.'
        : audio === 'silent' ? 'Nothing is playing right now.'
        : 'The tray app cannot capture this PC’s sound output.'}
    </Hint>
  );
}

/** Whether the tray app is watching key presses, for the typing effects. */
export function KeysState() {
  const reachable = useStore((s) => s.reachable);
  const keys = useStore((s) => s.status?.keys);
  const enabled = useStore((s) => s.settings.enabled);
  return (
    <Hint>
      {!reachable ? 'The tray app is not running, so the keyboard itself will not react.'
        : keys === 'watching' ? 'The tray app is watching the Dark Mount’s keys.'
        : keys === 'unavailable' ? 'The tray app could not start watching key presses.'
        : enabled ? 'The tray app starts watching keys when one of these effects is selected.'
        : 'Effects are switched off.'}
    </Hint>
  );
}
