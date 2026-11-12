using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DarkMountStudio;

/// <summary>
/// --selftest: runs the real engine and HTTP server for a few seconds and prints what happened.
/// Settings are kept in memory only, so settings.json is never touched.
/// </summary>
static class SelfTest
{
    static bool _ok = true;

    static void Check(string what, bool pass, string detail = "")
    {
        if (!pass) _ok = false;
        Console.WriteLine($"  [{(pass ? "ok" : "FAIL")}] {what}{(detail == "" ? "" : " - " + detail)}");
    }

    public static int Run(string? site)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"{Program.AppName} self-test");
        Console.WriteLine("This run uses default settings held in memory; settings.json is neither read nor written.");

        string FileStamp() => File.Exists(SettingsStore.DefaultPath)
            ? File.GetLastWriteTimeUtc(SettingsStore.DefaultPath).ToString("O", inv) + " " + new FileInfo(SettingsStore.DefaultPath).Length
            : "absent";
        string stampBefore = FileStamp();

        using var store = new SettingsStore(persist: false);
        // Audio spectrum, always driving (so frames are sent even when nothing is playing).
        store.Update(s =>
        {
            s.Enabled = true;
            s.Effect = "audio";
            s.HandBackWhenSilent = false;
        });
        var engine = new Engine(store) { AlwaysSend = true };
        using var server = new HttpServer(store, engine, site);
        bool keyboard = false;
        try
        {
            bool serving = server.Start();
            engine.Start();

            Console.WriteLine();
            Console.WriteLine("Keyboard");
            for (int i = 0; i < 50 && !engine.Status.Keyboard; i++) Thread.Sleep(100);
            var status = engine.Status;
            keyboard = status.Keyboard;
            Check("keyboard found (VID 373F PID 0001, LampArray interface)", keyboard);
            if (keyboard)
            {
                Console.WriteLine($"  lamp count: {status.Lamps}, minimum update interval: {engine.MinIntervalUs} us");
                Console.WriteLine($"  lamp table: {(engine.LampTableFromDevice ? "read from the keyboard" : "FALLBACK table (reading from the keyboard failed)")}");
                Check($"lamps mapped to positions: {engine.Mapped}/{status.Lamps}", engine.Mapped == status.Lamps);
                var lamps = Renderer.BuildLayout(status.Bindings!.Select(b => new RawLamp(0, 0, b)).ToArray(), 1, out _);
                Console.WriteLine($"  keys: {lamps.Count(l => !l.Bar)} ({lamps.Count(l => !l.Bar && l.Numpad)} on the numpad), " +
                                  $"edge LEDs: {lamps.Count(l => l.Bar)} ({lamps.Count(l => l.Bar && l.Numpad)} on the numpad)");
            }

            byte[] curve = Enumerable.Range(0, 256).Select(v => LedCurve.Of((byte)v)).ToArray();
            Check("LED curve: black stays off, full stays full, never goes down, dark colours go much darker",
                curve[0] == 0 && curve[255] == 255 && curve[20] == 2 && curve[128] == 55 && curve.Zip(curve.Skip(1)).All(p => p.First <= p.Second),
                $"20 -> {curve[20]}, 128 -> {curve[128]}, 200 -> {curve[200]}; in use: {store.Current.Gamma}");

            Console.WriteLine();
            Console.WriteLine("Audio (4 s of WASAPI loopback, effect \"audio\")");
            var peaks = new float[Spectrum.Bands];
            double inputPeakDb = double.NegativeInfinity;
            long rendered0 = engine.FramesRendered, sent0 = engine.FramesSent;
            int beat0 = engine.Beat;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 4000)
            {
                Thread.Sleep(30);
                float[] levels = engine.Levels;
                for (int b = 0; b < peaks.Length; b++) peaks[b] = Math.Max(peaks[b], levels[b]);
                inputPeakDb = Math.Max(inputPeakDb, engine.InputPeakDb);
            }
            double seconds = clock.Elapsed.TotalSeconds;
            long rendered = engine.FramesRendered - rendered0, sent = engine.FramesSent - sent0;
            status = engine.Status;
            Console.WriteLine($"  output device: {engine.AudioDevice ?? "none"} ({engine.AudioFormat ?? "capture not running"})");
            Console.WriteLine($"  audio state: {status.Audio}, input peak: {(double.IsNegativeInfinity(inputPeakDb) ? "silence" : inputPeakDb.ToString("0.0", inv) + " dBFS")}");
            Console.WriteLine($"  peak band levels (40 Hz .. 16 kHz): {string.Join(" ", peaks.Select(p => p.ToString("0.00", inv)))}");
            Console.WriteLine($"  beats heard: {engine.Beat - beat0} (random colours of the audio effects change on each)");
            Check("audio capture running", status.Audio != "unavailable");
            if (keyboard)
            {
                Console.WriteLine($"  frames rendered: {rendered}, frames sent: {sent} in {seconds.ToString("0.00", inv)} s ({(sent / seconds).ToString("0.0", inv)} fps)");
                if (engine.FramesSent > 0)
                    Console.WriteLine($"  send time per frame: average {(engine.TotalSendMs / engine.FramesSent).ToString("0.0", inv)} ms, longest {engine.MaxSendMs.ToString("0.0", inv)} ms");
                Check("driving the lights", status.Driving && sent > 0);
            }

            Console.WriteLine();
            Console.WriteLine($"HTTP server ({HttpServer.Url})");
            Check("listening", serving, serving ? "" : $"port {HttpServer.Port} is in use; HTTP checks skipped");
            if (serving) Http(engine);
        }
        finally
        {
            server.Dispose();
            engine.Dispose();
        }

        store.Flush();
        Console.WriteLine();
        Console.WriteLine("Shutdown");
        Check("settings.json untouched by this run", FileStamp() == stampBefore, stampBefore == "absent" ? "still absent" : "unchanged");
        if (keyboard) Check("keyboard told to run its own lighting again (report [6, 1])", engine.HandedBack == true);
        else Console.WriteLine("  no keyboard, nothing to hand back");
        Console.WriteLine();
        Console.WriteLine(_ok ? "SELF-TEST PASSED" : "SELF-TEST FAILED");
        return !keyboard ? 2 : _ok ? 0 : 3;
    }

    static void Http(Engine engine)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(HttpServer.Url), Timeout = TimeSpan.FromSeconds(5) };

        (HttpStatusCode Code, string Body, string? Type, string? Cache) Send(HttpMethod method, string path, string? json = null, string? origin = null, string? host = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (origin != null) request.Headers.Add("Origin", origin);
            if (host != null) request.Headers.Host = host;
            using var response = client.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return (response.StatusCode, reader.ReadToEnd(), response.Content.Headers.ContentType?.MediaType, response.Headers.CacheControl?.ToString());
        }

        try
        {
            var get = Send(HttpMethod.Get, "/api/state");
            using var state = JsonDocument.Parse(get.Body);
            var settings = state.RootElement.GetProperty("settings");
            var st = state.RootElement.GetProperty("status");
            Check("GET /api/state", get.Code == HttpStatusCode.OK && get.Type == "application/json" && get.Cache == "no-store",
                $"status = {st.GetRawText()[..Math.Min(st.GetRawText().Length, 70)]}...");
            Console.WriteLine($"         settings keys: {string.Join(", ", settings.EnumerateObject().Select(p => p.Name))}");
            Console.WriteLine($"         params.wave = {settings.GetProperty("params").GetProperty("wave").GetRawText()}");

            var levels = Send(HttpMethod.Get, "/api/levels");
            using var levelsDoc = JsonDocument.Parse(levels.Body);
            Check("GET /api/levels", levels.Code == HttpStatusCode.OK && levelsDoc.RootElement.GetProperty("levels").GetArrayLength() == Spectrum.Bands,
                $"{levelsDoc.RootElement.GetProperty("levels").GetArrayLength()} values");

            int before = settings.GetProperty("brightness").GetInt32();
            int changed = before == 50 ? 55 : 50;
            var put = Send(HttpMethod.Put, "/api/state", $"{{\"brightness\":{changed}}}", origin: HttpServer.Url);
            int inReply = JsonDocument.Parse(put.Body).RootElement.GetProperty("settings").GetProperty("brightness").GetInt32();
            int readBack = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement.GetProperty("settings").GetProperty("brightness").GetInt32();
            Check($"PUT /api/state brightness {before} -> {changed}", put.Code == HttpStatusCode.OK && inReply == changed && readBack == changed, $"reads back {readBack}");

            // params merge per effect and per key; zones merge per key; out-of-range values are clamped.
            var merged = JsonDocument.Parse(Send(HttpMethod.Put, "/api/state",
                "{\"params\":{\"wave\":{\"speed\":99}},\"zones\":{\"numpad\":false},\"effect\":\"no-such-effect\"}").Body).RootElement.GetProperty("settings");
            var wave = merged.GetProperty("params").GetProperty("wave");
            Check("PUT merges params and zones, clamps, ignores unknown values",
                wave.GetProperty("speed").GetDouble() == 3 && wave.GetProperty("scale").GetDouble() == 1 && wave.GetProperty("palette").GetArrayLength() == 6 &&
                merged.GetProperty("params").GetProperty("spiral").GetProperty("arms").GetDouble() == 2 &&
                !merged.GetProperty("zones").GetProperty("numpad").GetBoolean() && merged.GetProperty("zones").GetProperty("keys").GetBoolean() &&
                merged.GetProperty("effect").GetString() == "audio",
                $"wave.speed = {wave.GetProperty("speed").GetRawText()}, zones = {merged.GetProperty("zones").GetRawText()}");

            // Switching off must hand the lights back; a non-audio effect drives them without audio capture.
            JsonElement StatusAfter(string json, Func<JsonElement, bool> reached)
            {
                Send(HttpMethod.Put, "/api/state", json);
                JsonElement status = default;
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    status = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement.GetProperty("status");
                    if (reached(status)) break;
                }
                return status;
            }
            if (st.GetProperty("keyboard").GetBoolean())
            {
                var off = StatusAfter("{\"enabled\":false}", x => !x.GetProperty("driving").GetBoolean());
                Check("enabled = false stops driving the lights", !off.GetProperty("driving").GetBoolean() && off.GetProperty("keyboard").GetBoolean());
                var wavy = StatusAfter("{\"enabled\":true,\"effect\":\"wave\"}", x => x.GetProperty("driving").GetBoolean() && x.GetProperty("audio").GetString() == "unavailable");
                Check("enabled = true with effect \"wave\" drives the lights, audio capture off",
                    wavy.GetProperty("driving").GetBoolean() && wavy.GetProperty("audio").GetString() == "unavailable", $"audio = {wavy.GetProperty("audio").GetString()}");
            }

            // Key-reactive effects: watching only while enabled and such an effect is selected.
            string KeysAfter(string json, string wanted) =>
                StatusAfter(json, x => x.GetProperty("keys").GetString() == wanted).GetProperty("keys").GetString()!;
            string watching = KeysAfter("{\"enabled\":true,\"effect\":\"keypress\"}", "watching");
            Check("effect \"keypress\" while enabled: status.keys = watching", watching == "watching", $"keys = {watching}");

            // No key can be pressed from here, so feed pretend presses of Scroll Lock into the same
            // bookkeeping real input goes through (the Raw Input delivery itself stays untested).
            // Someone may be typing on the keyboard meanwhile: only the pretend key is looked at.
            var identity = Enumerable.Range(0, 256).ToArray();
            var events = new List<KeyEvent>();
            const ushort ScanScrollLock = 0x46, VkScrollLock = 0x91, Break = 1;
            const int BindingScrollLock = 101;
            List<KeyEvent> Pretend()
            {
                engine.Keys.Snapshot(identity, events);
                return events.Where(e => e.I == BindingScrollLock).ToList();
            }
            engine.Keys.Simulate(ScanScrollLock, 0, VkScrollLock);
            engine.Keys.Simulate(ScanScrollLock, 0, VkScrollLock); // auto-repeat
            var pretend = Pretend();
            Check("pretend key press recorded once, auto-repeat ignored", pretend.Count == 1 && pretend[0].R < 0, $"{pretend.Count} event(s)");
            int lamp = Array.IndexOf(engine.Status.Bindings ?? [], BindingScrollLock);
            if (lamp >= 0)
            {
                Thread.Sleep(200);
                byte[] frame = engine.LastFrame ?? [];
                bool lampLit = frame.Length > lamp * 3 + 2 && (frame[lamp * 3] | frame[lamp * 3 + 1] | frame[lamp * 3 + 2]) != 0;
                Check("rendered frame lights that key's lamp", lampLit, $"lamp {lamp}");
            }
            var live = Send(HttpMethod.Get, "/api/state");
            using (var liveDoc = JsonDocument.Parse(live.Body))
            {
                string Names(JsonElement e) => string.Join(",", e.EnumerateObject().Select(p => p.Name));
                var liveStatus = liveDoc.RootElement.GetProperty("status");
                Check("/api/state exposes no key data, only the state word",
                    Names(liveDoc.RootElement) == "settings,status,clock" &&
                    Names(liveStatus) == "keyboard,lamps,bindings,audio,keys,driving" &&
                    liveStatus.GetProperty("keys").ValueKind == JsonValueKind.String &&
                    Names(liveDoc.RootElement.GetProperty("settings")) == "enabled,effect,params,brightness,zones,numpadSide,paint,paintPresets,handBackWhenSilent,gamma,mode,layerPreset,layerPresets" &&
                    !live.Body.Contains("events", StringComparison.OrdinalIgnoreCase),
                    $"status fields: {Names(liveStatus)}");
            }
            engine.Keys.Simulate(ScanScrollLock, Break, VkScrollLock);
            engine.Keys.Simulate(ScanScrollLock, 0, VkScrollLock);
            pretend = Pretend();
            Check("release closes the press, the next press is a new event",
                pretend.Count == 2 && pretend[0].R >= pretend[0].T && pretend[1].R < 0 && pretend[1].N > pretend[0].N, $"{pretend.Count} event(s)");
            string keysOff = KeysAfter("{\"effect\":\"wave\"}", "off");
            engine.Keys.Snapshot(identity, events);
            Check("another effect: status.keys = off and the presses are forgotten", keysOff == "off" && events.Count == 0, $"keys = {keysOff}, {events.Count} event(s)");
            KeysAfter("{\"effect\":\"keyheat\"}", "watching");
            string disabled = KeysAfter("{\"enabled\":false}", "off");
            Check("key effect selected but disabled: status.keys = off", disabled == "off", $"keys = {disabled}");

            // "Random colours" per effect: a boolean in the effect's params; paint has no palette and ignores it.
            double speedBefore = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement
                .GetProperty("settings").GetProperty("params").GetProperty("wave").GetProperty("speed").GetDouble();
            var rnd = JsonDocument.Parse(Send(HttpMethod.Put, "/api/state",
                "{\"params\":{\"wave\":{\"random\":true},\"spiral\":{\"random\":\"yes\"},\"paint\":{\"random\":true}}}").Body)
                .RootElement.GetProperty("settings").GetProperty("params");
            var rndRead = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement.GetProperty("settings").GetProperty("params");
            Check("params.wave.random round-trips through PUT/GET; non-boolean and paint ignored",
                rnd.GetProperty("wave").GetProperty("random").GetBoolean() && rndRead.GetProperty("wave").GetProperty("random").GetBoolean() &&
                rndRead.GetProperty("wave").GetProperty("speed").GetDouble() == speedBefore &&
                !rndRead.GetProperty("spiral").GetProperty("random").GetBoolean() && !rndRead.GetProperty("rings").GetProperty("random").GetBoolean() &&
                !rndRead.GetProperty("paint").TryGetProperty("random", out _),
                $"wave = {rndRead.GetProperty("wave").GetRawText()}");

            // Paint presets: replaced as a whole, names trimmed, invalid entries dropped.
            string longName = new string('x', 41);
            var presets = JsonDocument.Parse(Send(HttpMethod.Put, "/api/state",
                "{\"paintPresets\":{\"  Test preset  \":{\"5\":\"#FF0000\",\"x\":\"#00ff00\",\"7\":\"red\"},\"   \":{\"1\":\"#ffffff\"},\"" + longName + "\":{\"1\":\"#ffffff\"},\"Second\":{\"12\":\"#00ff00\"}}}")
                .Body).RootElement.GetProperty("settings").GetProperty("paintPresets").GetRawText();
            string presetsRead = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement.GetProperty("settings").GetProperty("paintPresets").GetRawText();
            Check("paintPresets round-trips through PUT/GET (trimmed, validated)",
                presets == "{\"Test preset\":{\"5\":\"#ff0000\"},\"Second\":{\"12\":\"#00ff00\"}}" && presetsRead == presets, presetsRead);
            string replaced = JsonDocument.Parse(Send(HttpMethod.Put, "/api/state", "{\"paintPresets\":{\"Only\":{}}}").Body)
                .RootElement.GetProperty("settings").GetProperty("paintPresets").GetRawText();
            Check("PUT paintPresets replaces the whole object", replaced == "{\"Only\":{}}", replaced);

            // Layered presets: replaced as a whole and validated; the selected one is drawn layer by layer.
            const string layersJson =
                "{\"mode\":\"layers\",\"layerPreset\":7,\"layerPresets\":[{\"name\":\"  Desk  \",\"layers\":[" +
                "{\"name\":\"\",\"effect\":\"solid\",\"params\":{\"palette\":[\"#FF0000\"],\"speed\":2},\"lamps\":[2,0,1,1,-4,99999,\"x\"]}," +
                "{\"name\":\"Gone\",\"effect\":\"no-such-effect\",\"lamps\":[9]}," +
                "{\"name\":\"Blue\",\"effect\":\"solid\",\"params\":{\"palette\":[\"#0000ff\"]},\"lamps\":[12]}," +
                "{\"name\":\"Typing\",\"effect\":\"keypress\",\"lamps\":[]}]}]}";
            var layered = JsonDocument.Parse(Send(HttpMethod.Put, "/api/state", layersJson).Body).RootElement.GetProperty("settings");
            string layersBack = layered.GetProperty("layerPresets").GetRawText();
            Check("layerPresets validated (names, unknown effect dropped, lamp ids sorted, foreign params ignored)",
                layersBack == "[{\"name\":\"Desk\",\"layers\":[" +
                "{\"name\":\"Layer 1\",\"effect\":\"solid\",\"params\":{\"random\":false,\"palette\":[\"#ff0000\"]},\"lamps\":[0,1,2]}," +
                "{\"name\":\"Blue\",\"effect\":\"solid\",\"params\":{\"random\":false,\"palette\":[\"#0000ff\"]},\"lamps\":[12]}," +
                "{\"name\":\"Typing\",\"effect\":\"keypress\",\"params\":{\"fade\":0.6,\"random\":false,\"palette\":[\"#ff6a13\",\"#ffd166\",\"#00e5ff\",\"#7c4dff\"]},\"lamps\":[]}]}]" &&
                layered.GetProperty("mode").GetString() == "layers" && layered.GetProperty("layerPreset").GetInt32() == 0,
                layersBack.Length > 150 ? layersBack[..150] + "..." : layersBack);
            if (st.GetProperty("keyboard").GetBoolean())
            {
                // The key layer has no lights, so nothing needs key presses; lights outside the layers are off.
                var layerStatus = StatusAfter("{\"enabled\":true,\"brightness\":100,\"zones\":{\"keys\":true,\"bars\":true,\"numpad\":true}}",
                    x => x.GetProperty("driving").GetBoolean() && x.GetProperty("keys").GetString() == "off");
                Thread.Sleep(200);
                byte[] frame = engine.LastFrame ?? [];
                string Rgb(int lamp) => frame.Length > lamp * 3 + 2 ? $"{frame[lamp * 3]},{frame[lamp * 3 + 1]},{frame[lamp * 3 + 2]}" : "none";
                Check("layered preset drawn: each layer on its own lights, the rest off",
                    layerStatus.GetProperty("driving").GetBoolean() && layerStatus.GetProperty("keys").GetString() == "off" &&
                    Rgb(0) == "255,0,0" && Rgb(2) == "255,0,0" && Rgb(12) == "0,0,255" && Rgb(3) == "0,0,0" && Rgb(9) == "0,0,0",
                    $"lamp 0 = {Rgb(0)}, lamp 12 = {Rgb(12)}, lamp 3 = {Rgb(3)}");
            }
            string keysForLayer = KeysAfter(
                "{\"enabled\":true,\"layerPresets\":[{\"name\":\"Keys\",\"layers\":[{\"name\":\"Typing\",\"effect\":\"keypress\",\"lamps\":[30,31]}]}]}", "watching");
            Check("a key effect in a layer with lights: status.keys = watching", keysForLayer == "watching", $"keys = {keysForLayer}");
            string keysSingle = KeysAfter("{\"mode\":\"effect\",\"effect\":\"wave\"}", "off");
            Check("back to a single effect: status.keys = off", keysSingle == "off", $"keys = {keysSingle}");

            var restored = Send(HttpMethod.Put, "/api/state", settings.GetRawText());
            string after = JsonDocument.Parse(Send(HttpMethod.Get, "/api/state").Body).RootElement.GetProperty("settings").GetRawText();
            Check("previous settings restored (incl. paintPresets, random and layerPresets)", restored.Code == HttpStatusCode.OK && after == settings.GetRawText(), $"brightness {before}");

            Check("PUT with a body that is not JSON is refused", Send(HttpMethod.Put, "/api/state", "nonsense").Code == HttpStatusCode.BadRequest);
            var foreign = Send(HttpMethod.Get, "/api/state", origin: "http://example.com");
            Check("request from another origin is refused", foreign.Code == HttpStatusCode.Forbidden, $"{(int)foreign.Code}");
            var otherHost = Send(HttpMethod.Get, "/api/state", host: "example.com");
            Check("request for another host name is refused", otherHost.Code is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{(int)otherHost.Code}");

            var index = Send(HttpMethod.Get, "/");
            Check("GET /", index.Code == HttpStatusCode.OK && index.Type == "text/html" && index.Cache == "no-store", $"{index.Body.Length} chars, {index.Type}");
            var css = Send(HttpMethod.Get, "/styles.css");
            Check("GET /styles.css", css.Code == HttpStatusCode.OK && css.Type == "text/css", $"{css.Body.Length} chars, {css.Type}");
            foreach (string name in new[] { "app.js", "effects.js", "geometry.js", "layers.js", "layout.js" })
            {
                var js = Send(HttpMethod.Get, "/js/" + name);
                Check($"GET /js/{name}", js.Code == HttpStatusCode.OK && js.Type == "text/javascript", $"{js.Body.Length} chars, {js.Type}");
            }
            Check("paths outside the site are not served",
                Send(HttpMethod.Get, "/js/..%2fProgram.cs").Code == HttpStatusCode.NotFound &&
                Send(HttpMethod.Get, "/server.js").Code == HttpStatusCode.NotFound &&
                Send(HttpMethod.Get, "/js/%2e%2e/%2e%2e/tray/Program.cs").Code == HttpStatusCode.NotFound);
        }
        catch (Exception e)
        {
            Check("HTTP checks", false, e.GetType().Name + ": " + e.Message);
        }
    }
}
