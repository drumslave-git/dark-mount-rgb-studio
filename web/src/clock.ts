// The effects' time in seconds. It runs on the tray app's clock, so the preview shows the same
// moment of an animation as the keyboard does; until the tray app answers it starts at 0.

let clockOffset = -performance.now() / 1000; // effect time = performance.now() / 1000 + clockOffset
const jumpListeners = new Set<() => void>();

export const effectClock = (now = performance.now()) => now / 1000 + clockOffset;

/** Calls `listener` whenever the time jumps, so that what was timed on the old clock can be forgotten. */
export function onClockJump(listener: () => void) {
  jumpListeners.add(listener);
  return () => void jumpListeners.delete(listener);
}

// trayNow: the tray app's clock as it answered a request sent at `asked` and received at `answered`.
export function syncClock(trayNow: unknown, asked: number, answered: number) {
  if (typeof trayNow !== 'number' || !Number.isFinite(trayNow)) return;
  const offset = trayNow - (asked + answered) / 2000;
  const jump = Math.abs(offset - clockOffset);
  // Small differences are measuring noise; a slow answer says little about the time.
  if (jump < 0.01 || (jump < 0.5 && answered - asked > 30)) return;
  clockOffset = offset;
  if (jump < 0.5) return;
  // The time jumped (first answer, or the tray app was restarted).
  for (const listener of [...jumpListeners]) listener();
}
