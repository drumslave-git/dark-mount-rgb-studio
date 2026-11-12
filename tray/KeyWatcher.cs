using System.Runtime.InteropServices;

namespace DarkMountStudio;

/// <summary>
/// Notices key presses on the Dark Mount for the key-reactive effects, using Windows Raw Input
/// on a hidden message-only window (its own thread).
///
/// Privacy: it only listens while such an effect is running, only to this keyboard, and all it
/// keeps is a short in-memory list of "which light, pressed when, released when". Nothing is
/// logged, saved or served over HTTP; the list is wiped as soon as listening stops.
/// </summary>
sealed class KeyWatcher : IDisposable
{
    const string DeviceTag = "VID_373F&PID_0001";
    const int MaxEvents = 256;
    const double KeepSeconds = 30; // events released longer ago than this are dropped

    readonly record struct Press(int Binding, double T, double R, int N);

    readonly Func<double> _clock; // seconds, the same clock the effects use
    readonly object _gate = new();
    readonly List<Press> _events = new(); // oldest first
    int _count;

    readonly Dictionary<IntPtr, bool> _ours = new(); // window thread only: raw input device handle -> is it the Dark Mount
    Thread? _thread;
    volatile IntPtr _hwnd;
    volatile bool _wanted, _registered, _failed;

    public KeyWatcher(Func<double> clock) => _clock = clock;

    /// <summary>"watching" while registered for key input, "unavailable" if Windows refused, else "off".</summary>
    public string State => !_wanted ? "off" : _registered ? "watching" : _failed ? "unavailable" : "off";

    /// <summary>Starts or stops listening. Stopping forgets all presses at once.</summary>
    public void SetWanted(bool wanted)
    {
        if (wanted == _wanted) return;
        _wanted = wanted;
        if (!wanted) Clear();
        if (_thread == null)
        {
            if (!wanted) return;
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Key watcher" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start(); // registers on its way in
        }
        else if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_APP_APPLY, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }

    /// <summary>
    /// Copies the current presses into `into` (oldest first), translated to lamp indices with
    /// lampByBinding (binding -> lamp index, -1 = no such light).
    /// </summary>
    public void Snapshot(int[] lampByBinding, List<KeyEvent> into)
    {
        into.Clear();
        lock (_gate)
        {
            Prune(_clock());
            foreach (var e in _events)
            {
                int lamp = e.Binding < lampByBinding.Length ? lampByBinding[e.Binding] : -1;
                if (lamp >= 0) into.Add(new KeyEvent(lamp, e.T, e.R, e.N));
            }
        }
    }

    void Prune(double now) => _events.RemoveAll(e => e.R >= 0 && now - e.R > KeepSeconds);

    void Record(int binding, bool released)
    {
        lock (_gate)
        {
            if (!_wanted) return;
            double now = _clock();
            int open = _events.FindLastIndex(e => e.Binding == binding && e.R < 0);
            if (released)
            {
                if (open >= 0) _events[open] = _events[open] with { R = now };
                return;
            }
            if (open >= 0) return; // auto-repeat of a key that is still held
            Prune(now);
            if (_events.Count >= MaxEvents) _events.RemoveAt(0);
            _events.Add(new Press(binding, now, -1, _count));
            _count = (_count + 1) & int.MaxValue;
        }
    }

    /// <summary>Self-test only: feeds one key transition through the same path as real input (minus the device check).</summary>
    public void Simulate(ushort makeCode, ushort flags, ushort vkey)
    {
        int binding = BindingFor(makeCode, flags, vkey);
        if (binding != 0) Record(binding, (flags & RI_KEY_BREAK) != 0);
    }

    // ------------------------------------------------------------ scan codes

    static readonly byte[] Plain = new byte[0x60], Extended = new byte[0x60]; // make code -> binding (0 = none)

    static KeyWatcher()
    {
        static void Run(byte[] table, int from, int to, int binding)
        {
            for (int code = from; code <= to; code++) table[code] = (byte)(binding + code - from);
        }
        Run(Plain, 0x01, 0x01, 87);  // Esc
        Run(Plain, 0x02, 0x0B, 2);   // 1..0
        Run(Plain, 0x0C, 0x0F, 12);  // - = Backspace Tab
        Run(Plain, 0x10, 0x19, 16);  // Q..P
        Run(Plain, 0x1A, 0x1B, 26);  // [ ]
        Run(Plain, 0x1C, 0x1C, 41);  // Enter
        Run(Plain, 0x1D, 0x1D, 54);  // LCtrl
        Run(Plain, 0x1E, 0x26, 30);  // A..L
        Run(Plain, 0x27, 0x28, 39);  // ; '
        Run(Plain, 0x29, 0x29, 1);   // `
        Run(Plain, 0x2A, 0x2A, 42);  // LShift
        Run(Plain, 0x2B, 0x2B, 28);  // \
        Run(Plain, 0x2C, 0x32, 43);  // Z..M
        Run(Plain, 0x33, 0x36, 50);  // , . / RShift
        Run(Plain, 0x37, 0x37, 79);  // KP*
        Run(Plain, 0x38, 0x39, 56);  // LAlt Space
        Run(Plain, 0x3A, 0x3A, 29);  // Caps
        Run(Plain, 0x3B, 0x44, 88);  // F1..F10
        Run(Plain, 0x45, 0x45, 70);  // NumLock
        Run(Plain, 0x46, 0x46, 101); // ScrLk
        Plain[0x47] = 71; Plain[0x48] = 75; Plain[0x49] = 80; Plain[0x4A] = 84; // KP7 KP8 KP9 KP-
        Plain[0x4B] = 72; Plain[0x4C] = 76; Plain[0x4D] = 81; Plain[0x4E] = 85; // KP4 KP5 KP6 KP+
        Plain[0x4F] = 73; Plain[0x50] = 77; Plain[0x51] = 82;                   // KP1 KP2 KP3
        Plain[0x52] = 78; Plain[0x53] = 83;                                     // KP0 KP.
        Plain[0x56] = 105;           // ISO key
        Run(Plain, 0x57, 0x58, 98);  // F11 F12

        Extended[0x1C] = 86;  // KP Enter
        Extended[0x1D] = 59;  // RCtrl
        Extended[0x35] = 74;  // KP/
        Extended[0x37] = 100; // PrtSc
        Extended[0x38] = 58;  // RAlt
        Extended[0x47] = 63; Extended[0x48] = 65; Extended[0x49] = 67; // Home Up PgUp
        Extended[0x4B] = 62; Extended[0x4D] = 69;                      // Left Right
        Extended[0x4F] = 64; Extended[0x50] = 66; Extended[0x51] = 68; // End Down PgDn
        Extended[0x52] = 60; Extended[0x53] = 61;                      // Ins Del
        Extended[0x5B] = 103; Extended[0x5C] = 104; Extended[0x5D] = 104; // LWin, right Win, Menu
    }

