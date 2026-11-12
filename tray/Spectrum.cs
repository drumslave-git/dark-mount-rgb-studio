using NAudio.Dsp;

namespace DarkMountStudio;

/// <summary>
/// Turns the latest audio samples into 32 log-spaced band levels (40 Hz .. 16 kHz, bass first)
/// in 0..1, with automatic gain so quiet and loud music both reach a similar height.
/// The effects add their own sensitivity and attack/decay smoothing on top.
/// </summary>
sealed class Spectrum
{
    public const int FftSize = 2048, Bands = 32;
    const int FftBits = 11;
    const double TiltDbPerOctave = 3;  // lifts the treble: music carries far less energy up there
    const double RangeDb = 30;         // a band this far below the recent peak reads as 0
    const double AgcFloorDb = -60;     // never amplify more than this, so noise stays dark
    const double AgcDecayDbPerSecond = 3;
    const double PeakLevel = 0.7;      // level of the loudest recent band; leaves headroom for the effects' gain
    const float SilenceThreshold = 1e-4f;

    readonly float[] _window = new float[FftSize];
    readonly Complex[] _fft = new Complex[FftSize];
    readonly double[] _db = new double[Bands];
    double _agcDb = AgcFloorDb;

    /// <summary>Peak sample of the block analysed last, in dBFS (for diagnostics).</summary>
    public double InputPeakDb { get; private set; } = double.NegativeInfinity;

    public Spectrum()
    {
        for (int i = 0; i < FftSize; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize)); // Hann
    }

    /// <summary>samples: FftSize mono samples, or null for silence. Fills levels (Bands values).</summary>
    public void Update(float[]? samples, int sampleRate, double dt, float[] levels)
    {
        _agcDb = Math.Max(AgcFloorDb, _agcDb - AgcDecayDbPerSecond * dt);

        float peak = 0;
        if (samples != null)
            foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
        InputPeakDb = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        if (samples == null || peak < SilenceThreshold)
        {
            Array.Clear(levels);
            return;
        }

        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = samples[i] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftBits, _fft); // forward transform, scaled by 1/N

        int bins = FftSize / 2;
        double nyquist = sampleRate / 2.0;
        double max = double.NegativeInfinity;
        for (int b = 0; b < Bands; b++)
        {
            double f0 = 40 * Math.Pow(400, b / (double)Bands), f1 = 40 * Math.Pow(400, (b + 1) / (double)Bands);
            int i0 = (int)Math.Floor(f0 / nyquist * bins);
            int i1 = Math.Max(i0 + 1, (int)Math.Ceiling(f1 / nyquist * bins));
            double mag = 0;
            for (int i = Math.Max(1, i0); i < i1 && i < bins; i++)
                mag = Math.Max(mag, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
            // x4 undoes the Hann window and the split into two half spectra: a full-scale sine reads 0 dB.
            _db[b] = 20 * Math.Log10(mag * 4 + 1e-9) + TiltDbPerOctave * Math.Log2(Math.Sqrt(f0 * f1) / 40);
            max = Math.Max(max, _db[b]);
        }

        _agcDb = Math.Max(_agcDb, max);
        for (int b = 0; b < Bands; b++)
            levels[b] = (float)(PeakLevel * Math.Clamp((_db[b] - (_agcDb - RangeDb)) / RangeDb, 0, 1));
    }
}

/// <summary>
/// Counts the beats in the band levels. A beat is a frame in which the levels rise sharply
/// (the bass counts double) - at least half as sharply as the strongest rise of the last few
/// seconds, so it follows the drums of loud and quiet music alike and ignores sustained sound.
/// The audio effects use the count to change their random colours on every beat.
/// </summary>
sealed class BeatDetector
{
    const int BassBands = 10;          // up to about 220 Hz
    const double MinGapSeconds = 0.22; // no two beats closer than this
    const double PeakShare = 0.5;      // a beat rises at least this much of the recent strongest rise
    const double PeakSeconds = 3;      // how long a strong rise is remembered
    const double MinRise = 0.012;      // rises below this are never beats

    readonly float[] _previous = new float[Spectrum.Bands];
    double _peak, _sinceBeat = MinGapSeconds;

    /// <summary>How many beats have been heard so far.</summary>
    public int Count { get; private set; }

    /// <summary>levels: the band levels of this frame; dt: seconds since the previous frame.</summary>
    public void Update(float[] levels, double dt)
    {
        double rise = 0, weight = 0;
        for (int b = 0; b < levels.Length; b++)
        {
            double w = b < BassBands ? 2 : 1;
            rise += w * Math.Max(0, levels[b] - _previous[b]);
            weight += w;
            _previous[b] = levels[b];
        }
        rise /= weight;
        _sinceBeat += dt;
        _peak *= Math.Exp(-dt / PeakSeconds);
        if (rise > Math.Max(MinRise, _peak * PeakShare) && _sinceBeat >= MinGapSeconds)
        {
            Count++;
            _sinceBeat = 0;
        }
        _peak = Math.Max(_peak, rise);
    }
}
