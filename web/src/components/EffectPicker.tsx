import { Fragment } from 'react';
import { selectEffect } from '../actions.ts';
import { CATEGORIES, EFFECTS, findEffect } from '../lib/effects.ts';
import { useStore } from '../store.ts';
import { Chip, Heading, TitledButton } from './ui.tsx';

/** The category an effect is listed under. */
export const categoryOf = (effect: string) => CATEGORIES.find((c) => c.effects.includes(effect))?.id ?? 'all';

/** Left column of "One effect": the effects by category. `category` is 'all' or a category id. */
export function EffectPicker({ category, onCategory }: { category: string; onCategory: (id: string) => void }) {
  const selected = useStore((s) => s.settings.effect);
  const groups = category === 'all' ? CATEGORIES : CATEGORIES.filter((c) => c.id === category);

  return (
    <div>
      <Heading className="mb-2">Effect</Heading>
      <div className="mb-3 flex flex-wrap gap-1.5">
        <Chip pressed={category === 'all'} count={EFFECTS.length} onClick={() => onCategory('all')}>All</Chip>
        {CATEGORIES.map((c) => (
          <Chip key={c.id} pressed={c.id === category} count={c.effects.length} onClick={() => onCategory(c.id)}>{c.name}</Chip>
        ))}
      </div>
      <div>
        {groups.map((c) => (
          <Fragment key={c.id}>
            {category === 'all' && <h3 className="mt-4 mb-2 text-hint font-semibold text-muted first:mt-0">{c.name}</h3>}
            <div className="grid grid-cols-[repeat(auto-fill,minmax(160px,1fr))] gap-2">
              {c.effects.map((id) => findEffect(id)).map((e) => e && (
                <TitledButton key={e.id} title={e.name} detail={e.desc} pressed={e.id === selected} onClick={() => selectEffect(e.id)} />
              ))}
            </div>
          </Fragment>
        ))}
      </div>
    </div>
  );
}
