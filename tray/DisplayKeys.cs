using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DarkMountStudio;

/// <summary>
/// Puts the pictures chosen for the eight display keys of the numpad module (Settings.DisplayKeys)
/// on the keyboard, which keeps them itself; nothing has to run for them to stay.
///
/// A key's picture is its image over its backlight colour, 120 × 120, turned a quarter clockwise
/// (as the keyboard stores it) and sent as a JPEG over the vendor channel (<see cref="QLink"/>).
/// Every picture sent is written to the keyboard's flash, so a key is only sent when it differs
/// from what the keyboard holds, and only once its settings have been left alone for a moment
/// (a colour picker changes the colour many times a second). After a start or a reconnect the
/// keyboard is asked what it holds before anything is written.
/// </summary>
sealed class DisplayKeyWriter : IDisposable
{
    public const int Count = 8;
    public const int Size = 120;
    const int FirstKeyId = 109;   // the keyboard's ids of the display keys: 1-4 back row, 5-8 front row
    const int Quality = 95;
    const int QuietMs = 700;      // a change is sent once the display keys' settings have stayed the same this long
    const int RetryMs = 5000;     // after a failure (numpad module detached, keyboard busy ...)

    readonly SettingsStore _store;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly byte[]?[] _onKeyboard = new byte[Count][]; // what each key holds as far as is known; null: not known
    volatile bool _stop, _keyboard, _recheck;
    long _lastChange;
    object? _noted;

    volatile string[] _states = new string[Count];
    volatile string _problem = "";

    /// <summary>Per key: "" (not set: the key keeps whatever it shows), "waiting", "sending", "done" or "failed" (unusable picture).</summary>
    public string[] States => _states;
    /// <summary>Why pictures that are waiting do not go out, or "".</summary>
    public string Problem => _problem;

    public DisplayKeyWriter(SettingsStore store)
    {
        _store = store;
        Array.Fill(_states, "");
        _noted = store.Current.DisplayKeys;
        _store.Changed += SettingsChanged;
        _thread = new Thread(Run) { IsBackground = true, Name = "Display keys" };
    }

    public void Start() => _thread.Start();

    /// <summary>The engine found the keyboard (at start, after unplugging, after sleep): check what its keys show.</summary>
    public void KeyboardFound()
    {
        _keyboard = true;
        _recheck = true;
        _wake.Set();
    }

    public void KeyboardLost() => _keyboard = false;

    void SettingsChanged()
    {
        var keys = _store.Current.DisplayKeys;
        if (ReferenceEquals(keys, Interlocked.Exchange(ref _noted, keys))) return;
        Interlocked.Exchange(ref _lastChange, Environment.TickCount64);
        _wake.Set();
    }

    static string Name(int k) => (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    DisplayKey? Wanted(int k) => _store.Current.DisplayKeys.GetValueOrDefault(Name(k));

    void Run()
    {
        var composedFrom = new DisplayKey?[Count]; // the settings each picture below was made from
        var pictures = new byte[]?[Count];         // null: not set, or unusable
        long retryAt = 0;
        while (!_stop)
        {
            if (_recheck)
            {
                _recheck = false;
                Array.Clear(_onKeyboard);
            }
            var todo = new List<int>();
            var states = new string[Count];
            for (int k = 0; k < Count; k++)
            {
                var key = Wanted(k);
                if (!Equals(key, composedFrom[k]))
                {
                    composedFrom[k] = key;
                    pictures[k] = key == null ? null : TryCompose(key);
                }
                states[k] = key == null ? "" : pictures[k] == null ? "failed" : Same(_onKeyboard[k], pictures[k]) ? "done" : "waiting";
                if (states[k] == "waiting") todo.Add(k);
            }
            _states = states;
            if (todo.Count == 0)
            {
                _problem = "";
                _wake.WaitOne();
                continue;
            }
            if (!_keyboard)
            {
                _problem = "The keyboard is not connected.";
                _wake.WaitOne();
                continue;
            }
            long now = Environment.TickCount64;
            long wait = Math.Max(Interlocked.Read(ref _lastChange) + QuietMs, retryAt) - now;
            if (wait > 0)
            {
                _wake.WaitOne((int)Math.Min(wait, int.MaxValue));
                continue;
            }

            try
            {
                using var link = QLink.TryOpen() ?? throw new IOException("The keyboard's settings channel could not be opened.");
                if (!link.NumpadAttached()) throw new IOException("The numpad module is not attached.");
                foreach (int k in todo)
                {
                    var key = composedFrom[k];
                    byte[] picture = pictures[k]!;
                    if (_stop || !Equals(Wanted(k), key)) break; // changed meanwhile: start over once it settles
                    int id = FirstKeyId + k;
                    if (_onKeyboard[k] == null && link.Holds(id, picture, Size, Size))
                    {
                        _onKeyboard[k] = picture;
                        continue;
                    }
                    states = (string[])states.Clone();
                    states[k] = "sending";
                    _states = states;
                    _onKeyboard[k] = null; // part written from here on
                    // On exit the picture is still finished: a half-written one shows garbage until the next start.
                    if (link.WriteImage(id, picture, Size, Size, () => Equals(Wanted(k), key))) _onKeyboard[k] = picture;
                }
                _problem = "";
                retryAt = 0;
            }
            catch (Exception e)
            {
                _problem = e is QLinkException or TimeoutException ? "The keyboard did not take the pictures: " + e.Message : e.Message;
                Array.Clear(_onKeyboard); // perhaps another keyboard by the next try
                retryAt = Environment.TickCount64 + RetryMs;
            }
        }
    }

    static bool Same(byte[]? a, byte[]? b) => a != null && b != null && (ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b));

