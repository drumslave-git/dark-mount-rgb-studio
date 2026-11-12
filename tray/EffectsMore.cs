namespace DarkMountStudio;

// The second batch of effects from web/src/lib/effects.ts (static, motion, nature, pulse, more audio
// and more key-reactive ones) with the helpers they share. Like the JS they use no rounding
// other than floor and no random numbers, so both sides produce the same bytes.
static partial class Effects
{
    // ---------------------------------------------------------------- helpers

    /// <summary>A number as JS turns it into a 32-bit integer: fraction dropped, wrapped around.</summary>
    internal static int ToInt32(double x) =>
        double.IsFinite(x) ? unchecked((int)(long)(Math.Truncate(x) % 4294967296.0)) : 0;

    /// <summary>Repeatable 0..1 from three numbers (floored first).</summary>
    static double Rand(double a, double b, double c) =>
        Hash3(ToInt32(Math.Floor(a)), ToInt32(Math.Floor(b)), ToInt32(Math.Floor(c)));

    static double Tri(double x) // 0 -> 1 -> 0
    {
        double f = Frac(x) * 2;
        return f < 1 ? f : 2 - f;
    }

    static double Gauss(double x) => Math.Exp(-x * x);

    static void Mix(byte[] o, int i, double[] a, double[] c, double f, double b = 1)
    {
        o[i * 3] = Clamp8((a[0] + (c[0] - a[0]) * f) * b);
        o[i * 3 + 1] = Clamp8((a[1] + (c[1] - a[1]) * f) * b);
        o[i * 3 + 2] = Clamp8((a[2] + (c[2] - a[2]) * f) * b);
    }

    static void Add(double[] acc, int i, double[] c, double b)
    {
        acc[i * 3] += c[0] * b;
        acc[i * 3 + 1] += c[1] * b;
        acc[i * 3 + 2] += c[2] * b;
    }

    static double Avg(float[] levels, int from, int to)
    {
        double s = 0;
        for (int b = from; b < to; b++) s += levels[b];
        return s / (to - from);
    }

    // Key row of a light, 0 (F row) to 5 (space bar row). The board's v runs over the case height,
    // 7.84 key units, with the F row 1.96 units below the top edge.
    static int RowOf(in EffectLamp l)
    {
        double y = l.BoardV * 7.84 - 1.96;
        return y < 0.6 ? 0 : (int)Math.Min(5, Math.Max(1, 1 + Math.Floor(y - 0.7)));
    }

    // Largest projection of any light onto a direction, for effects that span the whole board.
    static double SpanAlong(EffectLamp[] lamps, double dx, double dy)
    {
        double span = 1e-6;
        for (int i = 0; i < lamps.Length; i++) span = Math.Max(span, Math.Abs(lamps[i].Cx * dx + lamps[i].Cy * dy));
        return span;
    }

    // PalLin into an unrounded colour (the JS writes it to a Float64Array).
    static void PalLinTo(Palette pal, double t, double[] colour)
    {
        pal.Lin(t, out double[] a, out double[] c, out double f);
        for (int j = 0; j < 3; j++) colour[j] = a[j] + (c[j] - a[j]) * f;
    }

    static readonly int[] WasdKeys = [17, 30, 31, 32, 62, 65, 66, 69]; // key ids of W A S D and the four arrows

    // ----------------------------------------------------------------- static

