// Physical positions of the Dark Mount's lights, in key units (1u = 19.05 mm).
//
// The keyboard reports a position for every lamp, but those are rough (keys are bunched
// into the F-row's groups, and part of the numpad table is scrambled), so effects would
// look uneven. Each lamp also reports a "binding" number, which identifies the key or
// edge LED reliably; positions here are looked up from that instead.

// Measured from a photo of the board: 0 is the left edge of Esc / the centre line of the F row.
const ROWS = [0, 1.2, 2.2, 3.2, 4.2, 5.2]; // F row, numbers, QWERTY, ASDF, ZXCV, bottom

type Point = [x: number, y: number];

const KEYS: Record<number, Point> = {};
const row = (y: number, first: number, xs: number[]) => xs.forEach((x, i) => (KEYS[first + i] = [x, y]));

// Main block. Binding numbers are the keyboard's own key ids.
row(ROWS[1], 1, [0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5, 10.5, 11.5, 12.5, 14]); // ` 1..0 - = Bksp
row(ROWS[2], 15, [0.75, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14.25]); // Tab q..p [ ] \
row(ROWS[3], 29, [0.875, 2.25, 3.25, 4.25, 5.25, 6.25, 7.25, 8.25, 9.25, 10.25, 11.25, 12.25, 13.875]); // Caps a..l ; ' Enter
row(ROWS[4], 42, [1.125, 2.75, 3.75, 4.75, 5.75, 6.75, 7.75, 8.75, 9.75, 10.75, 11.75, 13.625]); // LShift z../ RShift
KEYS[54] = [0.625, ROWS[5]]; // LCtrl
KEYS[103] = [1.875, ROWS[5]]; // Win
KEYS[56] = [3.125, ROWS[5]]; // LAlt
KEYS[57] = [6.875, ROWS[5]]; // Space
KEYS[58] = [10.625, ROWS[5]]; // RAlt
KEYS[104] = [11.875, ROWS[5]]; // right Win
KEYS[55] = [13.125, ROWS[5]]; // Fn
KEYS[59] = [14.375, ROWS[5]]; // RCtrl
KEYS[105] = [1.75, ROWS[4]]; // ISO key (sits under the left Shift on ANSI boards)
row(ROWS[0], 87, [0.5, 2.5, 3.5, 4.5, 5.5, 7, 8, 9, 10, 11.5, 12.5, 13.5, 14.5, 15.75, 16.75, 17.75]); // Esc F1..F12 PrtSc ScrLk Pause
KEYS[60] = [15.75, ROWS[1]]; // Ins
KEYS[61] = [15.75, ROWS[2]]; // Del
KEYS[62] = [15.75, ROWS[5]]; // Left
KEYS[63] = [16.75, ROWS[1]]; // Home
KEYS[64] = [16.75, ROWS[2]]; // End
KEYS[65] = [16.75, ROWS[4]]; // Up
KEYS[66] = [16.75, ROWS[5]]; // Down
KEYS[67] = [17.75, ROWS[1]]; // PgUp
KEYS[68] = [17.75, ROWS[2]]; // PgDn
KEYS[69] = [17.75, ROWS[5]]; // Right

// Numpad module, in its own coordinates (ids run down each column).
const PAD: Record<number, Point> = {
  70: [0.5, ROWS[1]], 71: [0.5, ROWS[2]], 72: [0.5, ROWS[3]], 73: [0.5, ROWS[4]], // NumLk 7 4 1
  74: [1.5, ROWS[1]], 75: [1.5, ROWS[2]], 76: [1.5, ROWS[3]], 77: [1.5, ROWS[4]], 78: [1, ROWS[5]], // / 8 5 2 0
  79: [2.5, ROWS[1]], 80: [2.5, ROWS[2]], 81: [2.5, ROWS[3]], 82: [2.5, ROWS[4]], 83: [2.5, ROWS[5]], // * 9 6 3 .
  84: [3.5, ROWS[1]], 85: [3.5, 2.7], 86: [3.5, 4.7], // - + Enter
};

// Edge light bars follow the outline of each case. The top bezel is tall (logo strip on
// the main block, the eight display keys on the numpad module).
const TOP = -1.96, BOTTOM = 5.88;
const MAIN: Case = { x0: -0.34, x1: 18.58, across: 23, first: 106 };
const PADBOX: Case = { x0: -0.38, x1: 4.4, across: 7, first: 170 };
const SIDE = 9;

interface Case {
  x0: number;
  x1: number;
  across: number; // lights along the top (and the bottom)
  first: number; // binding of the first light
}

export interface LampPosition {
  x: number;
  y: number;
  zone: 'keys' | 'numpad' | 'bars';
  bar: boolean;
}
const GAP = 0.1; // the numpad module sits flush against the main block

// Edge LEDs are numbered clockwise: top (left to right), right side (down),
// bottom (right to left), left side (up).
function perimeter(box: Case, index: number): Point | null {
  const { x0, x1, across } = box;
  const sideY = (k: number) => TOP + ((k + 1) / (SIDE + 1)) * (BOTTOM - TOP);
  const topX = (k: number) => x0 + (k / (across - 1)) * (x1 - x0);
  if (index < across) return [topX(index), TOP];
  index -= across;
  if (index < SIDE) return [x1, sideY(index)];
  index -= SIDE;
  if (index < across) return [topX(across - 1 - index), BOTTOM];
  index -= across;
  if (index < SIDE) return [x0, sideY(SIDE - 1 - index)];
  return null;
}

