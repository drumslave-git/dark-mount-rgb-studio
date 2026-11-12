// Settings page for the Dark Mount.
//
// This page only edits settings and draws a preview. The tray app (tray/) owns the keyboard's
// lights and keeps running the chosen effect - or layered preset - after the page is closed.

import { useState } from 'react';
import { EffectPicker, categoryOf } from './components/EffectPicker.tsx';
import { EffectSettings } from './components/EffectSettings.tsx';
import { LayerList, LayerSettings } from './components/Layers.tsx';
import { Preview } from './components/Preview.tsx';
import { ModeSwitch, Toolbar } from './components/Toolbar.tsx';
import { StatusLine } from './components/TrayState.tsx';
import { useStore } from './store.ts';

export function App() {
  const loaded = useStore((s) => s.loaded);
  return (
    <>
      <header className="flex items-center justify-between gap-4 border-b border-line px-6 pt-4 pb-3">
        <div>
          <h1 className="text-title font-semibold">Dark Mount Studio</h1>
          <StatusLine />
        </div>
      </header>

      <main className="max-w-[1200px] px-6 pt-4 pb-6">
        <Preview />
        {loaded && <Controls />}
        <p className="my-[13px] max-w-[80ch] text-hint text-muted">
          The keyboard can only remember its own built-in effects, so these are played by the small Dark Mount Studio tray app.
          This page just changes its settings — close the tab whenever you like. While the tray app is not running
          (or “Effects on” is off), the keyboard shows its built-in lighting.
          If Windows “Dynamic Lighting” is switched on for the keyboard, switch it off — it competes for the same lights.
        </p>
      </main>
    </>
  );
}

// Everything under the picture. Drawn once the settings are in, so nothing shows a made-up value first.
function Controls() {
  const mode = useStore((s) => s.settings.mode);
  const effect = useStore((s) => s.settings.effect);
  // The effect list opens on the selected effect's category and then stays where the user puts it.
  const [category, setCategory] = useState(() => categoryOf(effect));

  return (
    <>
      <Toolbar />
      <ModeSwitch />
      <div className="grid grid-cols-1 gap-6 min-[801px]:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        {mode === 'layers' ? (
          <>
            <LayerList />
            <LayerSettings />
          </>
        ) : (
          <>
            <EffectPicker category={category} onCategory={setCategory} />
            <EffectSettings />
          </>
        )}
      </div>
    </>
  );
}