    static void Solid(Frame c)
    {
        double[] colour = c.Pal.Pick(0);
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, colour);
    }

    static void Gradient(Frame c)
    {
        double a = c.Angle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        double span = SpanAlong(c.Lamps, dx, dy);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalLin(c.Pal, ((l.Cx * dx + l.Cy * dy) / span + 1) / 2, c.Out, i);
        }
    }

    static void Rows(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, c.Pal.Pick(RowOf(c.Lamps[i])));
    }

    static void KeysAndEdges(Frame c)
    {
        double[] keys = c.Pal.Pick(0), bars = c.Pal.Pick(1), numpad = c.Pal.Pick(2);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            Put(c.Out, i, l.Bar ? bars : l.Numpad ? numpad : keys);
        }
    }

    static void Wasd(Frame c)
    {
        double[] hot = c.Pal.Pick(0), rest = c.Pal.Pick(1);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            Put(c.Out, i, Array.IndexOf(WasdKeys, l.Binding) >= 0 && !l.Bar ? hot : rest);
        }
    }

    static void Confetti(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, c.Pal.PickR(c.Lamps[i].Rnd));
    }

    // ----------------------------------------------------------------- motion

    static void Stripes(Frame c)
    {
        double a = c.Angle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        int n = c.Pal.Length;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            Put(c.Out, i, c.Pal.Pick(Math.Floor(((l.Cx * dx + l.Cy * dy) * c.Scale * 0.8 - c.T * c.Speed * 0.2) * n)));
        }
    }

    static void Pinwheel(Frame c)
    {
        int n = c.Pal.Length;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double x = Frac(Math.Atan2(l.Cy, l.Cx) / Tau + c.T * c.Speed * 0.12) * c.Arms * n;
            Put(c.Out, i, c.Pal.Pick(Math.Floor(x)));
        }
    }

    static void Checker(Frame c)
    {
        double ph = c.T * c.Speed * 0.6, step = Math.Floor(ph), f = Clamp01((Frac(ph) - 0.7) / 0.3);
        int n = c.Pal.Length;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double ix = Math.Floor(l.Cx * c.Scale * 3 + 100), iy = Math.Floor(l.Cy * c.Scale * 3 + 100);
            // ix + iy picks the palette colour; the multiple of n changes nothing for a normal
            // palette but gives every square its own number, so random colours differ per square.
            double cell = ix + iy + (ix * 31 + iy * 17) * n;
            PalCyc(c.Pal, (cell + step + f) / n, c.Out, i);
        }
    }

    static void EdgeChase(Frame c)
    {
        double sharp = 1 + (1 - c.Width) * 6;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            if (!l.Bar)
            {
                c.Out[i * 3] = c.Out[i * 3 + 1] = c.Out[i * 3 + 2] = 0;
                continue;
            }
            double x = (c.T * c.Speed * 0.15 - Math.Atan2(l.Cy, l.Cx) / Tau) * c.Arms;
            Put(c.Out, i, c.Pal.Pick(Math.Floor(x)), Math.Pow(1 - Frac(x), sharp));
        }
    }

    static void Bounce(Frame c)
    {
        double bx = (Tri(c.T * c.Speed * 0.11) - 0.5) * 2.9, by = (Tri(c.T * c.Speed * 0.17 + 0.3) - 0.5) * 0.9;
        double r = 0.1 + c.Scale * 0.14;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalCyc(c.Pal, c.T * 0.07, c.Out, i, Gauss(Hypot(l.Cx - bx, l.Cy - by) / r));
        }
    }

    static void Curtain(Frame c)
    {
        double ph = c.T * c.Speed * 0.35, k = Math.Floor(ph), front = Frac(ph) * 1.1;
        double[] old = c.Pal.Pick(k), next = c.Pal.Pick(k + 1);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double d = Math.Abs(c.Lamps[i].U - 0.5) * 2;
            Mix(c.Out, i, old, next, Clamp01((front - d) / 0.08));
        }
    }

    static void Tunnel(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalCyc(c.Pal, Math.Log(Hypot(l.Cx, l.Cy) + 0.08) * c.Scale * 0.9 + c.T * c.Speed * 0.3, c.Out, i);
        }
    }

    static void Kaleido(Frame c)
    {
        double w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double nz = Noise3(Math.Abs(l.Cx) * c.Scale * 2.2 + w * 0.2, Math.Abs(l.Cy) * c.Scale * 2.2 - w * 0.15, w * 0.1);
            PalCyc(c.Pal, nz * 2, c.Out, i);
        }
    }

    static void Conveyor(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            int row = RowOf(l);
            PalCyc(c.Pal, l.Cx * c.Scale * 0.3 + (row % 2 != 0 ? 1 : -1) * c.T * c.Speed * 0.25 + row * 0.13, c.Out, i);
        }
    }

    static void Snake(Frame c)
    {
        double len = 0.04 + c.Width * 0.25, head = Frac(c.T * c.Speed * 0.07);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            int row = RowOf(l);
            double q = (row + (row % 2 != 0 ? 1 - l.U : l.U)) / 6;
            double d = Frac(head - q);
            double b = 0;
            if (d < len)
            {
                b = 1 - d / len;
                b *= b;
            }
            PalCyc(c.Pal, q * 3, c.Out, i, b);
        }
    }

    static void Crossfire(Frame c)
    {
        double pos = Tri(c.T * c.Speed * 0.18), w = 0.02 + c.Width * 0.2;
        double pass = Math.Floor(c.T * c.Speed * 0.18) * c.Pal.Length; // new random colours on every pass
        double[] c0 = c.Pal.Pick(pass), c1 = c.Pal.Pick(pass + 1);
        var o = c.Out;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double b0 = Gauss((c.Lamps[i].U - pos) / w), b1 = Gauss((c.Lamps[i].U - (1 - pos)) / w);
            o[i * 3] = Clamp8(c0[0] * b0 + c1[0] * b1);
            o[i * 3 + 1] = Clamp8(c0[1] * b0 + c1[1] * b1);
            o[i * 3 + 2] = Clamp8(c0[2] * b0 + c1[2] * b1);
        }
    }

    static void Wipe(Frame c)
    {
        double a = c.Angle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        double span = SpanAlong(c.Lamps, dx, dy);
        double ph = c.T * c.Speed * 0.3, k = Math.Floor(ph), front = Frac(ph) * 1.1;
        double[] old = c.Pal.Pick(k), next = c.Pal.Pick(k + 1);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double x = ((l.Cx * dx + l.Cy * dy) / span + 1) / 2;
            Mix(c.Out, i, old, next, Clamp01((front - x) / 0.06));
        }
    }

    // ----------------------------------------------------------------- nature

    static void Ocean(Frame c)
    {
        double w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double h = Math.Sin(l.Cx * 3 * c.Scale + w) * 0.5 + Math.Sin(l.Cx * 5 * c.Scale - w * 0.7 + l.Cy * 4) * 0.3;
            double nz = Noise3(l.Cx * 2 * c.Scale, l.Cy * 3 + w * 0.2, w * 0.3);
            PalLin(c.Pal, 0.45 + 0.3 * h + (nz - 0.5) * 0.5, c.Out, i);
        }
    }

    static void Rain(Frame c)
    {
        const int Columns = 36;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double col = Math.Min(Columns - 1, Math.Floor(l.U * Columns));
            double b = 0;
            for (int j = 0; j < 2; j++)
            {
                double ph = c.T * c.Speed * 0.7 * (0.6 + Rand(col, j, 11) * 0.8) + Rand(col, j, 12);
                if (Rand(col * 2 + j, Math.Floor(ph), 13) >= c.Density) continue;
                double d = Frac(ph) * 1.4 - 0.2 - l.V;
                if (d >= 0 && d < 0.35) b = Math.Max(b, 1 - d / 0.35);
            }
            PalLin(c.Pal, b, c.Out, i, b);
        }
    }

    static void Snow(Frame c)
    {
        double w = c.T * c.Speed, thr = 0.8 - c.Density * 0.25;
        double[] sky = c.Pal.Pick(0), flake = c.Pal.Pick(c.Pal.Length - 1);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double nz = Noise3(l.Cx * 7 + Math.Sin(w * 0.4) * 0.6, l.Cy * 7 - w * 0.9, 3.3);
            double f = Clamp01((nz - thr) * 10);
            Mix(c.Out, i, sky, flake, f, 0.12 + 0.88 * f);
        }
    }

    static void Storm(Frame c)
    {
        double w = c.T * c.Speed, slot = Math.Floor(w * 0.8), fp = Frac(w * 0.8);
        double strike = Rand(slot, 0, 21) < c.Density * 0.7 ? Math.Exp(-fp * 7) * (0.6 + 0.4 * Math.Sin(fp * 70)) : 0;
        double x0 = Rand(slot, 1, 21);
        int n = c.Pal.Length;
        Palette sky = n > 1 ? c.Pal.Sub(0, n - 1) : c.Pal; // clouds: every colour but the last
        double[] bolt = c.Pal.Pick(n - 1 + slot * n);      // with random colours, every strike gets its own
        double[] cloud = new double[3];
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            PalLinTo(sky, Noise3(l.Cx * 1.5 + w * 0.1, l.Cy * 2, w * 0.15) * 0.9, cloud);
            Mix(c.Out, i, cloud, bolt, Clamp01(strike * (0.25 + 0.75 * Gauss((l.U - x0) * 3.5))));
        }
    }

    static void Fireflies(Frame c)
    {
        var acc = Accumulator(c);
        int count = 4 + (int)Math.Floor(c.Density * 14);
        double r = 0.07 + c.Scale * 0.07, w = c.T * c.Speed;
        for (int k = 0; k < count; k++)
        {
            double fx = Math.Sin(w * (0.21 + Rand(k, 0, 31) * 0.3) + Rand(k, 1, 31) * Tau) * 1.45;
            double fy = Math.Sin(w * (0.27 + Rand(k, 2, 31) * 0.3) + Rand(k, 3, 31) * Tau) * 0.45;
            double blink = 0.5 + 0.5 * Math.Sin(w * (1 + Rand(k, 4, 31) * 2) + k);
            blink *= blink;
            double[] colour = c.Pal.Pick(k);
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                double d = Hypot(c.Lamps[i].Cx - fx, c.Lamps[i].Cy - fy) / r;
                if (d <= 2.5) Add(acc, i, colour, Gauss(d) * blink);
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void Embers(Frame c)
    {
        const int Columns = 36;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double col = Math.Min(Columns - 1, Math.Floor(l.U * Columns));
            double b = Clamp01((l.V - 0.7) * 2.2) * 0.5;
            for (int j = 0; j < 2; j++)
            {
                double ph = c.T * c.Speed * 0.5 * (0.6 + Rand(col, j, 14) * 0.8) + Rand(col, j, 15);
                if (Rand(col * 2 + j, Math.Floor(ph), 16) >= c.Density) continue;
                double d = l.V - (1.2 - Frac(ph) * 1.4);
                if (d >= 0 && d < 0.18) b = Math.Max(b, (1 - d / 0.18) * (1 - Frac(ph)));
            }
            PalLin(c.Pal, b, c.Out, i, Clamp01(b * 1.5));
        }
    }

    static void DayCycle(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++) PalCyc(c.Pal, c.T * c.Speed * 0.02 + (1 - c.Lamps[i].V) * 0.12, c.Out, i);
    }

    static void Candle(Frame c)
    {
        double w = c.T * c.Speed, glow = 0.55 + 0.45 * Noise3(w * 3, 0.5, 0.5);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double nz = Noise3(c.Lamps[i].Cx * 1.3, c.Lamps[i].Cy * 1.3, w * 2.2);
            PalLin(c.Pal, nz, c.Out, i, Clamp01(glow * (0.6 + 0.4 * nz)));
        }
    }

    static void Meteor(Frame c)
    {
        var acc = Accumulator(c);
        const double Dx = 0.6, Dy = 0.8, Tail = 0.55;
        for (int k = 0; k < 7; k++)
        {
            double ph = c.T * c.Speed * 0.35 * (0.6 + Rand(k, 0, 41) * 0.8) + Rand(k, 1, 41);
            double cyc = Math.Floor(ph), f = Frac(ph);
            if (Rand(k, cyc, 42) >= c.Density) continue;
            double hx = Rand(k, cyc, 43) * 3.4 - 2 + f * 1.9 * Dx, hy = -0.75 + f * 1.9 * Dy;
            double[] colour = c.Pal.Pick(k % c.Pal.Length + (cyc * 7 + k) * c.Pal.Length); // random: a new colour per meteor
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                double rx = c.Lamps[i].Cx - hx, ry = c.Lamps[i].Cy - hy;
                double along = rx * Dx + ry * Dy;
                if (along > 0.03 || along < -Tail) continue;
                Add(acc, i, colour, Math.Min(1, 1 + along / Tail) * Gauss((rx * Dy - ry * Dx) / 0.09));
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    // ------------------------------------------------------------------ pulse

    static void Heartbeat(Frame c)
    {
        double ph = Frac(c.T * c.Speed * 1.1);
        double beat = Math.Max(Gauss((ph - 0.1) / 0.05), 0.7 * Gauss((ph - 0.32) / 0.06));
        double[] colour = c.Pal.Pick(Math.Floor(c.T * c.Speed * 1.1) * c.Pal.Length); // random: a new colour per heartbeat
        for (int i = 0; i < c.Lamps.Length; i++)
            Put(c.Out, i, colour, Clamp01(0.06 + beat * (1 - 0.35 * Hypot(c.Lamps[i].Cx, c.Lamps[i].Cy))));
    }

    static void ColorCycle(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++) PalCyc(c.Pal, c.T * c.Speed * 0.08, c.Out, i);
    }

    static void Flash(Frame c)
    {
        double ph = c.T * c.Speed * 2;
        int on = Frac(ph) < 0.05 + c.Width * 0.45 ? 1 : 0;
        double[] colour = c.Pal.Pick(Math.Floor(ph));
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, colour, on);
    }

    static void Sparkle(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double x = c.T * c.Speed * 5 + l.Rnd * 97, slot = Math.Floor(x);
            bool on = Rand(l.Id, slot, 51) < c.Density * 0.3;
            double b = 0;
            if (on)
            {
                b = 1 - Frac(x);
                b *= b;
            }
            Put(c.Out, i, c.Pal.PickR(Rand(l.Id, slot, 52)), b);
        }
    }

    static void Disco(Frame c)
    {
        double ph = c.T * c.Speed * 2, beat = Math.Floor(ph), glow = 0.45 + 0.55 * Math.Exp(-Frac(ph) * 3);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double cell = Math.Floor(c.Lamps[i].U * 8 * c.Scale) + Math.Floor(c.Lamps[i].V * 3 * c.Scale) * 31;
            Put(c.Out, i, c.Pal.PickR(Rand(cell, beat, 61)), glow);
        }
    }

    static void Glitch(Frame c)
    {
        double slot = Math.Floor(c.T * c.Speed * 9);
        int n = c.Pal.Length, others = n - 1 != 0 ? n - 1 : 1;
        double[] rest = c.Pal.Pick(0);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            int row = RowOf(l);
            bool hit = Rand(row, slot, 71) < c.Density * 0.35 && Frac(l.U * 3 + Rand(row, slot, 72)) < 0.5;
            double r = Rand(row, slot, 73);
            // Colour 1..n-1 of the palette; with random colours, a new one for every flicker.
            if (hit) Put(c.Out, i, c.Pal.Pick((1 + Math.Floor(r * others)) % n + Math.Floor(r * 4096) * n));
            else Put(c.Out, i, rest, 0.3);
        }
    }

    static void PulseWave(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double u = c.Lamps[i].U;
            PalLin(c.Pal, u, c.Out, i, 0.12 + 0.88 * (0.5 - 0.5 * Math.Cos(Tau * (c.T * c.Speed * 0.4 - u * c.Scale))));
        }
    }

    static void Mosaic(Frame c)
    {
        for (int i = 0; i < c.Lamps.Length; i++)
            PalCyc(c.Pal, c.Lamps[i].Rnd * 7 + c.T * c.Speed * 0.08 * (0.5 + c.Lamps[i].Rnd), c.Out, i);
    }

    // ------------------------------------------------------------------ audio

    static int Band(double u) => Math.Min(31, (int)Math.Floor(u * 32));

    static void AudioVu(Frame c)
    {
        double level = Clamp01(Avg(SmoothLevels(c, c.Gain), 0, 32) * 1.4);
        for (int i = 0; i < c.Lamps.Length; i++) PalLin(c.Pal, c.Lamps[i].U, c.Out, i, Clamp01((level - c.Lamps[i].U) * 12));
    }

    static void AudioGlow(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double u = c.Lamps[i].U;
            PalLin(c.Pal, u, c.Out, i, Clamp01(sm[Band(u)] * 1.3));
        }
    }

    static void AudioBloom(Frame c)
    {
        double radius = 0.08 + Avg(SmoothLevels(c, c.Gain), 0, 8) * 1.9;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            double d = Hypot(c.Lamps[i].Cx, c.Lamps[i].Cy);
            PalLin(c.Pal, d / 1.6, c.Out, i, Clamp01((radius - d) * 8));
        }
    }

    static void AudioFire(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        double w = c.T * c.Speed;
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double lv = sm[Band(l.U)];
            double nz = Noise3(l.Cx * 4, l.Cy * 4 + w * 2.5, w * 0.6);
            PalLin(c.Pal, (lv * 1.25 - (1 - l.V)) * 2.2 + (nz - 0.5) * 0.6 + 0.15 * l.V, c.Out, i);
        }
    }

    static void AudioHalo(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        double bass = Clamp01(Avg(sm, 0, 8) * 1.4);
        double[] halo = c.Pal.Pick(c.Pal.Length - 1);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            if (l.Bar)
            {
                Put(c.Out, i, halo, bass);
                continue;
            }
            double h = 1 - l.V;
            PalLin(c.Pal, h, c.Out, i, Clamp01((sm[Band(l.U)] - h * 0.92) * 8));
        }
    }

    static void AudioCentre(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double h = Math.Abs(l.V - 0.58) * 2;
            PalLin(c.Pal, h, c.Out, i, Clamp01((sm[Band(l.U)] - h * 0.95) * 8));
        }
    }

    static void AudioSparkle(Frame c)
    {
        var sm = SmoothLevels(c, c.Gain);
        for (int i = 0; i < c.Lamps.Length; i++)
        {
            ref readonly var l = ref c.Lamps[i];
            double tw = Math.Pow(Math.Max(0, Math.Sin(c.T * c.Speed * (2 + l.Rnd * 4) + l.Rnd * 50)), 3);
            PalCyc(c.Pal, l.Rnd * 7.3, c.Out, i, Clamp01(tw * sm[Band(l.U)] * 2.2));
        }
    }

    // --------------------------------------------------------------- reactive

    static void KeyCross(Frame c)
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
                double dx = Math.Abs(c.Lamps[i].Cx - src.Cx), dy = Math.Abs(c.Lamps[i].Cy - src.Cy);
                if (dy <= 0.06 && dx <= head) Add(acc, i, colour, Math.Exp(-(head - dx) / tail) * fade);
                else if (dx <= 0.07 && dy <= head) Add(acc, i, colour, Math.Exp(-(head - dy) / tail) * fade);
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeyFireworks(Frame c)
    {
        const double Life = 1.3, R = 0.06;
        const int Sparks = 9;
        var acc = Accumulator(c);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= Life || ev.I >= c.Lamps.Length) continue;
            var src = c.Lamps[ev.I];
            double fade = (1 - age / Life) * (1 - age / Life);
            for (int j = 0; j < Sparks; j++)
            {
                double ang = Tau * (j + Rand(ev.N, j, 81)) / Sparks;
                double dist = (0.35 + Rand(ev.N, j, 82) * 0.5) * c.Scale * (1 - Math.Exp(-age * c.Speed * 3));
                double sx = src.Cx + Math.Cos(ang) * dist, sy = src.Cy + Math.Sin(ang) * dist + age * age * 0.12;
                double[] colour = c.Pal.Pick(ev.N + j + ((double)ev.N * Sparks + j) * c.Pal.Length); // random: every spark its own colour
                for (int i = 0; i < c.Lamps.Length; i++)
                {
                    double d = Hypot(c.Lamps[i].Cx - sx, c.Lamps[i].Cy - sy) / R;
                    if (d <= 2.5) Add(acc, i, colour, Gauss(d) * fade);
                }
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeyShadow(Frame c)
    {
        if (c.St.Mul == null || c.St.Mul.Length != c.Lamps.Length) c.St.Mul = new double[c.Lamps.Length];
        var mul = c.St.Mul;
        Array.Fill(mul, 1);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= c.Fade || ev.I >= c.Lamps.Length) continue;
            var src = c.Lamps[ev.I];
            double k = (1 - age / c.Fade) * (1 - age / c.Fade);
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                double d = Hypot(c.Lamps[i].Cx - src.Cx, c.Lamps[i].Cy - src.Cy) / 0.2;
                if (d <= 2.5) mul[i] *= 1 - k * Gauss(d);
            }
        }
        for (int i = 0; i < c.Lamps.Length; i++) PalCyc(c.Pal, c.Lamps[i].Cx * 0.25 + c.T * 0.04, c.Out, i, mul[i]);
    }

    static void KeyDrip(Frame c)
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
            double head = age * c.Speed * 1.5, fade = 1 - age / Life;
            for (int i = 0; i < c.Lamps.Length; i++)
            {
                if (Math.Abs(c.Lamps[i].Cx - src.Cx) > 0.07) continue;
                double dy = c.Lamps[i].Cy - src.Cy;
                if (dy < -0.01 || dy > head) continue;
                Add(acc, i, colour, Math.Exp(-(head - dy) / tail) * fade);
            }
        }
        WriteAccumulator(acc, c.Out);
    }

    static void KeyEdge(Frame c)
    {
        double b = 0;
        double[] colour = c.Pal.Pick(0);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= c.Fade) continue;
            double k = (1 - age / c.Fade) * (1 - age / c.Fade);
            if (k >= b)
            {
                b = k;
                colour = c.Pal.Pick(ev.N);
            }
        }
        for (int i = 0; i < c.Lamps.Length; i++) Put(c.Out, i, colour, c.Lamps[i].Bar ? b : 0);
        foreach (var ev in c.Keys)
            if (ev.R < 0 && ev.I < c.Lamps.Length) Put(c.Out, ev.I, c.Pal.Pick(ev.N));
    }

    static void KeyTrail(Frame c)
    {
        Array.Clear(c.Out);
        foreach (var ev in c.Keys)
        {
            if (ev.I >= c.Lamps.Length) continue;
            double b = ev.R < 0 ? 1 : Clamp01(1 - (c.T - ev.R) / c.Fade);
            if (b <= 0) continue;
            if (!c.Pal.Random) PalCyc(c.Pal, ev.N * 0.045, c.Out, ev.I, b * b);
            else Put(c.Out, ev.I, c.Pal.Pick(ev.N), b * b); // random: every press its own colour
        }
    }

    static void KeySpeed(Frame c)
    {
        double rate = 0;
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age >= 0) rate += 0.14 * Math.Exp(-age / c.Fade);
        }
        rate = Clamp01(rate);
        for (int i = 0; i < c.Lamps.Length; i++) PalLin(c.Pal, rate, c.Out, i, 0.12 + 0.88 * rate);
    }

    static void KeySpot(Frame c)
    {
        Array.Clear(c.Out);
        if (c.Keys.Count == 0) return;
        var ev = c.Keys[^1];
        if (ev.I >= c.Lamps.Length) return;
        double b = 1;
        if (!(ev.R < 0))
        {
            b = Clamp01(1 - (c.T - ev.R) / c.Fade);
            b *= b;
        }
        var src = c.Lamps[ev.I];
        double r = 0.12 + c.Scale * 0.22;
        double[] colour = c.Pal.Pick(ev.N);
        for (int i = 0; i < c.Lamps.Length; i++)
            Put(c.Out, i, colour, Gauss(Hypot(c.Lamps[i].Cx - src.Cx, c.Lamps[i].Cy - src.Cy) / r) * b);
    }

    static void KeyRow(Frame c)
    {
        var acc = Accumulator(c);
        foreach (var ev in c.Keys)
        {
            double age = c.T - ev.T;
            if (age < 0 || age >= c.Fade || ev.I >= c.Lamps.Length) continue;
            int row = RowOf(c.Lamps[ev.I]);
            double[] colour = c.Pal.Pick(ev.N);
            double k = (1 - age / c.Fade) * (1 - age / c.Fade);
            for (int i = 0; i < c.Lamps.Length; i++)
                if (!c.Lamps[i].Bar && RowOf(c.Lamps[i]) == row) Add(acc, i, colour, k);
        }
        WriteAccumulator(acc, c.Out);
    }
}
