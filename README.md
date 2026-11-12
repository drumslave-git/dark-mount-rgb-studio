# Dark Mount Studio

Custom RGB lighting for the be quiet! Dark Mount keyboard: 74 effects, including ones that react to
sound and to typing, run by a small Windows tray app and set up in a page in your browser.

The keyboard can only store its eight built-in effects, so anything else has to be sent to it by a
program that keeps running. Dark Mount Studio is that program: a tray app of about 1 MB that draws
the effects 30 times a second. The settings page only changes settings, so you can close the tab
and the lights carry on.

This is an unofficial hobby project. It is not affiliated with or endorsed by be quiet! / Listan.

## What it does

- **74 effects in six groups**: Static, Motion, Nature, Pulse, Audio and Typing.
- **Audio effects** follow whatever is playing on the PC, on whichever output device is in use,
  and change colours on the beat.
- **Typing effects** react to key presses on the Dark Mount itself.
- **Paint**: colour single keys by hand and save the result as a preset.
- **Layers**: split the lights into groups and run a different effect on each one.
- **Live preview** of the whole board, with the numpad module on either side, in step with the
  keyboard.
- Lights the keys, the edge bars and the numpad module, each of which can be switched off.
- Hands the lights back to the keyboard's own effect when it is switched off, exits, or (for audio
  effects) when nothing is playing.

## Requirements

- Windows (tested on Windows 11)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run it
- .NET SDK 8 or later to build it
- A be quiet! Dark Mount (USB `373F:0001`)

## Build and run

```bash
cd tray
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

This writes `tray/publish/DarkMountStudio.exe`. The settings page is embedded in the exe, so that
one file is all you need. Start it and an icon appears in the tray:

- **Double-click** the icon, or choose **Open settings**, to open the page at
  <http://localhost:4173>.
- **Enabled** switches the effects on and off.
- **Start with Windows** adds or removes the app from your startup programs.

If Windows *Dynamic Lighting* is switched on for the keyboard, switch it off. It competes for the
same lights.

Settings are saved in `%AppData%\DarkMountStudio\settings.json`.

## How it works

```
 browser tab (index.html, js/)          tray app (tray/, C#)             keyboard
 settings + preview  ── HTTP, JSON ──▶  settings, effects, audio,  ── HID LampArray ──▶  201 lights
                        localhost:4173  key watching, 30 fps
```

- The keyboard exposes a standard HID LampArray interface. The tray app takes control of it and
  sends every frame over USB with [HidSharp](https://www.zer7.com/software/hidsharp).
- Sound is captured from the output device with WASAPI loopback
  ([NAudio](https://github.com/naudio/NAudio)) and turned into 32 frequency bands plus a beat count.
- Key presses come from Windows Raw Input, filtered to the Dark Mount, and only while a typing
  effect is selected. They are used for the lights and nothing else: never logged, saved or sent
  anywhere.
- The page is served on loopback only, and the server refuses requests that come from other sites.
- The effects exist twice: `js/effects.js` draws the preview and `tray/Effects*.cs` drives the
  keyboard. The two give the same numbers for the same moment.

### Layout

| Path | What it is |
| --- | --- |
| `index.html`, `styles.css` | The settings page |
| `js/app.js` | Page logic: settings, preview, painting, layer editor |
| `js/effects.js` | All effects, palettes and their parameters (preview copy) |
| `js/layers.js` | Layered presets |
| `js/geometry.js`, `js/layout.js` | Where each light sits and the keyboard's lamp table |
| `tray/Program.cs` | Entry point and command line |
| `tray/Engine.cs` | The loop that renders and sends frames |
| `tray/Effects.cs`, `tray/EffectsMore.cs`, `tray/Palette.cs`, `tray/Renderer.cs` | C# port of the effects and layers |
| `tray/Keyboard.cs` | HID LampArray access |
| `tray/AudioCapture.cs`, `tray/Spectrum.cs` | Sound capture, frequency bands, beat detection |
| `tray/KeyWatcher.cs` | Key presses for the typing effects |
| `tray/HttpServer.cs`, `tray/Settings.cs` | The page server, its API and the saved settings |
| `tray/TrayApp.cs` | Tray icon and menu |
| `tray/SelfTest.cs` | `--selftest` |

### Command line

| Command | What it does |
| --- | --- |
| `DarkMountStudio.exe` | Runs the tray app (one instance only) |
| `DarkMountStudio.exe --site <folder>` | Same, but serves the page from disk, for working on it |
| `DarkMountStudio.exe --exit` | Asks the running tray app to quit cleanly |
| `DarkMountStudio.exe --selftest` | Checks the keyboard, audio and the HTTP API and prints a report |
| `DarkMountStudio.exe --audio-devices` | Lists the output devices and which one would be listened to |
| `DarkMountStudio.exe --dump-frame <effect> <seconds> [settings JSON] [beats]` | Prints one rendered frame as JSON, for comparing with `js/effects.js` |

Stop the app with **Exit** in the tray menu or with `--exit`. If the process is killed instead, the
lights stay frozen on the last frame.

### HTTP API

| Request | Answer |
| --- | --- |
| `GET /api/state` | `{ settings, status, clock }` |
| `PUT /api/state` | Takes any part of the settings object, answers like `GET` |
| `GET /api/levels` | `{ levels: [32 numbers], beat }` |

## Working on it

Run the tray app with `--site .` from the repository root and it serves the page straight from the
working tree, so changes to `index.html`, `styles.css` and `js/` show up on reload. Publish again
to embed them in the exe.

When you change an effect, change it in both `js/effects.js` and the C# port, then compare the two
with `--dump-frame`.
