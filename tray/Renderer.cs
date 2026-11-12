namespace DarkMountStudio;

/// <summary>
/// Draws frames: lays the lamps out, runs the selected effect - or, with a layered preset, each
/// layer's effect on that layer's lights - then applies brightness and zones. The same steps as
/// rebuildLayout() and loop() in js/app.js and js/layers.js.
/// </summary>
sealed class Renderer
{
    /// <summary>One layer of a layered preset, ready to draw.</summary>
    sealed class Pass
    {
        public required EffectDef Effect;
        public readonly Frame Frame = new();          // the layer's own lamps, output and scratch
        public int[] Ids = [];                        // lamp id of each of the layer's lamps
        public int[] LocalOf = [];                    // lamp id -> index in Ids, or -1
        public bool Random;
        public int Seed;
        public readonly List<KeyEvent> Keys = new();  // the key presses that belong to this layer
    }

    /// <summary>A layer is never treated as wider than this many times its height (js/layers.js: MAX_ASPECT).</summary>
    const double MaxAspect = 4;

    readonly Frame _frame = new();
    Settings? _settings;
    RawLamp[]? _raw;
    int _padSide;
    EffectDef _effect = Effects.All[0];
    Pass[]? _passes; // null while a single effect runs on the whole board
    bool[] _lit = [];
    double _brightness = 1;
    bool _random;
    int _seed = Random.Shared.Next(1_000_000); // for "Random colours"; redrawn when the effect or the option changes

    /// <summary>Use this seed for random colours instead of drawing one (--dump-frame).</summary>
    public int? FixedSeed { get; init; }

    /// <summary>How many lamps of the current table have a known binding.</summary>
    public int Mapped { get; private set; }

