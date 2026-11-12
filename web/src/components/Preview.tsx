import { useEffect, useRef } from 'react';
import { effectOf } from '../lib/effects.ts';
import { attachPreview } from '../preview.ts';
import { activePreset } from '../settings.ts';
import { useStore } from '../store.ts';
import { cx } from './ui.tsx';

/** The keyboard picture. All its drawing and pointer handling is in preview.ts. */
export function Preview() {
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => attachPreview(canvas.current!), []);

  // The pointer shows what a click in the picture does.
  const cursor = useStore(({ settings, selectedLayer }) => {
    if (settings.mode === 'layers') return activePreset(settings)?.layers[selectedLayer] ? 'cursor-crosshair' : '';
    if (settings.effect === 'paint') return 'cursor-crosshair';
    return effectOf(settings.effect).needs === 'keys' ? 'cursor-pointer' : '';
  });

  return (
    <div className="rounded-lg border border-line bg-stage p-3">
      <canvas ref={canvas} aria-label="Keyboard lighting preview" className={cx('block w-full touch-none', cursor)} />
    </div>
  );
}
