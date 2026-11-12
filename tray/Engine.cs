using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarkMountStudio;

/// <summary>What the settings page and the tray tooltip get to see.</summary>
sealed record EngineStatus(bool Keyboard, int Lamps, int[]? Bindings, string Audio, string Keys, bool Driving);

/// <summary>
/// The background loop: finds the keyboard, captures audio when an audio effect is selected,
/// watches key presses when a key effect is selected, renders the selected effect (or the
/// layers of the selected layered preset) 30 times a second and sends it. Whenever it stops
/// driving the lights (disabled, silence, exit, error) it tells the keyboard to run its own effect again.
/// </summary>
sealed class Engine : IDisposable
{
    const int Fps = 30;
    const int SilenceMs = 3000;      // hand the lights back after this much silence
    const int RetryMs = 3000;        // how often to look for the keyboard / check it is still there
    const int AudioCheckMs = 1000;   // how often to check which output device is playing

    readonly SettingsStore _store;
    readonly object _hidGate = new(); // all keyboard I/O happens under this lock
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    readonly Stopwatch _clock = Stopwatch.StartNew(); // the effects' time; key presses are stamped with it too
    readonly AudioCapture _audio = new();
    readonly KeyWatcher _keys;
    readonly List<KeyEvent> _keyEvents = new();
    readonly Spectrum _spectrum = new();
    readonly BeatDetector _beats = new();
    readonly Renderer _renderer = new();
    readonly float[] _samples = new float[Spectrum.FftSize];
    readonly float[] _levels = new float[Spectrum.Bands];
    byte[] _led = []; // the frame as sent when Settings.Gamma is on

    Keyboard? _keyboard;
    RawLamp[]? _lamps;
    int[]? _bindings;
    int[] _lampByBinding = [];
    bool _driving, _timerFast;
    int _disposed;
    volatile bool _stop, _reconnect;

    // Published for other threads.
    volatile float[] _publishedLevels = new float[Spectrum.Bands];
    volatile int _publishedBeat;
    volatile EngineStatus _status = new(false, 0, null, "unavailable", "off", false);

    /// <summary>Self-test: send every frame, even when it equals the previous one.</summary>
    public bool AlwaysSend { get; init; }

    public EngineStatus Status => _status;
    /// <summary>The 32 band levels the audio effects see (a fresh array each frame; do not modify).</summary>
    public float[] Levels => _publishedLevels;
    /// <summary>How many beats have been heard in the sound so far (see <see cref="BeatDetector"/>).</summary>
    public int Beat => _publishedBeat;
    /// <summary>The effects' clock in seconds. The settings page runs its preview on it, so both show the same moment.</summary>
    public double Now => _clock.Elapsed.TotalSeconds;

    // Diagnostics for the self-test.
    public long FramesRendered, FramesSent;
    public double MaxSendMs, TotalSendMs;
    public int Mapped;
    public bool LampTableFromDevice;
    public int MinIntervalUs;
    public bool? HandedBack;
    public string? AudioDevice => _audio.DeviceName;
    public string? AudioFormat => _audio.Format;
    public double InputPeakDb => _spectrum.InputPeakDb;
    public KeyWatcher Keys => _keys;
    /// <summary>Copy of the frame rendered last, before the LED curve (only kept when AlwaysSend is set).</summary>
    public volatile byte[]? LastFrame;

    public Engine(SettingsStore store)
    {
        _store = store;
        _keys = new KeyWatcher(() => _clock.Elapsed.TotalSeconds);
        _store.Changed += Wake;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lighting engine" };
    }

    public void Start() => _thread.Start();

    void Wake() => _wake.Set();

    /// <summary>Drops the keyboard handle and opens it again (after the PC wakes from sleep).</summary>
    public void Reconnect()
    {
        _reconnect = true;
        _wake.Set();
    }

