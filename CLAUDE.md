# Dark Mount Studio

RGB lighting for the be quiet! Dark Mount keyboard. A C# tray app (`tray/`) renders effects and
streams them to the keyboard; a page (`web/`: TypeScript, React, Tailwind, built with Vite) edits
its settings and draws a preview. See `README.md` for the layout, the command line and the HTTP API.

## Product rules

- The page is a settings UI only. Nothing may depend on a browser tab staying open; anything live
  runs in the tray app.
- The app is portable. Everything it saves goes into `settings.json` next to the exe
  (`SettingsStore`). Never write to AppData, temp folders or anywhere else, and keep the page free
  of `localStorage`, cookies and the like: a page preference worth keeping becomes a setting. The
  only thing outside the folder is the opt-in "Start with Windows" registry entry.
- The page ships inside the exe as three static files and loads nothing from the network. Its only
  runtime dependencies are React and React DOM; ask before adding another.
- Key presses are used for lighting only. Never log them, save them or expose them over the HTTP API.
- Display-key pictures are written to the keyboard only when they change and once the change has
  settled, because every write goes to its flash. Do not bring back GIFs on the display keys or
  screen mirroring unless asked.

## Build, run, test

```bash
cd tray
dotnet publish -c Release -o publish
```

- The owner runs a release build from `D:\Apps\DarkMountStudio\` (Start with Windows points
  there); `settings.json` in that folder holds their real settings. Leave that folder to the
  owner. `tray/publish/` is the dev build: publish over it, but never delete the folder or its
  `settings.json` (an older copy of the owner's settings).
- Building the exe builds the page first (`npm ci` if needed, then `npm run build` in `web/`; the
  `BuildSite` target in the csproj) and embeds `web/dist`, so it needs Node.js 22.18 or later.
  Publish again after editing anything in `web/`.
- For page work, run `npm run dev` in `web/` (the `studio` entry in `.claude/launch.json`): Vite
  serves the page on <http://localhost:5173> with hot reload and passes `/api` on to the tray app
  on port 4173, which has to be running. `DarkMountStudio.exe --site web/dist` serves a built page
  from disk instead.
- `npm run build` type-checks with `tsc` and must stay free of errors. Vite must keep writing
  exactly `index.html`, `assets/app.js` and `assets/app.css`: the csproj embeds those by name.
- The app is single-instance and the owner's copy is usually running. Publishing only needs the
  dev copy stopped. To try a dev build, stop the running copy with `DarkMountStudio.exe --exit`
  and start `tray/publish/DarkMountStudio.exe`; when done, stop it the same way and start
  `D:\Apps\DarkMountStudio\DarkMountStudio.exe` again. Each copy points Start with Windows at
  itself when it starts (`Autostart.FollowMove`), so always end with the owner's copy running.
  Never kill the process: the lights freeze on the last frame.
- `--selftest` drives the real keyboard for about 8 s and checks the HTTP API. It uses default
  settings in memory and does not touch `settings.json`.
- Testing through the page (the dev server too) changes the `settings.json` of whichever copy is
  running. With the owner's copy those are their real settings: note the values you change and
  put them back afterwards, through the page or the HTTP API. Never write `settings.json` while the app runs: it keeps its settings in memory, never reads the
  file again and saves over it, or loads the stale file on its next start.
- There is no test suite. `dotnet build` must stay free of warnings.

## Commits and releases

Every push to `main` that touches `tray/` or `web/` publishes a
release (`.github/workflows/release.yml`). The version comes from the last `vX.Y.Z` tag and the
commit subjects since then (`.github/scripts/next-version.sh`), so write them accordingly:
`feat: ...` for a new feature (minor), `feat!: ...` or `fix!: ...` for a breaking change (major),
anything else is a patch. Never set the version by hand: the csproj stays at `0.0.0-dev` and the
workflow passes `-p:Version`. The workflow also runs `--version` and `--dump-frame wave 1` on the
built exe, so both must keep working without a keyboard.

## The page

- `web/src/lib/` is the lighting logic (effects, layers, geometry, board layout). It must not touch
  the DOM or import React, and its imports name the `.ts` file, so Node can run it as it is.
- `store.ts` holds the state, `tray.ts` talks to the tray app, `actions.ts` has every change the
  user can make, `preview.ts` draws the keyboard picture, `components/` is the React UI.
- State is never changed in place: an action builds new objects and queues the same change for the
  tray app. `preview.ts` and the components tell what changed by comparing objects.
- Only syntax that disappears when the types are removed (`erasableSyntaxOnly`): no enums, no
  namespaces, no parameter properties.
- Styling is Tailwind utilities in the components; colours and type sizes are the `@theme` tokens
  in `web/src/index.css`. Shared looks go into `components/ui.tsx`, not into new CSS classes.

## The two ports must stay in step

Every effect exists twice: `web/src/lib/effects.ts` (preview) and `tray/Effects.cs` +
`tray/EffectsMore.cs` (keyboard). Layers likewise: `layers.ts` `layerLamps` and `tray/Renderer.cs`
`LayerLayout`; the board layout: `board.ts` `buildBoard` and `Renderer.BuildLayout`. The lamp
table is in `layout.ts` and `tray/FallbackLayout.cs`, the geometry in `geometry.ts` and
`tray/Geometry.cs`. Change both sides together.

Check a change by comparing the two ports' frames, which take the same arguments:

```bash
cd web
npm run --silent frame -- wave 1
../tray/publish/DarkMountStudio.exe --dump-frame wave 1
```

Both take `<effect> <t> [settings JSON] [beats]`; the fixed inputs are described on
`Program.DumpFrame` and mirrored in `web/scripts/frame.ts`. The output must match byte for byte;
only `matrix` and `ripples` are random and cannot be compared. For layers use
`layers <t> '<settings JSON with layerPresets>'`.

Rules that keep the ports comparable:

- Effects are stateless functions of time and inputs. No `Math.round` and no `Math.random`: use
  `floor` and the `rand` / hash helpers.
- Never index the palette directly. Use `pick` / `pickR` / `palCyc` / `palLin`, so "Random colours"
  and the per-beat colour change work. Do not name a palette property after an Array method.
- An effect must work on a layer that is a single row, a single column or a single lamp
  (`u` / `v` are 0.5 there), and must index key events through the lamp list it is given.
- A new effect goes into `EFFECTS` and exactly one entry of `CATEGORIES` in `effects.ts`, and
  into `Effects.All` in C#.

## Hardware facts

- USB `373F:0001`. Interface MI_03 is a standard HID LampArray with 201 lamps; each lamp's
  "binding" byte is the vendor key id (1–105 keys, 70–86 numpad keys, 106–169 main edge LEDs
  clockwise from top-left, 170–201 numpad edge LEDs). The positions the firmware reports are
  crude, hence `geometry.ts`.
- The firmware is proprietary (not QMK/VIA) and stores only its eight built-in effects, so custom
  effects have to be streamed.
- The LEDs are linear: a colour that looks black on screen still glows. `tray/LedCurve.cs` applies
  the sRGB curve just before sending (setting `gamma`). Effects, preview and `--dump-frame` work
  with uncorrected colours.
- The keyboard forgets host control across sleep; `Engine.Reconnect` handles it.
- Interface MI_02 (usage page 0xFF00) is the vendor channel ("QLink", `tray/QLink.cs`) that the
  official configurator uses. The eight display keys (ids 109–116, 1–4 the back row) store a
  120 × 120 baseline JPEG each (4:4:4 works), turned 90° clockwise. Writing takes about 10 ms per
  49 bytes; the self-test only reads them.