    /// <summary>The keyboard's key id (lamp binding) for a raw key, or 0 for keys without a light. The Fn key never reaches Windows.</summary>
    static int BindingFor(ushort makeCode, ushort flags, ushort vkey)
    {
        bool e0 = (flags & RI_KEY_E0) != 0;
        if (vkey == 0xFF) return 0;                                         // not a real key
        if (e0 && makeCode is 0x2A or 0x36 or 0xAA or 0xB6) return 0;       // fake shifts around navigation keys
        if (vkey == 0x13) return 102;                                       // Pause (arrives as E1 1D)
        if (vkey == 0x90) return 70;                                        // NumLock, whatever its scan code
        if (makeCode >= Plain.Length) return 0;
        return e0 ? Extended[makeCode] : Plain[makeCode];
    }

    // ------------------------------------------------------------- raw input

    const int WM_INPUT = 0x00FF, WM_APP_APPLY = 0x8001, WM_APP_QUIT = 0x8002;
    const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIM_TYPEKEYBOARD = 1;
    const ushort RI_KEY_BREAK = 1, RI_KEY_E0 = 2;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUT { public RAWINPUTHEADER Header; public RAWKEYBOARD Keyboard; public ulong Spare1, Spare2, Spare3; } // room to spare

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUT data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, [Out] char[] data, ref uint size);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    sealed class Window(KeyWatcher owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == WM_INPUT) owner.OnInput(m.LParam);
                else if (m.Msg == WM_APP_APPLY) owner.Apply();
                else if (m.Msg == WM_APP_QUIT)
                {
                    owner._wanted = false;
                    owner.Apply();
                    owner._hwnd = IntPtr.Zero;
                    DestroyHandle();
                    Application.ExitThread();
                    return;
                }
            }
            catch
            {
                // never let a hiccup here take the app down
            }
            base.WndProc(ref m); // WM_INPUT must reach DefWindowProc so Windows can clean up
        }
    }

    void MessageLoop()
    {
        try
        {
            var window = new Window(this);
            window.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
            _hwnd = window.Handle;
            Apply();
            Application.Run();
        }
        catch
        {
            _failed = true;
        }
    }

    // Window thread: brings the raw input registration in line with _wanted.
    void Apply()
    {
        bool wanted = _wanted;
        if (wanted == _registered) return;
        // Usage page 1, usage 6 = keyboards. INPUTSINK: also while another window has the focus.
        var device = new RAWINPUTDEVICE
        {
            UsagePage = 1, Usage = 6,
            Flags = wanted ? RIDEV_INPUTSINK : RIDEV_REMOVE,
            Target = wanted ? _hwnd : IntPtr.Zero,
        };
        bool ok = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _failed = wanted && !ok;
        _registered = wanted && ok;
        if (!_registered)
        {
            _ours.Clear();
            Clear();
        }
    }

    void OnInput(IntPtr rawInput)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUT>();
        if (GetRawInputData(rawInput, RID_INPUT, out RAWINPUT raw, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue) return;
        if (raw.Header.Type != RIM_TYPEKEYBOARD || !IsOurs(raw.Header.Device)) return;
        int binding = BindingFor(raw.Keyboard.MakeCode, raw.Keyboard.Flags, raw.Keyboard.VKey);
        if (binding != 0) Record(binding, (raw.Keyboard.Flags & RI_KEY_BREAK) != 0);
    }

    // Only this keyboard counts; other keyboards (and synthetic input, which has no device) are ignored.
    bool IsOurs(IntPtr device)
    {
        if (_ours.TryGetValue(device, out bool ours)) return ours;
        var name = new char[512];
        uint size = (uint)name.Length;
        uint copied = device == IntPtr.Zero ? 0 : GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, name, ref size);
        ours = copied != uint.MaxValue && copied > 0 &&
               new string(name, 0, (int)Math.Min(copied, (uint)name.Length)).Contains(DeviceTag, StringComparison.OrdinalIgnoreCase);
        if (_ours.Count > 64) _ours.Clear();
        _ours[device] = ours;
        return ours;
    }

    public void Dispose()
    {
        _wanted = false;
        Clear();
        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_thread is { IsAlive: true } thread && thread != Thread.CurrentThread) thread.Join(1000);
    }
}
