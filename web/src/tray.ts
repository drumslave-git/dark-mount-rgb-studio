// Talking to the tray app, which owns the settings and the keyboard's lights (tray/HttpServer.cs).

import { syncClock } from './clock.ts';
import { adopt } from './settings.ts';
import type { SettingsPatch } from './settings.ts';
import { getState, setState } from './store.ts';
import type { State, TrayStatus } from './store.ts';

let pending: SettingsPatch | null = null;
let pushTimer = 0;
let pushing = false;
// The id of the tray app whose settings the page shows. A restarted app has a new one and shows the
// settings it saved, which the page then takes over instead of showing its own copy.
let instance: unknown = null;

// What a new status means for the state. The tray app also says which lamp is which key.
function withStatus(status: TrayStatus | null | undefined): Partial<State> {
  const fromTray = status?.bindings;
  const changed = Array.isArray(fromTray) && fromTray.length > 0 && fromTray.join() !== getState().bindings.join();
  return { status: status ?? null, ...(changed ? { bindings: fromTray } : null) };
}

// Asks the tray app for its state. The settings are taken over the first time a tray app answers
// (on opening the page, or after it restarted): after that the page is where they are changed.
async function pull() {
  try {
    const asked = performance.now();
    const r = await fetch('/api/state', { cache: 'no-store' });
    const answered = performance.now();
    if (!r.ok) throw new Error(String(r.status));
    const j = await r.json();
    syncClock(j.clock, asked, answered);
    const patch: Partial<State> = { reachable: true, ...withStatus(j.status) };
    const settings = getState().settings;
    if (j.instance !== instance) {
      instance = j.instance;
      pending = null; // changes meant for an app that has stopped
      patch.selectedLayer = 0;
      patch.settings = adopt(settings, j.settings);
    } else if (!pending && !pushing && typeof j.settings?.enabled === 'boolean' && j.settings.enabled !== settings.enabled) {
      patch.settings = { ...settings, enabled: j.settings.enabled }; // toggled from the tray icon's menu
    }
    setState(patch);
  } catch {
    setState({ reachable: false, status: null });
  }
}

// Queues a change for the tray app. `patch` has the same shape as the settings object.
export function push(patch: SettingsPatch) {
  pending ??= {};
  const { params, zones, ...rest } = patch;
  Object.assign(pending, rest);
  if (params) {
    pending.params ??= {};
    for (const [id, p] of Object.entries(params)) pending.params[id] = { ...pending.params[id], ...p };
  }
  if (zones) pending.zones = { ...pending.zones, ...zones };
  clearTimeout(pushTimer);
  pushTimer = window.setTimeout(flush, 120);
}

async function flush() {
  if (pushing) {
    pushTimer = window.setTimeout(flush, 120);
    return;
  }
  const body = pending;
  pending = null;
  if (!body) return;
  pushing = true;
  try {
    const r = await fetch('/api/state', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error(String(r.status));
    const j = await r.json();
    pushing = false;
    setState({ reachable: true, ...withStatus(j.status) });
  } catch {
    pushing = false;
    setState({ reachable: false });
  }
}

/** Asks again in a moment: sound capture and key watching start on demand. */
export const refreshStatusSoon = () => void setTimeout(() => pull(), 900);

/** Reads the settings once, then keeps an eye on the tray app's status. */
export async function connect() {
  await pull();
  setState({ loaded: true });
  setInterval(() => pull(), 3000);
}
