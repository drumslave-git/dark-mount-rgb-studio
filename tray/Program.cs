using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DarkMountStudio;

/// <summary>
/// Dark Mount Studio: a tray app that runs lighting effects on a be quiet! Dark Mount keyboard
/// (also sound- and key-reactive ones) and serves their settings page on http://localhost:4173.
///
///   DarkMountStudio.exe                       the tray app (single instance)
///   DarkMountStudio.exe --site &lt;folder&gt;       same, but serve the page from disk (development)
///   DarkMountStudio.exe --exit                ask the running tray app to quit cleanly
///   DarkMountStudio.exe --selftest            check keyboard, audio and the HTTP API, print a report
///   DarkMountStudio.exe --dump-frame &lt;effect&gt; &lt;seconds&gt; [settings JSON] [beats]   print one rendered frame as JSON
///                                            (effect "layers": the layered preset selected in the settings JSON)
///   DarkMountStudio.exe --audio-devices       list the output devices and which one would be listened to
///   DarkMountStudio.exe --version             print the version
/// </summary>
static class Program
{
    public const string AppName = "Dark Mount Studio";
    /// <summary>"1.2.3" in a release (the release workflow sets it from the git tag), "dev" in a local build.</summary>
    public static readonly string AppVersion =
        typeof(Program).Assembly.GetName().Version is { } v && (v.Major | v.Minor | v.Build) != 0 ? v.ToString(3) : "dev";
    const string MutexName = @"Local\DarkMountStudio.SingleInstance";
    const string ExitEventName = @"Local\DarkMountStudio.Exit";

    [STAThread]
    static int Main(string[] args)
    {
        int at = Array.IndexOf(args, "--dump-frame");
        if (at >= 0)
        {
            AttachConsole();
            return args.Length > at + 2
                ? DumpFrame(args[at + 1], args[at + 2], args.Length > at + 3 ? args[at + 3] : null,
                    args.Length > at + 4 && int.TryParse(args[at + 4], out int beat) ? beat : 0)
                : Usage("--dump-frame <effectId> <tSeconds> [settings JSON] [beats heard]");
        }
        if (args.Contains("--version"))
        {
            AttachConsole();
            Console.WriteLine(AppVersion);
            return 0;
        }
        if (args.Contains("--audio-devices"))
        {
            AttachConsole();
            AudioCapture.PrintDevices(Console.Out);
            return 0;
        }
        if (args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            return 0;
        }
        string? site = null;
        at = Array.IndexOf(args, "--site");
        if (at >= 0)
        {
            if (args.Length <= at + 1 || !Directory.Exists(args[at + 1]))
            {
                AttachConsole();
                return Usage("--site <existing folder>");
            }
            site = Path.GetFullPath(args[at + 1]);
        }
        bool selfTest = args.Contains("--selftest");
        if (selfTest) AttachConsole();

        // One instance only: two would fight over the keyboard and the port.
        using var mutex = new Mutex(false, MutexName);
        bool mine;
        try { mine = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { mine = true; }
        if (!mine)
        {
            if (selfTest) Console.WriteLine($"{AppName} is already running; quit it first (tray menu > Exit, or --exit).");
            return selfTest ? 1 : 0;
        }
        try
        {
            return selfTest ? SelfTest.Run(site) : RunTray(site);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    static int RunTray(string? site)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();

        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        using var store = new SettingsStore();
        using var engine = new Engine(store);
        using var server = new HttpServer(store, engine, site);

        // Whatever way the process ends, the keyboard gets its own lighting back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => engine.Dispose();
        Application.ApplicationExit += (_, _) => engine.Dispose();
        Application.ThreadException += (_, _) =>
        {
            engine.Dispose();
            Application.Exit();
        };
        SessionEndedEventHandler sessionEnded = (_, _) => engine.Dispose(); // log-off / shutdown
        PowerModeChangedEventHandler powerChanged = (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) engine.Reconnect(); // the keyboard forgets host control across sleep
        };
        SystemEvents.SessionEnded += sessionEnded;
        SystemEvents.PowerModeChanged += powerChanged;
        try
        {
            server.Start(); // if the port is taken the app still runs; the tooltip says so
            engine.Start();
            using var tray = new TrayApp(store, engine, server, exitSignal);
            Application.Run(tray);
        }
        finally
        {
            SystemEvents.SessionEnded -= sessionEnded;
            SystemEvents.PowerModeChanged -= powerChanged;
            server.Dispose();
            engine.Dispose();
        }
        return 0;
    }

    /// <summary>
    /// Prints one frame as a JSON array of RGB bytes per lamp id, for comparing with web/src/lib/effects.ts ("npm run frame" in web/ prints the same from it):
    /// stored lamp table, numpad on the left, default parameters, brightness 100, all zones on,
    /// levels[b] = clamp(0.3 + 0.5 sin b), `beat` beats heard (fourth argument, 0 if left out), paint = { 5: #ff0000 }, fresh scratch state, dt = 1/30,
    /// key presses = lamp 60 pressed at T-1.2 and released at T-1.0, lamp 95 held since T-0.4,
    /// lamp 60 pressed again at T-0.15 and released at T-0.05. With "random": true in the
    /// effect's params (third argument) the palette seed is 1234. With "layers" for the effect,
    /// the layered preset that the settings JSON selects is drawn instead of a single effect.
    /// </summary>
    static int DumpFrame(string effectId, string seconds, string? settingsJson, int beat)
    {
        bool layers = effectId == "layers";
        if (!layers && !Effects.ById.ContainsKey(effectId))
            return Usage($"--dump-frame <effectId> <tSeconds>; effects: layers, {string.Join(", ", Effects.All.Select(e => e.Id))}");
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
            return Usage("--dump-frame <effectId> <tSeconds>");
        var settings = Settings.Defaults();
        if (settingsJson != null)
        {
            // Optional: the same JSON a PUT /api/state takes, to compare with other parameters or palettes.
            try
            {
                using var patch = System.Text.Json.JsonDocument.Parse(settingsJson);
                settings.Apply(patch.RootElement);
            }
            catch (System.Text.Json.JsonException)
            {
                return Usage("--dump-frame <effectId> <tSeconds> [settings JSON]");
            }
        }
        if (layers) settings.Mode = "layers";
        else settings.Effect = effectId;
        if (layers && settings.ActiveLayers == null) return Usage("--dump-frame layers <tSeconds> <settings JSON with layerPresets>");
        settings.Paint["5"] = "#ff0000";
        var levels = new float[Spectrum.Bands];
        for (int b = 0; b < levels.Length; b++) levels[b] = (float)Math.Clamp(0.3 + 0.5 * Math.Sin(b), 0, 1);
        KeyEvent[] keys = [new(60, t - 1.2, t - 1.0, 0), new(95, t - 0.4, -1, 1), new(60, t - 0.15, t - 0.05, 2)];
        byte[] rgb = new Renderer { FixedSeed = 1234 }.Render(settings, FallbackLayout.Lamps, t, 1.0 / 30, levels, keys, beat);
        Console.WriteLine("[" + string.Join(",", rgb) + "]");
        return 0;
    }

    static int Usage(string text)
    {
        Console.Error.WriteLine("Usage: DarkMountStudio " + text);
        return 2;
    }

    /// <summary>This is a windowless program; when started from a terminal without redirection, borrow its console for output.</summary>
    static void AttachConsole()
    {
        IntPtr stdout = GetStdHandle(-11);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(-1);
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
}
