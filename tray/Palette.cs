namespace DarkMountStudio;

/// <summary>
/// An effect's palette: a list of [r, g, b] colours (the `pal` of js/effects.js). With the
/// effect's "Random colours" option on it also carries a seed. Every lookup then gives a random
/// bright colour instead: colour number k (any whole number - they never repeat) gets its own
/// hue, and keeps the brightness of the palette colour it stands in for, so a palette's dark
/// gaps stay dark. Effects go through Pick / PickR / PalCyc / PalLin, never the list directly.
/// </summary>
sealed class Palette
{
    readonly double[][] _colours;
    readonly int _seed;
    readonly double _seedHash;
    readonly double[] _ra = new double[3], _rb = new double[3]; // scratch for the two stops of a lookup
    readonly int _beatStep;
    double _offset; // pal.offset in the JS

    const double Golden = 0.61803398875;

    public Palette(double[][] colours, int? seed = null)
    {
        _colours = colours;
        Random = seed != null;
        _seed = seed ?? 0;
        _seedHash = Effects.Hash3(_seed, 0, 977);
        _beatStep = BeatStep(colours.Length);
    }

    /// <summary>How far one beat moves a palette of n colours on (beatStep in js/effects.js, explained there).</summary>
    static int BeatStep(int n)
    {
        for (int step = n; ; step++)
        {
            bool ok = true;
            for (int m = 1; m <= 4 && ok; m++) ok = Math.Abs(Frac(m * step * Golden) - 0.5) <= (m == 1 ? 0.25 : m == 2 ? 0.3 : 0.38);
            if (ok) return step;
        }
    }

    /// <summary>
    /// With random colours: gives the palette a fresh set of colours for beat number `beat`
    /// (newColoursOnBeat in js/effects.js). The audio effects call it on every frame.
    /// </summary>
    public void NewColoursOnBeat(int beat)
    {
        if (Random) _offset = (double)beat * _beatStep;
    }

    public int Length => _colours.Length;

    /// <summary>True when the palette carries a seed (pal.seed !== undefined in the JS).</summary>
    public bool Random { get; }

    static double Frac(double x) => x - Math.Floor(x);
    static double Wrap(double k, double n) => ((k % n) + n) % n;

    // Palette colour number k, wrapped. (The JS would fail on a number that is not whole; this falls back to the first colour.)
    double[] Plain(double k)
    {
        double j = Wrap(k, _colours.Length);
        return _colours[j >= 0 && j < _colours.Length ? (int)j : 0];
    }

    double[] RandomStop(double k, double[] into)
    {
        double[] stop = Plain(k);
        double v = Math.Max(Math.Max(stop[0], stop[1]), stop[2]);
        k += _offset;
        // Golden-ratio steps keep neighbouring colours well apart; the hash adds a little jitter.
        double h = Frac(k * Golden + _seedHash + (Effects.Hash3(Effects.ToInt32(k), _seed, 978) - 0.5) * 0.12) * 6;
        double s = Math.Floor(h), f = h - s, up = v * f, down = v * (1 - f);
        into[0] = s == 0 || s == 5 ? v : s == 1 ? down : s == 4 ? up : 0;
        into[1] = s == 1 || s == 2 ? v : s == 0 ? up : s == 3 ? down : 0;
        into[2] = s == 3 || s == 4 ? v : s == 2 ? up : s == 5 ? down : 0;
        return into;
    }

    /// <summary>Colour number k of the palette; k may be any whole number.</summary>
    public double[] Pick(double k) => Random ? RandomStop(k, new double[3]) : Plain(k);

    /// <summary>A palette colour chosen by r in 0..1.</summary>
    public double[] PickR(double r)
    {
        int n = _colours.Length;
        double j = Math.Floor(r * n) % n;
        return Random ? RandomStop(j + Math.Floor(r * 4096) * n, new double[3]) : _colours[j >= 0 && j < n ? (int)j : 0];
    }

    /// <summary>Part of a palette (colours from..to-1), keeping its random setting.</summary>
    public Palette Sub(int from, int to) => new(_colours[from..to], Random ? _seed : null);

    /// <summary>
    /// The two colours and the blend factor of a lookup that wraps around (last colour blends
    /// back into the first). With random colours it does not wrap, unless `period` says after
    /// how many colours it must repeat (needed where the lookup goes once around a circle).
    /// The arrays returned are only valid until the next lookup.
    /// </summary>
    public void Cyc(double t, double period, out double[] a, out double[] c, out double f)
    {
        int n = _colours.Length;
        if (!Random)
        {
            double x = Frac(t) * n;
            double floor = Math.Floor(x);
            int k = double.IsFinite(x) ? (int)floor % n : 0;
            f = x - floor;
            a = _colours[k];
            c = _colours[(k + 1) % n];
        }
        else
        {
            double x = t * n, k = Math.Floor(x);
            f = x - k;
            a = RandomStop(period > 0 ? Wrap(k, period) : k, _ra);
            c = RandomStop(period > 0 ? Wrap(k + 1, period) : k + 1, _rb);
        }
    }

    /// <summary>The two colours and the blend factor of a lookup from the first colour (t = 0) to the last (t = 1).</summary>
    public void Lin(double t, out double[] a, out double[] c, out double f)
    {
        int n = _colours.Length;
        if (n == 1)
        {
            a = c = Random ? RandomStop(0, _ra) : _colours[0];
            f = 0;
            return;
        }
        double x = (t < 0 ? 0 : t > 1 ? 1 : t) * (n - 1);
        int k = double.IsFinite(x) ? Math.Min((int)Math.Floor(x), n - 2) : 0;
        f = x - k;
        a = Random ? RandomStop(k, _ra) : _colours[k];
        c = Random ? RandomStop(k + 1, _rb) : _colours[k + 1];
    }
}