    static byte[]? TryCompose(DisplayKey key)
    {
        try
        {
            return Compose(key);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or ExternalException or OutOfMemoryException)
        {
            return null; // not a picture GDI+ can read
        }
    }

    /// <summary>
    /// What a key gets: the image, fitted into 120 × 120, over the backlight colour, which shows
    /// wherever the image is transparent; turned 90° clockwise and encoded as JPEG.
    /// </summary>
    public static byte[] Compose(DisplayKey key)
    {
        double[] back = Settings.HexToRgb(key.Color);
        var upright = new byte[Size * Size * 4]; // BGRA, row by row; transparent where there is no image
        if (key.Image.Length > 0)
        {
            byte[] file = Convert.FromBase64String(key.Image[(key.Image.IndexOf(',') + 1)..]);
            using var stream = new MemoryStream(file);
            using var source = new Bitmap(stream);
            using var fitted = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fitted))
            using (var wrap = new ImageAttributes())
            {
                // Whole and centred; an image of exactly 120 × 120 (what the page sends) is copied pixel for pixel.
                bool exact = source.Width == Size && source.Height == Size;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                wrap.SetWrapMode(WrapMode.TileFlipXY); // no dark seams at the edges when scaling
                float k = Math.Min((float)Size / source.Width, (float)Size / source.Height);
                float w = source.Width * k, h = source.Height * k, x = (Size - w) / 2, y = (Size - h) / 2;
                g.DrawImage(source, [new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h)],
                    new RectangleF(0, 0, source.Width, source.Height), GraphicsUnit.Pixel, wrap);
            }
            var data = fitted.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < Size; row++) Marshal.Copy(data.Scan0 + row * data.Stride, upright, row * Size * 4, Size * 4);
            }
            finally
            {
                fitted.UnlockBits(data);
            }
        }
        // Mixed as a browser mixes them (on the stored values), so the key looks like the page's picture.
        var rgb = new byte[Size * Size * 3];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = ((Size - 1 - x) * Size + y) * 4; // stored pixel (x, y) is upright pixel (y, 119 - x)
                int a = upright[i + 3], o = (y * Size + x) * 3;
                rgb[o] = Mix(upright[i + 2], back[0], a);
                rgb[o + 1] = Mix(upright[i + 1], back[1], a);
                rgb[o + 2] = Mix(upright[i], back[2], a);
            }
        }
        return Jpeg.Encode(rgb, Size, Size, Quality);
    }

    static byte Mix(int over, double under, int alpha) => (byte)((over * alpha + (int)under * (255 - alpha) + 127) / 255);

    /// <summary>Stops the writer. A picture being written is finished first, if that takes no more than a few seconds.</summary>
    public void Dispose()
    {
        _store.Changed -= SettingsChanged;
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(3000);
    }
}
