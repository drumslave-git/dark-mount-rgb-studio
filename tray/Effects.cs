namespace DarkMountStudio;

/// <summary>One light as the effects see it. u/v run 0..1 across the board (v = 1 is the front edge);
/// cx/cy are centred with equal scale (cy spans -0.5..0.5). Binding is the keyboard's key id,
/// Bar marks an edge LED and Numpad a light on the numpad module (`pad` in the JS).
/// Inside a layer, u/v/cx/cy are measured over the layer's own lights (see Renderer.LayerLayout);
/// BoardV is always v over the whole board (`bv` in the JS), which tells the key rows apart.</summary>
readonly record struct EffectLamp(int Id, int Binding, double U, double V, double Cx, double Cy, double Rnd, bool Bar, bool Numpad, double BoardV);

/// <summary>Scratch state that lives as long as an effect stays selected (in a layer: as long as the layer keeps its effect).</summary>
sealed class Scratch
{
    public float[]? Smooth;
    public MatrixColumn[]? Cols;
    public List<Ripple>? Rip;
    public double[]? Acc, Heat, Mul;
}

sealed class MatrixColumn { public double Y, Sp; }
readonly record struct Ripple(double X, double Y, double T0, double C);

/// <summary>One key press: I = lamp index of the key, T = time of the press and R = time of the release
/// (below 0 while still held), both in seconds on the same clock as Frame.T, N = running count of presses.</summary>
readonly record struct KeyEvent(int I, double T, double R, int N);

/// <summary>Everything an effect needs to draw one frame (the `ctx` of web/src/lib/effects.ts).</summary>
sealed class Frame
{
    public double T, Dt;                                        // seconds
    public EffectLamp[] Lamps = [];
    public byte[] Out = [];                                     // 3 bytes per lamp, written like a Uint8ClampedArray
    public double Speed, Scale, Angle, Density, Width, Arms, Gain, Fade; // p
    public Palette Pal = new([[255, 255, 255]]);
    public Scratch St = new();
    public float[]? Levels;                                     // io.levels: 32 values in 0..1, bass first
    public int Beat;                                            // io.beat: how many beats have been heard so far
    public IReadOnlyDictionary<int, double[]> Paint = new Dictionary<int, double[]>(); // io.paint
    public IReadOnlyList<KeyEvent> Keys = [];                   // io.keys.events, oldest first
}

/// <summary>The live input an effect depends on.</summary>
enum Needs { Nothing, Audio, Keys }

sealed record EffectDef(string Id, string[] Params, EffectParams Defaults, Needs Needs, Action<Frame> Render)
{
    public bool NeedsAudio => Needs == Needs.Audio;
    public bool NeedsKeys => Needs == Needs.Keys;
}

/// <summary>
/// Lighting effects: a C# port of web/src/lib/effects.ts, which draws the preview in the settings page.
/// Same ids, parameters, defaults and maths - keep the two in step when changing an effect.
/// This file holds the shared helpers, the table of all effects and the first batch of
/// effects; EffectsMore.cs holds the rest.
/// </summary>
static partial class Effects
{
    const double Tau = Math.PI * 2;

