// What the page knows: the tray app's settings and status. The page keeps nothing itself (no
// localStorage, no cookies); everything here is lost with the tab and read again from the tray app.
//
// State is never changed in place: every change makes new objects, so a part that is still the
// same object has not changed.

import { useSyncExternalStore } from 'react';
import { FALLBACK_BINDINGS } from './lib/board.ts';
import { defaultSettings } from './settings.ts';
import type { Settings } from './settings.ts';

/** What the tray app reports about itself (EngineStatus in tray/Engine.cs). */
export interface TrayStatus {
  keyboard?: boolean;
  lamps?: number;
  bindings?: number[] | null;
  audio?: string; // 'listening', 'silent' or 'unavailable'
  keys?: string; // 'watching', 'unavailable' or 'off'
  driving?: boolean;
}

export interface State {
  loaded: boolean; // the tray app has been asked once, whether or not it answered
  reachable: boolean; // the tray app answered the last request
  status: TrayStatus | null;
  settings: Settings;
  bindings: number[]; // the "binding" of each lamp id (see lib/geometry.ts)
  selectedLayer: number; // index into the selected layered preset's layers
}

let state: State = {
  loaded: false,
  reachable: false,
  status: null,
  settings: defaultSettings(),
  bindings: FALLBACK_BINDINGS,
  selectedLayer: 0,
};

const listeners = new Set<() => void>();

export const getState = () => state;

export function setState(patch: Partial<State>) {
  state = { ...state, ...patch };
  for (const listener of [...listeners]) listener();
}

export function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}

/**
 * A part of the state for a component, which is drawn again when that part changes. `select`
 * must hand out what is in the state (or a plain value worked out from it), not a new object.
 */
export const useStore = <T>(select: (s: State) => T): T => useSyncExternalStore(subscribe, () => select(state));