    void Run()
    {
        var clock = _clock;
        long frameTicks = Stopwatch.Frequency / Fps;
        long nextFrame = 0, nextAudioCheck = 0, nextKeyboardTry = 0, nextPing = 0, lastSend = 0;
        double lastTime = 0;
        byte[] lastSent = [];
        Settings? seen = null;
        bool needsAudio = false, needsKeys = false, layered = false;
        try
        {
            while (!_stop)
            {
                long now = clock.ElapsedMilliseconds;
                double t = clock.Elapsed.TotalSeconds;
                double dt = Math.Min(0.25, t - lastTime);
                lastTime = t;
                var s = _store.Current;
                if (!ReferenceEquals(s, seen))
                {
                    // Which live inputs the selected effect - or any layer that has lights - needs.
                    seen = s;
                    var layers = s.ActiveLayers;
                    layered = layers != null;
                    var effects = layers != null
                        ? layers.Where(l => l.Lamps.Length > 0).Select(l => Effects.ById.GetValueOrDefault(l.Effect))
                        : [Effects.ById.GetValueOrDefault(s.Effect) ?? Effects.All[0]];
                    needsAudio = needsKeys = false;
                    foreach (var e in effects)
                    {
                        needsAudio |= e?.NeedsAudio == true;
                        needsKeys |= e?.NeedsKeys == true;
                    }
                }
                bool fast = false; // true while there is something to do 30 times a second
                string audioState = "unavailable";
                try
                {
                    // Audio is only captured while an audio effect is selected.
                    float[]? levels = null;
                    bool sound = false;
                    if (needsAudio)
                    {
                        if (now >= nextAudioCheck)
                        {
                            nextAudioCheck = now + AudioCheckMs;
                            _audio.EnsureRunning();
                        }
                        _spectrum.Update(_audio.CopyLatest(_samples) ? _samples : null, _audio.SampleRate, dt, _levels);
                        _beats.Update(_levels, dt);
                        _publishedLevels = (float[])_levels.Clone();
                        _publishedBeat = _beats.Count;
                        levels = _levels;
                        sound = _audio.Running && _audio.MsSinceSound < SilenceMs;
                        audioState = !_audio.Running ? "unavailable" : sound ? "listening" : "silent";
                        fast = true;
                    }
                    else if (nextAudioCheck != 0)
                    {
                        nextAudioCheck = 0;
                        _audio.Stop();
                        _publishedLevels = new float[Spectrum.Bands];
                    }

                    // Key presses are only watched while a key effect is running.
                    _keys.SetWanted(s.Enabled && needsKeys);

                    if (_reconnect)
                    {
                        _reconnect = false;
                        CloseKeyboard();
                        nextKeyboardTry = 0;
                    }
                    if (_keyboard == null && now >= nextKeyboardTry)
                    {
                        nextKeyboardTry = now + RetryMs;
                        nextPing = now + RetryMs;
                        OpenKeyboard();
                        lastSent = [];
                    }
                    if (_keyboard != null)
                    {
                        // With an audio effect the lights can go back to the keyboard while nothing is playing.
                        // A layered preset keeps the lights: its other layers go on, the audio ones just go dark.
                        bool drive = s.Enabled && (layered || !needsAudio || !s.HandBackWhenSilent || sound);
                        if (drive != _driving)
                        {
                            lock (_hidGate) _keyboard.SetAutonomous(!drive);
                            _driving = drive;
                            lastSent = [];
                        }
                        if (_driving)
                        {
                            if (needsKeys) _keys.Snapshot(_lampByBinding, _keyEvents);
                            byte[] rgb = _renderer.Render(s, _lamps!, t, dt, levels, needsKeys ? _keyEvents : null, _beats.Count);
                            if (AlwaysSend) LastFrame = (byte[])rgb.Clone();
                            FramesRendered++;
                            if (s.Gamma)
                            {
                                // From screen colours to what the LEDs need to look the same (see LedCurve).
                                if (_led.Length != rgb.Length) _led = new byte[rgb.Length];
                                LedCurve.Apply(rgb, _led);
                                rgb = _led;
                            }
                            // A frame identical to the last one is only re-sent once a second.
                            if (AlwaysSend || now - lastSend >= 1000 || !rgb.AsSpan().SequenceEqual(lastSent))
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                lock (_hidGate) _keyboard.SendFrame(rgb);
                                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                                TotalSendMs += ms;
                                MaxSendMs = Math.Max(MaxSendMs, ms);
                                FramesSent++;
                                if (lastSent.Length != rgb.Length) lastSent = new byte[rgb.Length];
                                rgb.CopyTo(lastSent, 0);
                                lastSend = now;
                            }
                            nextPing = now + RetryMs;
                            fast = true;
                        }
                        else if (now >= nextPing)
                        {
                            nextPing = now + RetryMs;
                            lock (_hidGate) _keyboard.Ping(); // notices an unplugged keyboard while idle
                        }
                    }
                }
                catch (Exception)
                {
                    // Any HID error: close, look for the keyboard again later.
                    CloseKeyboard();
                    nextKeyboardTry = clock.ElapsedMilliseconds + RetryMs;
                }
                _status = new EngineStatus(_keyboard != null, _keyboard?.LampCount ?? 0, _bindings, audioState, _keys.State, _driving);

                // Pacing. The loop is the only sender, so a slow send can never overlap the next
                // one; frames that would have been due meanwhile are dropped, not queued.
                SetTimerResolution(fast);
                if (!fast)
                {
                    _wake.WaitOne(250);
                    nextFrame = 0;
                    continue;
                }
                long ticks = clock.ElapsedTicks;
                nextFrame += frameTicks;
                if (nextFrame <= ticks) nextFrame = ticks;
                else Thread.Sleep((int)((nextFrame - ticks) * 1000 / Stopwatch.Frequency));
            }
        }
        finally
        {
            CloseKeyboard();
            _audio.Dispose();
            _keys.Dispose();
            SetTimerResolution(false);
        }
    }

    void OpenKeyboard()
    {
        var keyboard = Keyboard.TryOpen();
        if (keyboard == null) return;
        RawLamp[]? lamps = null;
        try { lamps = keyboard.ReadLamps(); }
        catch { /* fall back to the stored table */ }
        LampTableFromDevice = lamps != null;
        lamps ??= FallbackLayout.For(keyboard.LampCount);
        Renderer.BuildLayout(lamps, 1, out Mapped);
        MinIntervalUs = keyboard.MinIntervalUs;
        _lamps = lamps;
        lock (_hidGate) _keyboard = keyboard;
        _driving = false;
        _bindings = lamps.Select(l => l.Binding).ToArray();
        var lampByBinding = new int[256];
        Array.Fill(lampByBinding, -1);
        for (int id = 0; id < lamps.Length; id++)
            if (lamps[id].Binding is > 0 and < 256) lampByBinding[lamps[id].Binding] = id;
        _lampByBinding = lampByBinding;
        _keys.Clear();
    }

    /// <summary>Hands the lights back to the keyboard (report [6, 1]) and closes it.</summary>
    void CloseKeyboard()
    {
        lock (_hidGate)
        {
            if (_keyboard == null) return;
            try
            {
                _keyboard.SetAutonomous(true);
                HandedBack = true;
            }
            catch
            {
                HandedBack = false; // unplugged; it starts up in its own mode anyway
            }
            try { _keyboard.Dispose(); } catch { /* already gone */ }
            _keyboard = null;
        }
        _driving = false;
        _lamps = null;
        _bindings = null;
    }

    void SetTimerResolution(bool fast)
    {
        if (fast == _timerFast) return;
        _timerFast = fast;
        // Thread.Sleep is only accurate to ~15 ms unless a finer timer resolution is requested.
        if (fast) timeBeginPeriod(1);
        else timeEndPeriod(1);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    /// <summary>Stops the loop and makes sure the keyboard runs its own lighting again. Safe to call repeatedly and from any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(2000);
        // Normally the loop has already handed back on its way out. If it is stuck or never ran, do it here.
        if (Monitor.TryEnter(_hidGate, 1000))
        {
            try { CloseKeyboard(); }
            finally { Monitor.Exit(_hidGate); }
        }
        _store.Changed -= Wake;
    }
}