    /// <summary>Normalised lamp positions, as rebuildLayout() in js/app.js computes them.</summary>
    public static EffectLamp[] BuildLayout(RawLamp[] raw, int padSide, out int mapped)
    {
        mapped = 0;
        var pts = new LampPos[raw.Length];
        double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y0 = x0, y1 = x1;
        for (int id = 0; id < raw.Length; id++)
        {
            var known = Geometry.LampPosition(raw[id].Binding, padSide);
            if (known != null) mapped++;
            var p = pts[id] = known ?? Geometry.FallbackPosition(raw[id].X, raw[id].Y);
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        double w = x1 - x0, h = y1 - y0;
        if (!(w > 0)) w = 1;
        if (!(h > 0)) h = 1;
        var lamps = new EffectLamp[raw.Length];
        for (int id = 0; id < raw.Length; id++)
        {
            var p = pts[id];
            double v = (p.Y - y0) / h;
            lamps[id] = new EffectLamp(
                id,
                raw[id].Binding,
                (p.X - x0) / w,
                v,
                (p.X - (x0 + x1) / 2) / h,
                (p.Y - y0) / h - 0.5,
                Math.Abs(Math.Sin(id * 12.9898) * 43758.5453) % 1,
                p.Bar, p.Numpad, v);
        }
        return lamps;
    }

    /// <summary>
    /// The lights of one layer as its effect sees them: positions are measured over the layer's
    /// own extent, so the effect plays on those lights as if they were a whole keyboard
    /// (layerLamps() in js/layers.js). ids must be valid indexes into `board`.
    /// </summary>
    public static EffectLamp[] LayerLayout(EffectLamp[] board, int[] ids)
    {
        var lamps = new EffectLamp[ids.Length];
        if (ids.Length == 0) return lamps;
        double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y0 = x0, y1 = x1;
        foreach (int id in ids)
        {
            ref readonly var l = ref board[id];
            x0 = Math.Min(x0, l.Cx); x1 = Math.Max(x1, l.Cx);
            y0 = Math.Min(y0, l.Cy); y1 = Math.Max(y1, l.Cy);
        }
        double w = x1 - x0, h = y1 - y0;
        double unit = Math.Max(h, w / MaxAspect);
        if (!(unit > 0)) unit = 1;
        double mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        for (int k = 0; k < ids.Length; k++)
        {
            ref readonly var l = ref board[ids[k]];
            lamps[k] = l with
            {
                U = w > 0 ? (l.Cx - x0) / w : 0.5,
                V = h > 0 ? (l.Cy - y0) / h : 0.5,
                Cx = (l.Cx - mx) / unit,
                Cy = (l.Cy - my) / unit,
            };
        }
        return lamps;
    }

    /// <summary>
    /// Renders one frame and returns the RGB buffer (3 bytes per lamp id; reused between calls).
    /// t and dt are in seconds; levels are the 32 audio band levels or null; keys are the recent key presses;
    /// beat is the number of beats heard in the sound so far.
    /// </summary>
    public byte[] Render(Settings s, RawLamp[] raw, double t, double dt, float[]? levels, IReadOnlyList<KeyEvent>? keys = null, int beat = 0)
    {
        var f = _frame;
        if (!ReferenceEquals(raw, _raw) || s.NumpadSide != _padSide)
        {
            f.Lamps = BuildLayout(raw, s.NumpadSide, out int mapped);
            f.Out = new byte[raw.Length * 3];
            Mapped = mapped;
            _raw = raw;
            _padSide = s.NumpadSide;
            _settings = null;
        }
        if (!ReferenceEquals(s, _settings))
        {
            var paint = new Dictionary<int, double[]>();
            foreach (var (id, hex) in s.Paint) paint[int.Parse(id)] = Settings.HexToRgb(hex);

            if (s.ActiveLayers is { } layers)
            {
                var passes = new Pass[layers.Count];
                for (int k = 0; k < passes.Length; k++)
                {
                    var layer = layers[k];
                    var effect = Effects.ById.GetValueOrDefault(layer.Effect) ?? Effects.All[0];
                    bool random = layer.Params.Random == true && effect.Defaults.Palette != null;
                    var pass = passes[k] = new Pass { Effect = effect, Random = random, Seed = Random.Shared.Next(1_000_000) };
                    // Scratch (and the random colours) live as long as the layer keeps its effect.
                    if (_passes != null && k < _passes.Length && _passes[k].Effect == effect)
                    {
                        pass.Frame.St = _passes[k].Frame.St;
                        if (_passes[k].Random == random) pass.Seed = _passes[k].Seed;
                    }
                    pass.Ids = layer.Lamps.Where(id => id < f.Lamps.Length).ToArray();
                    pass.LocalOf = new int[f.Lamps.Length];
                    Array.Fill(pass.LocalOf, -1);
                    for (int i = 0; i < pass.Ids.Length; i++) pass.LocalOf[pass.Ids[i]] = i;
                    pass.Frame.Lamps = LayerLayout(f.Lamps, pass.Ids);
                    pass.Frame.Out = new byte[pass.Ids.Length * 3];
                    pass.Frame.Paint = paint;
                    SetParams(pass.Frame, effect, layer.Params, random ? FixedSeed ?? pass.Seed : null);
                }
                _passes = passes;
            }
            else
            {
                var effect = Effects.ById.GetValueOrDefault(s.Effect) ?? Effects.All[0];
                var p = s.Params.GetValueOrDefault(effect.Id) ?? effect.Defaults;
                bool random = p.Random == true && effect.Defaults.Palette != null;
                if (effect != _effect) f.St = new Scratch(); // scratch lives as long as the effect stays selected
                if (effect != _effect || random != _random) _seed = Random.Shared.Next(1_000_000);
                _effect = effect;
                _random = random;
                _passes = null;
                f.Paint = paint;
                SetParams(f, effect, p, random ? FixedSeed ?? _seed : null);
            }

            _brightness = s.Brightness / 100.0;
            _lit = new bool[f.Lamps.Length];
            for (int i = 0; i < _lit.Length; i++)
            {
                var l = f.Lamps[i];
                _lit[i] = (l.Bar ? s.Zones.Bars : s.Zones.Keys) && (!l.Numpad || s.Zones.Numpad);
            }
            _settings = s;
        }

        var o = f.Out;
        if (_passes == null)
        {
            f.T = t;
            f.Dt = dt;
            f.Levels = levels;
            f.Beat = beat;
            f.Keys = keys ?? [];
            _effect.Render(f);
        }
        else
        {
            Array.Clear(o); // lights that are in no layer stay off
            foreach (var pass in _passes)
            {
                if (pass.Ids.Length == 0) continue;
                var pf = pass.Frame;
                pf.T = t;
                pf.Dt = dt;
                pf.Levels = levels;
                pf.Beat = beat;
                pass.Keys.Clear();
                if (keys != null && pass.Effect.NeedsKeys)
                {
                    // A layer reacts to its own keys; each press is re-addressed to the layer's lamp list.
                    foreach (var ev in keys)
                    {
                        if ((uint)ev.I < (uint)pass.LocalOf.Length && pass.LocalOf[ev.I] >= 0) pass.Keys.Add(ev with { I = pass.LocalOf[ev.I] });
                    }
                }
                pf.Keys = pass.Keys;
                pass.Effect.Render(pf);
                var po = pf.Out;
                for (int k = 0; k < pass.Ids.Length; k++)
                {
                    int at = pass.Ids[k] * 3;
                    o[at] = po[k * 3];
                    o[at + 1] = po[k * 3 + 1];
                    o[at + 2] = po[k * 3 + 2];
                }
            }
        }

        for (int i = 0; i < _lit.Length; i++)
        {
            double k = _lit[i] ? _brightness : 0;
            if (k == 1) continue;
            o[i * 3] = Effects.Clamp8(o[i * 3] * k);
            o[i * 3 + 1] = Effects.Clamp8(o[i * 3 + 1] * k);
            o[i * 3 + 2] = Effects.Clamp8(o[i * 3 + 2] * k);
        }
        return o;
    }

    /// <summary>Puts an effect's parameters and palette into the frame it draws with.</summary>
    static void SetParams(Frame f, EffectDef effect, EffectParams p, int? seed)
    {
        double Value(string name) => p.Get(name) ?? effect.Defaults.Get(name) ?? 0;
        f.Speed = Value("speed"); f.Scale = Value("scale"); f.Angle = Value("angle"); f.Density = Value("density");
        f.Width = Value("width"); f.Arms = Value("arms"); f.Gain = Value("gain"); f.Fade = Value("fade");
        var palette = p.Palette is { Length: > 0 } ? p.Palette : effect.Defaults.Palette ?? ["#ffffff"];
        f.Pal = new Palette(palette.Select(Settings.HexToRgb).ToArray(), seed);
    }
}