// Returns { x, y, zone, bar } for a lamp binding, or null when the binding is unknown.
// padSide: 1 = numpad attached on the left, 2 = on the right.
export function lampPosition(binding: number, padSide: number): LampPosition | null {
  const padShift = padSide === 2 ? MAIN.x1 + GAP - PADBOX.x0 : MAIN.x0 - GAP - PADBOX.x1;
  if (KEYS[binding]) return { x: KEYS[binding][0], y: KEYS[binding][1], zone: 'keys', bar: false };
  if (PAD[binding]) return { x: PAD[binding][0] + padShift, y: PAD[binding][1], zone: 'numpad', bar: false };
  const perMain = 2 * (MAIN.across + SIDE);
  if (binding >= MAIN.first && binding < MAIN.first + perMain) {
    const [x, y] = perimeter(MAIN, binding - MAIN.first)!;
    return { x, y, zone: 'bars', bar: true };
  }
  const perPad = 2 * (PADBOX.across + SIDE);
  if (binding >= PADBOX.first && binding < PADBOX.first + perPad) {
    const [x, y] = perimeter(PADBOX, binding - PADBOX.first)!;
    return { x: x + padShift, y, zone: 'numpad', bar: true };
  }
  return null;
}

// Browser key codes (KeyboardEvent.code) to the keyboard's key ids, for trying the
// key-reactive effects in the preview.
const CODES: Record<string, number> = {
  Backquote: 1, Minus: 12, Equal: 13, Backspace: 14, Tab: 15, BracketLeft: 26, BracketRight: 27, Backslash: 28,
  CapsLock: 29, Semicolon: 39, Quote: 40, Enter: 41, ShiftLeft: 42, Comma: 50, Period: 51, Slash: 52, ShiftRight: 53,
  ControlLeft: 54, AltLeft: 56, Space: 57, AltRight: 58, ControlRight: 59,
  Insert: 60, Delete: 61, ArrowLeft: 62, Home: 63, End: 64, ArrowUp: 65, ArrowDown: 66, PageUp: 67, PageDown: 68, ArrowRight: 69,
  NumLock: 70, Numpad7: 71, Numpad4: 72, Numpad1: 73, NumpadDivide: 74, Numpad8: 75, Numpad5: 76, Numpad2: 77, Numpad0: 78,
  NumpadMultiply: 79, Numpad9: 80, Numpad6: 81, Numpad3: 82, NumpadDecimal: 83, NumpadSubtract: 84, NumpadAdd: 85, NumpadEnter: 86,
  Escape: 87, PrintScreen: 100, ScrollLock: 101, Pause: 102, MetaLeft: 103, MetaRight: 104, ContextMenu: 104, IntlBackslash: 105,
};
'1234567890'.split('').forEach((d, i) => (CODES[`Digit${d}`] = 2 + i));
'QWERTYUIOP'.split('').forEach((c, i) => (CODES[`Key${c}`] = 16 + i));
'ASDFGHJKL'.split('').forEach((c, i) => (CODES[`Key${c}`] = 30 + i));
'ZXCVBNM'.split('').forEach((c, i) => (CODES[`Key${c}`] = 43 + i));
for (let f = 1; f <= 12; f++) CODES[`F${f}`] = 87 + f;

export const keyIdForCode = (code: string): number | null => CODES[code] ?? null;

// Keycap sizes in key units for the keys that are not 1 x 1.
const SIZES: Record<number, [w: number, h: number]> = {
  14: [2, 1], // Backspace
  15: [1.5, 1], 28: [1.5, 1], // Tab, backslash
  29: [1.75, 1], 41: [2.25, 1], // Caps, Enter
  42: [2.25, 1], 53: [2.75, 1], // Shifts
  54: [1.25, 1], 103: [1.25, 1], 56: [1.25, 1], 57: [6.25, 1], // LCtrl, Win, LAlt, Space
  58: [1.25, 1], 104: [1.25, 1], 55: [1.25, 1], 59: [1.25, 1], // RAlt, Win, Fn, RCtrl
  78: [2, 1], 85: [1, 2], 86: [1, 2], // numpad 0, +, Enter
};

// Width and height of the keycap a lamp sits under (edge LEDs have no keycap).
export function keySize(binding: number): [w: number, h: number] {
  return SIZES[binding] ?? [1, 1];
}

// The ISO key's light exists on every board but is covered by the wide Shift on ANSI ones.
export const ISO_KEY = 105;

// Outlines of the two cases and the eight display keys (which have no lights), for drawing.
export interface Chassis {
  main: { x0: number; y0: number; x1: number; y1: number };
  pad: { x0: number; y0: number; x1: number; y1: number };
  displayKeys: { x: number; y: number; size: number }[];
}

export function chassis(padSide: number): Chassis {
  const padShift = padSide === 2 ? MAIN.x1 + GAP - PADBOX.x0 : MAIN.x0 - GAP - PADBOX.x1;
  const displayKeys = [];
  for (const y of [-1.08, -0.04]) {
    for (const x of [0.61, 1.54, 2.46, 3.4]) displayKeys.push({ x: x + padShift, y, size: 0.78 });
  }
  return {
    main: { x0: MAIN.x0, y0: TOP, x1: MAIN.x1, y1: BOTTOM },
    pad: { x0: PADBOX.x0 + padShift, y0: TOP, x1: PADBOX.x1 + padShift, y1: BOTTOM },
    displayKeys, // in order: 1-4 back row, 5-8 front row
  };
}

// Rough placement for a lamp whose binding is not in the tables above: the position the
// keyboard reported, scaled onto the main block.
export function fallbackPosition(xUm: number, yUm: number): LampPosition {
  return {
    x: MAIN.x0 + ((xUm - 100000) / 333790) * (MAIN.x1 - MAIN.x0),
    y: TOP + (yUm / 140000) * (BOTTOM - TOP),
    zone: 'keys',
    bar: false,
  };
}
