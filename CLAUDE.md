# Dark Mount Studio

RGB lighting for the be quiet! Dark Mount keyboard. A C# tray app (`tray/`) renders effects and
streams them to the keyboard; a plain HTML/JS page (repo root, `js/`) edits its settings and draws
a preview. See `README.md` for the layout, the command line and the HTTP API.

## Product rules

- The page is a settings UI only. Nothing may depend on a browser tab staying open; anything live
  runs in the tray app.
- No build step and no dependencies for the page: plain ES modules, served as they are.
- Key presses are used for lighting only. Never log them, save them or expose them over the HTTP API.
- Do not bring back display-key picture upload, GIFs or screen mirroring unless asked.

## Build, run, test

```bash
cd tray
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

- The page files are embedded in the exe, so publish again after editing `index.html`,
  `styles.css` or `js/`. For page work, run `tray/publish/DarkMountStudio.exe --site .` instead
  (also the `studio` entry in `.claude/launch.json`).
- The app is single-instance and usually already running (Start with Windows). Stop it with
  `DarkMountStudio.exe --exit` before publishing and start it again afterwards. Never kill the
  process: the lights freeze on the last frame.
- `--selftest` drives the real keyboard for about 8 s and checks the HTTP API. It uses default
  settings in memory and does not touch `settings.json`.
- A `--site` instance reads and writes the real `%AppData%\DarkMountStudio\settings.json`. Back the
  file up before testing through the page and restore it afterwards. Read it with the shell, not
  with Python (the Store build of Python sees its own copy of AppData).
- There is no test suite. `dotnet build` must stay free of warnings.

## The two ports must stay in step

Every effect exists twice: `js/effects.js` (preview) and `tray/Effects.cs` + `tray/EffectsMore.cs`
(keyboard). Layers likewise: `js/layers.js` `layerLamps` and `tray/Renderer.cs` `LayerLayout`. The
lamp table is in `js/layout.js` and `tray/FallbackLayout.cs`, the geometry in `js/geometry.js` and
`tray/Geometry.cs`. Change both sides together.

Check a change by comparing `--dump-frame <effect> <t> [settings JSON] [beats]` with the same frame
rendered from `js/effects.js` in Node. The fixed inputs are described on `Program.DumpFrame`. The
output must match byte for byte; only `matrix` and `ripples` are random and cannot be compared. For
layers use `--dump-frame layers <t> '<settings JSON with layerPresets>'`.

Rules that keep the ports comparable:

- Effects are stateless functions of time and inputs. No `Math.round` and no `Math.random`: use
  `floor` and the `rand` / hash helpers.
- Never index the palette directly. Use `pick` / `pickR` / `palCyc` / `palLin`, so "Random colours"
  and the per-beat colour change work. Do not name a palette property after an Array method.
- An effect must work on a layer that is a single row, a single column or a single lamp
  (`u` / `v` are 0.5 there), and must index key events through the lamp list it is given.
- A new effect goes into `EFFECTS` and exactly one entry of `CATEGORIES` in `js/effects.js`, and
  into `Effects.All` in C#.

## Hardware facts

- USB `373F:0001`. Interface MI_03 is a standard HID LampArray with 201 lamps; each lamp's
  "binding" byte is the vendor key id (1–105 keys, 70–86 numpad keys, 106–169 main edge LEDs
  clockwise from top-left, 170–201 numpad edge LEDs). The positions the firmware reports are
  crude, hence `geometry.js`.
- The firmware is proprietary (not QMK/VIA) and stores only its eight built-in effects, so custom
  effects have to be streamed.
- The LEDs are linear: a colour that looks black on screen still glows. `tray/LedCurve.cs` applies
  the sRGB curve just before sending (setting `gamma`). Effects, preview and `--dump-frame` work
  with uncorrected colours.
- The keyboard forgets host control across sleep; `Engine.Reconnect` handles it.