    static double Frac(double x) => x - Math.Floor(x);
    static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary>Stores a number the way a JS Uint8ClampedArray does: round half to even, clamp to 0..255.</summary>
    public static byte Clamp8(double v) => !(v > 0) ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v, MidpointRounding.ToEven);

    // Math.hypot as V8 computes it (scaled, Kahan-summed) so results match the page bit for bit.
    static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        double max = Math.Max(x, y);
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        for (int i = 0; i < 2; i++)
        {
            double n = (i == 0 ? x : y) / max;
            double summand = n * n - compensation;
            double preliminary = sum + summand;
            compensation = preliminary - sum - summand;
            sum = preliminary;
        }
        return Math.Sqrt(sum) * max;
    }

    // Integer hash with 32-bit wraparound, as Math.imul gives in JS.
    internal static double Hash3(int x, int y, int z)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + z * 1274126177;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    static double Lerp(double a, double b, double f) => a + (b - a) * f;

    static double Noise3(double x, double y, double z)
    {
        double xfl = Math.Floor(x), yfl = Math.Floor(y), zfl = Math.Floor(z);
        int xi = unchecked((int)(long)xfl), yi = unchecked((int)(long)yfl), zi = unchecked((int)(long)zfl);
        double xf = x - xfl, yf = y - yfl, zf = z - zfl;
        xf = xf * xf * (3 - 2 * xf);
        yf = yf * yf * (3 - 2 * yf);
        zf = zf * zf * (3 - 2 * zf);
        double Plane(int zz) => Lerp(
            Lerp(Hash3(xi, yi, zz), Hash3(unchecked(xi + 1), yi, zz), xf),
            Lerp(Hash3(xi, unchecked(yi + 1), zz), Hash3(unchecked(xi + 1), unchecked(yi + 1), zz), xf),
            yf);
        return Lerp(Plane(zi), Plane(unchecked(zi + 1)), zf);
    }

    static void Put(byte[] o, int i, double[] c, double b = 1)
    {
        o[i * 3] = Clamp8(c[0] * b);
        o[i * 3 + 1] = Clamp8(c[1] * b);
        o[i * 3 + 2] = Clamp8(c[2] * b);
    }

    // Palette lookup that wraps around (last colour blends back into the first). With random
    // colours it does not wrap, unless `period` says after how many colours it must repeat.
    static void PalCyc(Palette pal, double t, byte[] o, int i, double b = 1, double period = 0)
    {
        pal.Cyc(t, period, out double[] a, out double[] c, out double f);
        o[i * 3] = Clamp8((a[0] + (c[0] - a[0]) * f) * b);
        o[i * 3 + 1] = Clamp8((a[1] + (c[1] - a[1]) * f) * b);
        o[i * 3 + 2] = Clamp8((a[2] + (c[2] - a[2]) * f) * b);
    }

    // Palette lookup from first colour (t = 0) to last colour (t = 1).
    static void PalLin(Palette pal, double t, byte[] o, int i, double b = 1)
    {
        pal.Lin(t, out double[] a, out double[] c, out double f);
        o[i * 3] = Clamp8((a[0] + (c[0] - a[0]) * f) * b);
        o[i * 3 + 1] = Clamp8((a[1] + (c[1] - a[1]) * f) * b);
        o[i * 3 + 2] = Clamp8((a[2] + (c[2] - a[2]) * f) * b);
    }

    // Audio bands with fast attack and slow decay. The result lives in the effect's scratch object.
    // With random colours, the palette gets new ones on every beat.
    static float[] SmoothLevels(Frame c, double gain)
    {
        c.Pal.NewColoursOnBeat(c.Beat);
        var smooth = c.St.Smooth ??= new float[32];
        var lv = c.Levels;
        for (int b = 0; b < 32; b++)
        {
            double target = lv != null ? Clamp01(lv[b] * gain) : 0;
            smooth[b] = (float)(target > smooth[b] ? target : smooth[b] * 0.88);
        }
        return smooth;
    }

    static readonly string[] Rainbow = ["#ff0000", "#ffd000", "#00ff40", "#00e5ff", "#2040ff", "#ff00d0"];
    static readonly string[] Sunset = ["#ff3d00", "#ff9100", "#ff2d78", "#6a1b9a"];
    static readonly string[] OceanPalette = ["#001a66", "#0066ff", "#00e5ff", "#e0ffff"];
    static readonly string[] FirePalette = ["#000000", "#7a0c00", "#ff3d00", "#ffb300", "#fff3c4"];
    static readonly string[] Ice = ["#00102e", "#0b63ce", "#7fdbff", "#ffffff"];
    static readonly string[] AuroraPalette = ["#000814", "#00c853", "#00e5ff", "#7c4dff"];
    static readonly string[] Candy = ["#ff4fa3", "#ffd166", "#06d6a0", "#4cc9f0"];

    /// <summary>Allowed range of each numeric parameter (PARAMS in web/src/lib/effects.ts).</summary>
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> ParamRanges = new Dictionary<string, (double, double)>
    {
        ["speed"] = (0, 3), ["scale"] = (0.2, 4), ["angle"] = (0, 360), ["density"] = (0.05, 1),
        ["width"] = (0.05, 1), ["arms"] = (1, 6), ["gain"] = (0.3, 4), ["fade"] = (0.1, 5),
    };

    // Same order as EFFECTS in web/src/lib/effects.ts.
    public static readonly EffectDef[] All =
    [
        new("wave", ["speed", "scale", "angle"], new() { Speed = 0.8, Scale = 1, Angle = 0, Palette = Rainbow }, Needs.Nothing, Wave),
        new("spiral", ["speed", "scale", "arms"], new() { Speed = 0.8, Scale = 1, Arms = 2, Palette = Rainbow }, Needs.Nothing, Spiral),
        new("rings", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = Candy }, Needs.Nothing, Rings),
        new("plasma", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = Sunset }, Needs.Nothing, Plasma),
        new("aurora", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = AuroraPalette }, Needs.Nothing, Aurora),
        new("fire", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = FirePalette }, Needs.Nothing, Fire),
        new("lava", ["speed", "scale"], new() { Speed = 0.6, Scale = 1, Palette = ["#14002e", "#b3005e", "#ff5a00", "#ffd166"] }, Needs.Nothing, Lava),
        new("matrix", ["speed", "density"], new() { Speed = 1, Density = 0.6, Palette = ["#00ff66", "#d9ffe6"] }, Needs.Nothing, Matrix),
        new("twinkle", ["speed", "density"], new() { Speed = 1, Density = 0.5, Palette = Ice }, Needs.Nothing, Twinkle),
        new("ripples", ["speed", "density", "width"], new() { Speed = 1, Density = 0.5, Width = 0.3, Palette = OceanPalette }, Needs.Nothing, Ripples),
        new("scanner", ["speed", "width"], new() { Speed = 1, Width = 0.3, Palette = ["#ff1a00"] }, Needs.Nothing, Scanner),
        new("radar", ["speed", "width"], new() { Speed = 1, Width = 0.5, Palette = ["#00ff66"] }, Needs.Nothing, Radar),
        new("breathing", ["speed"], new() { Speed = 1, Palette = Candy }, Needs.Nothing, Breathing),
        new("strobe", ["speed"], new() { Speed = 1, Palette = ["#ff0000", "#0030ff"] }, Needs.Nothing, Strobe),
        new("audio", ["gain"], new() { Gain = 1.5, Palette = ["#00e5ff", "#7c4dff", "#ff2d78"] }, Needs.Audio, Audio),
        new("audiomirror", ["gain"], new() { Gain = 1.5, Palette = ["#ff6a13", "#ff2d78", "#7c4dff"] }, Needs.Audio, AudioMirror),
        new("audiopulse", ["gain"], new() { Gain = 1.5, Palette = ["#1a0033", "#7c4dff", "#ff2d78", "#ffd166"] }, Needs.Audio, AudioPulse),
        new("keypress", ["fade"], new() { Fade = 0.6, Palette = ["#ff6a13", "#ffd166", "#00e5ff", "#7c4dff"] }, Needs.Keys, KeyPress),
        new("keyripple", ["speed", "width"], new() { Speed = 1, Width = 0.3, Palette = ["#00e5ff", "#7c4dff", "#ff2d78"] }, Needs.Keys, KeyRipple),
        new("keysplash", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = ["#ff6a13", "#ffd166", "#ff2d78"] }, Needs.Keys, KeySplash),
        new("keybeam", ["speed", "width"], new() { Speed = 1, Width = 0.4, Palette = ["#00ff9d", "#00e5ff"] }, Needs.Keys, KeyBeam),
        new("keyheat", ["fade"], new() { Fade = 4, Palette = ["#001a66", "#00e5ff", "#ffd166", "#ff2d00"] }, Needs.Keys, KeyHeat),
        new("solid", [], new() { Palette = ["#ff6a13"] }, Needs.Nothing, Solid),
        new("gradient", ["angle"], new() { Angle = 0, Palette = Sunset }, Needs.Nothing, Gradient),
        new("rows", [], new() { Palette = Rainbow }, Needs.Nothing, Rows),
        new("zones", [], new() { Palette = ["#ffffff", "#ff6a13", "#00e5ff"] }, Needs.Nothing, KeysAndEdges),
        new("wasd", [], new() { Palette = ["#ff6a13", "#141a2e"] }, Needs.Nothing, Wasd),
        new("confetti", [], new() { Palette = Candy }, Needs.Nothing, Confetti),
        new("stripes", ["speed", "scale", "angle"], new() { Speed = 0.8, Scale = 1, Angle = 30, Palette = ["#ff6a13", "#000000"] }, Needs.Nothing, Stripes),
        new("pinwheel", ["speed", "arms"], new() { Speed = 0.8, Arms = 3, Palette = Candy }, Needs.Nothing, Pinwheel),
        new("checker", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = ["#ff2d78", "#00e5ff"] }, Needs.Nothing, Checker),
        new("edgechase", ["speed", "arms", "width"], new() { Speed = 1, Arms = 2, Width = 0.4, Palette = ["#ff6a13", "#ffd166"] }, Needs.Nothing, EdgeChase),
        new("bounce", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = Rainbow }, Needs.Nothing, Bounce),
        new("curtain", ["speed"], new() { Speed = 0.8, Palette = Candy }, Needs.Nothing, Curtain),
        new("tunnel", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = ["#000000", "#7c4dff", "#00e5ff", "#ffffff"] }, Needs.Nothing, Tunnel),
        new("kaleido", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = Rainbow }, Needs.Nothing, Kaleido),
        new("conveyor", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = Rainbow }, Needs.Nothing, Conveyor),
        new("snake", ["speed", "width"], new() { Speed = 1, Width = 0.4, Palette = ["#00ff66", "#ffd166"] }, Needs.Nothing, Snake),
        new("crossfire", ["speed", "width"], new() { Speed = 1, Width = 0.3, Palette = ["#ff2d00", "#00a2ff"] }, Needs.Nothing, Crossfire),
        new("wipe", ["speed", "angle"], new() { Speed = 0.8, Angle = 0, Palette = Candy }, Needs.Nothing, Wipe),
        new("ocean", ["speed", "scale"], new() { Speed = 0.8, Scale = 1, Palette = OceanPalette }, Needs.Nothing, Ocean),
        new("rain", ["speed", "density"], new() { Speed = 1, Density = 0.6, Palette = ["#0a2a66", "#4fc3f7", "#e1f5fe"] }, Needs.Nothing, Rain),
        new("snow", ["speed", "density"], new() { Speed = 0.6, Density = 0.5, Palette = ["#0b1a33", "#ffffff"] }, Needs.Nothing, Snow),
        new("storm", ["speed", "density"], new() { Speed = 1, Density = 0.5, Palette = ["#020417", "#0b1b4d", "#2a3a7c", "#ffffff"] }, Needs.Nothing, Storm),
        new("fireflies", ["speed", "density", "scale"], new() { Speed = 0.8, Density = 0.5, Scale = 1, Palette = ["#c6ff00", "#ffd166"] }, Needs.Nothing, Fireflies),
        new("embers", ["speed", "density"], new() { Speed = 1, Density = 0.6, Palette = ["#1a0500", "#ff3d00", "#ffb300"] }, Needs.Nothing, Embers),
        new("daycycle", ["speed"], new() { Speed = 1, Palette = ["#050a30", "#ff7e5f", "#87ceeb", "#fceabb", "#ff6a88"] }, Needs.Nothing, DayCycle),
        new("candle", ["speed"], new() { Speed = 1, Palette = ["#ff6a00", "#ffb347", "#ffd9a0"] }, Needs.Nothing, Candle),
        new("meteor", ["speed", "density"], new() { Speed = 1, Density = 0.6, Palette = ["#ffffff", "#9ad0ff", "#ffd166"] }, Needs.Nothing, Meteor),
        new("heartbeat", ["speed"], new() { Speed = 1, Palette = ["#ff0033"] }, Needs.Nothing, Heartbeat),
        new("colorcycle", ["speed"], new() { Speed = 1, Palette = Rainbow }, Needs.Nothing, ColorCycle),
        new("flash", ["speed", "width"], new() { Speed = 1, Width = 0.3, Palette = ["#ffffff"] }, Needs.Nothing, Flash),
        new("sparkle", ["speed", "density"], new() { Speed = 1, Density = 0.5, Palette = ["#ffffff", "#ffd166", "#00e5ff"] }, Needs.Nothing, Sparkle),
        new("disco", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = Rainbow }, Needs.Nothing, Disco),
        new("glitch", ["speed", "density"], new() { Speed = 1, Density = 0.5, Palette = ["#00e5ff", "#ff2d78", "#ffffff"] }, Needs.Nothing, Glitch),
        new("pulsewave", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = Sunset }, Needs.Nothing, PulseWave),
        new("mosaic", ["speed"], new() { Speed = 1, Palette = Candy }, Needs.Nothing, Mosaic),
        new("audiovu", ["gain"], new() { Gain = 1.5, Palette = ["#00ff66", "#ffd166", "#ff2d00"] }, Needs.Audio, AudioVu),
        new("audioglow", ["gain"], new() { Gain = 1.5, Palette = Rainbow }, Needs.Audio, AudioGlow),
        new("audiobloom", ["gain"], new() { Gain = 1.5, Palette = ["#ffffff", "#ff2d78", "#7c4dff"] }, Needs.Audio, AudioBloom),
        new("audiofire", ["gain", "speed"], new() { Gain = 1.5, Speed = 1, Palette = FirePalette }, Needs.Audio, AudioFire),
        new("audiohalo", ["gain"], new() { Gain = 1.5, Palette = ["#00e5ff", "#7c4dff", "#ff2d78"] }, Needs.Audio, AudioHalo),
        new("audiocentre", ["gain"], new() { Gain = 1.5, Palette = ["#ffd166", "#ff6a13", "#ff2d78"] }, Needs.Audio, AudioCentre),
        new("audiosparkle", ["gain", "speed"], new() { Gain = 1.5, Speed = 1, Palette = Ice }, Needs.Audio, AudioSparkle),
        new("keycross", ["speed", "width"], new() { Speed = 1, Width = 0.4, Palette = ["#ff2d78", "#7c4dff"] }, Needs.Keys, KeyCross),
        new("keyfireworks", ["speed", "scale"], new() { Speed = 1, Scale = 1, Palette = ["#ffd166", "#ff2d78", "#00e5ff", "#7c4dff"] }, Needs.Keys, KeyFireworks),
        new("keyshadow", ["fade"], new() { Fade = 1.2, Palette = Sunset }, Needs.Keys, KeyShadow),
        new("keydrip", ["speed", "width"], new() { Speed = 1, Width = 0.4, Palette = ["#00e5ff", "#2040ff"] }, Needs.Keys, KeyDrip),
        new("keyedge", ["fade"], new() { Fade = 0.5, Palette = ["#ff6a13", "#ffd166", "#00e5ff"] }, Needs.Keys, KeyEdge),
        new("keytrail", ["fade"], new() { Fade = 2.5, Palette = Rainbow }, Needs.Keys, KeyTrail),
        new("keyspeed", ["fade"], new() { Fade = 1.5, Palette = ["#001a66", "#00e5ff", "#ffd166", "#ff2d00"] }, Needs.Keys, KeySpeed),
        new("keyspot", ["scale", "fade"], new() { Scale = 1, Fade = 1.5, Palette = ["#ffffff", "#ffd166"] }, Needs.Keys, KeySpot),
        new("keyrow", ["fade"], new() { Fade = 0.6, Palette = ["#00ff9d", "#00e5ff", "#7c4dff"] }, Needs.Keys, KeyRow),
        new("paint", [], new(), Needs.Nothing, Paint),
    ];

    public static readonly IReadOnlyDictionary<string, EffectDef> ById = All.ToDictionary(e => e.Id);

    static void Wave(Frame c)
    {
        double a = c.Angle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalCyc(c.Pal, (l.Cx * dx + l.Cy * dy) * c.Scale * 0.3 - c.T * c.Speed * 0.25, c.Out, i);
        }
    }

    static void Spiral(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double ang = Math.Atan2(l.Cy, l.Cx) / Tau;
            PalCyc(c.Pal, ang * c.Arms + Hypot(l.Cx, l.Cy) * c.Scale * 0.4 - c.T * c.Speed * 0.3, c.Out, i, 1, c.Arms * c.Pal.Length);
        }
    }

    static void Rings(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalCyc(c.Pal, Hypot(l.Cx, l.Cy) * c.Scale * 0.6 - c.T * c.Speed * 0.3, c.Out, i);
        }
    }

    static void Plasma(Frame c)
    {
        double s = c.Scale, w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double v =
                Math.Sin(l.Cx * s * 3 + w) +
                Math.Sin(l.Cy * s * 4 - w * 1.3) +
                Math.Sin((l.Cx + l.Cy) * s * 2 + w * 0.7) +
                Math.Sin(Hypot(l.Cx, l.Cy) * s * 5 - w * 1.7);
            PalCyc(c.Pal, v / 8 + 0.5 + w * 0.02, c.Out, i);
        }
    }

    static void Aurora(Frame c)
    {
        double s = c.Scale, w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double n = Noise3(l.Cx * s * 1.3 + w * 0.15, l.Cy * s * 2.2, w * 0.2);
            double glow = Noise3(l.Cx * s * 0.8 - w * 0.1, l.Cy * s + 7, w * 0.12);
            PalLin(c.Pal, Clamp01(n * 1.5 - 0.25), c.Out, i, 0.35 + 0.65 * Clamp01(glow * 1.6 - 0.2));
        }
    }

    static void Fire(Frame c)
    {
        double w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double n = Noise3(l.Cx * c.Scale * 3, l.Cy * 3 + w * 2, w * 0.5);
            PalLin(c.Pal, n * 1.7 * (0.25 + l.V) - 0.12, c.Out, i);
        }
    }

    static void Lava(Frame c)
    {
        double w = c.T * c.Speed;
        Span<double> bx = stackalloc double[5], by = stackalloc double[5];
        for (int k = 0; k < 5; k++)
        {
            bx[k] = Math.Sin(w * (0.31 + k * 0.07) + k * 1.9) * 1.4;
            by[k] = Math.Cos(w * (0.43 + k * 0.05) + k * 2.7) * 0.45;
        }
        double r2 = 0.06 * c.Scale * c.Scale;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double f = 0;
            for (int k = 0; k < 5; k++)
            {
                double ex = l.Cx - bx[k], ey = l.Cy - by[k];
                f += r2 / (ex * ex + ey * ey + 0.01);
            }
            PalLin(c.Pal, f * 0.55, c.Out, i);
        }
    }

    static void Matrix(Frame c)
    {
        const int Columns = 30;
        var rng = Random.Shared;
        var cols = c.St.Cols ??= Enumerable.Range(0, Columns)
            .Select(_ => new MatrixColumn { Y = rng.NextDouble() * 2 - 1, Sp = 0.5 + rng.NextDouble() }).ToArray();
        foreach (var col in cols)
        {
            col.Y += c.Dt * col.Sp * c.Speed * 0.9;
            if (col.Y > 1.8 && rng.NextDouble() < c.Density * 0.2)
            {
                col.Y = -0.1 - rng.NextDouble() * 0.4;
                col.Sp = 0.5 + rng.NextDouble();
            }
        }
        double[] head = c.Pal.Pick(c.Pal.Length - 1), tail = c.Pal.Pick(0);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double d = cols[Math.Min(Columns - 1, (int)Math.Floor(l.U * Columns))].Y - l.V;
            if (d < 0 || d > 0.7) Put(c.Out, i, tail, 0);
            else if (d < 0.08) Put(c.Out, i, head);
            else
            {
                double fade = 1 - d / 0.7;
                Put(c.Out, i, tail, fade * fade);
            }
        }
    }

    static void Twinkle(Frame c)
    {
        double sharp = 2 + (1 - c.Density) * 14;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double b = Math.Pow(Math.Max(0, Math.Sin(c.T * c.Speed * (1 + l.Rnd * 2.5) + l.Rnd * 50)), sharp);
            PalCyc(c.Pal, l.Rnd * 7.3, c.Out, i, b);
        }
    }

    static void Ripples(Frame c)
    {
        var rng = Random.Shared;
        var rip = c.St.Rip ??= new List<Ripple>();
        if (rng.NextDouble() < c.Dt * c.Density * 4)
            rip.Add(new Ripple((rng.NextDouble() - 0.5) * 3.2, rng.NextDouble() - 0.5, c.T, rng.NextDouble()));
        rip.RemoveAll(r => !(c.T - r.T0 < 4));
        double w = 0.04 + c.Width * 0.25;
        var tmp = new byte[3];
        var o = c.Out;
        Array.Clear(o);
        foreach (var r in rip)
        {
            double age = c.T - r.T0, rad = age * c.Speed * 0.8, fade = (1 - age / 4) * (1 - age / 4);
            PalCyc(c.Pal, r.C, tmp, 0);
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                ref readonly var l = ref c.Lamps[i];
                double d = (Hypot(l.Cx - r.X, l.Cy - r.Y) - rad) / w;
                if (d > 2.5 || d < -2.5) continue;
                double b = Math.Exp(-d * d) * fade;
                o[i * 3] = Clamp8(o[i * 3] + tmp[0] * b);
                o[i * 3 + 1] = Clamp8(o[i * 3 + 1] + tmp[1] * b);
                o[i * 3 + 2] = Clamp8(o[i * 3 + 2] + tmp[2] * b);
            }
        }
    }

    static void Scanner(Frame c)
    {
        double ph = Frac(c.T * c.Speed * 0.35) * 2;
        double pos = ph < 1 ? ph : 2 - ph;
        double w = 0.02 + c.Width * 0.2;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double d = (c.Lamps[i].U - pos) / w;
            PalCyc(c.Pal, c.Lamps[i].U * 0.999, c.Out, i, Math.Exp(-d * d));
        }
    }

    static void Radar(Frame c)
    {
        double ang = c.T * c.Speed * 0.25;
        double sharp = 1 + (1 - c.Width) * 10;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double d = Frac(ang - Math.Atan2(l.Cy, l.Cx) / Tau);
            PalCyc(c.Pal, ang, c.Out, i, Math.Pow(1 - d, sharp));
        }
    }

    static void Breathing(Frame c)
    {
        double ph = c.T * c.Speed * 0.25;
        double b = 0.5 - 0.5 * Math.Cos(ph * Tau);
        double[] colour = c.Pal.Pick(Math.Floor(ph));
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, colour, b);
    }

    static void Strobe(Frame c)
    {
        double ph = Frac(c.T * c.Speed * 1.2);
        int side = ph < 0.5 ? 0 : 1;
        int flash = Frac(ph * 4) < 0.6 ? 1 : 0; // double flash per side
        double[] colour = c.Pal.Pick(side % c.Pal.Length + Math.Floor(c.T * c.Speed * 1.2) * c.Pal.Length);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            bool mine = (c.Lamps[i].Cx < 0 ? 0 : 1) == side;
            Put(c.Out, i, colour, mine ? flash : 0);
        }
    }

    static void Audio(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double level = sm[Math.Min(31, (int)Math.Floor(l.U * 32))];
            double h = 1 - l.V; // 0 at the front edge, 1 at the back
            double b = Clamp01((level - h * 0.92) * 8);
            PalLin(c.Pal, h, c.Out, i, b);
        }
    }

    static void AudioMirror(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double level = sm[Math.Min(31, (int)Math.Floor(Math.Abs(l.U - 0.5) * 2 * 32))];
            double h = 1 - l.V;
            double b = Clamp01((level - h * 0.92) * 8);
            PalLin(c.Pal, h, c.Out, i, b);
        }
    }

    static void AudioPulse(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        double bass = 0;
        for (int b = 0; b < 8; b++) bass += sm[b];
        bass /= 8;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double local = sm[Math.Min(31, (int)Math.Floor(l.U * 32))];
            PalLin(c.Pal, Clamp01(bass * 0.8 + local * 0.4), c.Out, i, Clamp01(0.08 + bass * 1.1));
        }
    }

    // Sums light in floating point; WriteAccumulator stores it once, so overlapping presses add up
    // the same way whatever order they are drawn in.
    static double[] Accumulator(Frame c)
    {
        if (c.St.Acc == null || c.St.Acc.Length != c.Out.Length) c.St.Acc = new double[c.Out.Length];
        Array.Clear(c.St.Acc);
        return c.St.Acc;
    }

    static void WriteAccumulator(double[] acc, byte[] o)
    {
        for (int i = 0; i < o.Length; i++) o[i] = Clamp8(acc[i]);
    }

    static void KeyPress(Frame c)
    {
        Array.Clear(c.Out);
        foreach (var ev in c.Keys)
        {
            if (ev.I >= c.Lamps.Length) continue;
            double b = 1;
            if (!(ev.R < 0))
            {
                double x = Clamp01(1 - (c.T - ev.R) / c.Fade);
                b = x * x;
            }
            if (b > 0) Put(c.Out, ev.I, c.Pal.Pick(ev.N), b);
        }
    }

    static void KeyRipple(Frame c)
    {
        const double Life = 2.5;
        var acc = Accumulator(c);
        double w = 0.03 + c.Width * 0.2;
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= Life || ev.I >= c.Lamps.Length) continue;
            var src = c.Lamps[ev.I];
            double[] colour = c.Pal.Pick(ev.N);
            double rad = age * c.Speed * 1.6, fade = (1 - age / Life) * (1 - age / Life);
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                double d = (Hypot(c.Lamps[i].Cx - src.Cx, c.Lamps[i].Cy - src.Cy) - rad) / w;
                if (d > 2.5 || d < -2.5) continue;
                double b = Math.Exp(-d * d) * fade;
                acc[i * 3] += colour[0] * b;
                acc[i * 3 + 1] += colour[1] * b;
                acc[i * 3 + 2] += colour[2] * b;
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeySplash(Frame c)
    {
        const double Life = 1.2;
        var acc = Accumulator(c);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= Life || ev.I >= c.Lamps.Length) continue;
            var src = c.Lamps[ev.I];
            double[] colour = c.Pal.Pick(ev.N);
            double radius = c.Scale * 0.45 * (1 - Math.Exp(-age * c.Speed * 6));
            if (radius <= 0) continue;
            double fade = (1 - age / Life) * (1 - age / Life);
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                double d = Hypot(c.Lamps[i].Cx - src.Cx, c.Lamps[i].Cy - src.Cy);
                if (d >= radius) continue;
                double b = (1 - d / radius) * fade;
                acc[i * 3] += colour[0] * b;
                acc[i * 3 + 1] += colour[1] * b;
                acc[i * 3 + 2] += colour[2] * b;
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeyBeam(Frame c)
    {
        const double Life = 1.5;
        var acc = Accumulator(c);
        double tail = 0.1 + c.Width * 0.6;
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= Life || ev.I >= c.Lamps.Length) continue;
            var src = c.Lamps[ev.I];
            double[] colour = c.Pal.Pick(ev.N);
            double head = age * c.Speed * 3, fade = 1 - age / Life;
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                if (Math.Abs(c.Lamps[i].Cy - src.Cy) > 0.06) continue; // same row only
                double dx = Math.Abs(c.Lamps[i].Cx - src.Cx);
                if (dx > head) continue;
                double b = Math.Exp(-(head - dx) / tail) * fade;
                acc[i * 3] += colour[0] * b;
                acc[i * 3 + 1] += colour[1] * b;
                acc[i * 3 + 2] += colour[2] * b;
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeyHeat(Frame c)
    {
        if (c.St.Heat == null || c.St.Heat.Length != c.Lamps.Length) c.St.Heat = new double[c.Lamps.Length];
        var heat = c.St.Heat;
        Array.Clear(heat);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age >= 0 && ev.I < c.Lamps.Length) heat[ev.I] += 0.3 * Math.Exp(-age / c.Fade);
        }
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double h = Clamp01(heat[i]);
            PalLin(c.Pal, h, c.Out, i, Clamp01(h * 4));
        }
    }

    static readonly double[] Black = [0, 0, 0];

    static void Paint(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
            Put(c.Out, i, c.Paint.TryGetValue(c.Lamps[i].Id, out var colour) ? colour : Black);
    }
}
