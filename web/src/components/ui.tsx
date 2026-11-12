// The page's building blocks: buttons, boxes and the small pieces of text around them.

import type { ComponentProps, ReactNode } from 'react';

/** Joins class names, leaving out the ones that are switched off. */
export const cx = (...names: (string | false | null | undefined)[]) => names.filter(Boolean).join(' ');

const BUTTON = 'cursor-pointer border disabled:cursor-default disabled:opacity-45';
const IDLE = 'border-line hover:border-line-hover';
const CHOSEN = 'relative border-accent bg-chosen';
const BOX = 'rounded-md border border-line bg-panel-2 px-2 py-[5px]';

interface ButtonProps extends ComponentProps<'button'> {
  small?: boolean;
  /** For a button that is one of a set: whether it is the selected one. */
  pressed?: boolean;
}

/**
 * A button. Its looks beyond the border come from `className`; without one it is the plain
 * grey button.
 */
export function Button({ small, pressed, className, ...props }: ButtonProps) {
  return (
    <button
      type="button"
      aria-pressed={pressed}
      className={cx(
        BUTTON,
        pressed ? CHOSEN : IDLE,
        className ?? cx('rounded-md bg-panel-2', small ? 'px-2 py-[3px] text-small' : 'px-3 py-1.5'),
      )}
      {...props}
    />
  );
}

/** A pill, for choosing one of a row. `count` is shown small after the label. */
export function Chip({ count, children, pressed, ...props }: ButtonProps & { count?: number }) {
  return (
    <Button
      pressed={pressed}
      className={cx('inline-flex items-baseline gap-1.5 rounded-full px-3 py-1', !pressed && 'bg-panel')}
      {...props}
    >
      <span>{children}</span>
      {count !== undefined && <small className="text-small text-muted tabular-nums">{count}</small>}
    </Button>
  );
}

/** A button with a name and a line of small print under it. `clip` cuts a name that is too long. */
export function TitledButton({ title, detail, clip, className, pressed, ...props }: ButtonProps & { title: string; detail: string; clip?: boolean }) {
  return (
    <Button pressed={pressed} className={cx('flex flex-col gap-0.5 px-3 py-2 text-left', !pressed && 'bg-panel', className)} {...props}>
      <span className={cx(clip && 'truncate')}>{title}</span>
      <small className="text-small leading-[1.3] text-muted">{detail}</small>
    </Button>
  );
}

export function Select({ className, ...props }: ComponentProps<'select'>) {
  return <select className={cx(BOX, 'max-w-full min-w-0', className)} {...props} />;
}

export function TextInput({ className, ...props }: ComponentProps<'input'>) {
  return <input type="text" className={cx(BOX, 'min-w-0 flex-[1_1_140px] placeholder:text-muted', className)} {...props} />;
}

export function ColorInput({ value, onChange, ...props }: Omit<ComponentProps<'input'>, 'onChange'> & { onChange: (hex: string) => void }) {
  return (
    <input
      type="color"
      value={value}
      onChange={(ev) => onChange(ev.target.value)}
      className="h-7 w-8 cursor-pointer rounded-md border border-line bg-transparent p-0"
      {...props}
    />
  );
}

/** A tick box with its label. */
export function Check({ checked, onChange, children, className, title }: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  children: ReactNode;
  className?: string;
  title?: string;
}) {
  return (
    <label className={cx('flex items-center gap-2', className)} title={title}>
      <input type="checkbox" checked={checked} onChange={(ev) => onChange(ev.target.checked)} />
      <span>{children}</span>
    </label>
  );
}

export function Heading({ className, ...props }: ComponentProps<'h2'>) {
  return <h2 className={cx('text-hint font-semibold text-muted', className)} {...props} />;
}

/** A line of explanation. */
export function Hint({ className, ...props }: ComponentProps<'p'>) {
  return <p className={cx('text-hint text-muted', className)} {...props} />;
}

/** A row of controls that wraps when it runs out of room. */
export function Line({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cx('flex flex-wrap items-center gap-2', className)} {...props} />;
}

/** The box the settings of an effect or a layer are in. */
export function Panel({ children }: { children: ReactNode }) {
  return <div className="grid gap-3 rounded-lg border border-line bg-panel px-4 py-3">{children}</div>;
}
