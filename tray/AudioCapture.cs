using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DarkMountStudio;

/// <summary>
/// WASAPI loopback capture, mixed to mono into a ring buffer holding the most recent samples.
/// It follows the sound: of all active output devices it listens to the one that is playing
/// (headphones, speakers, a monitor...), with only one capture stream open at a time.
/// </summary>
sealed class AudioCapture : IDisposable
{
    const float SoundThreshold = 2e-4f; // captured samples quieter than this (about -74 dBFS) count as silence
    const float PlayingPeak = 1e-3f;    // an output device counts as playing above this peak meter value (-60 dBFS)
    const int KeepMs = 1500;            // stay on the current device while it was audible this recently

    static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    readonly object _gate = new();
    readonly float[] _ring = new float[Spectrum.FftSize];
    int _pos;
    long _lastData = long.MinValue / 2, _lastSound = long.MinValue / 2;

    WasapiLoopbackCapture? _capture;
    MMDevice? _device;
    string? _deviceId;
    volatile bool _stopped;
    int _channels, _bytesPerSample;
    bool _float;

    public int SampleRate { get; private set; } = 48000;
    public string? DeviceName { get; private set; }
    public string? Format { get; private set; }
    public bool Running => _capture != null && !_stopped;

    /// <summary>Milliseconds since the last audible sample was captured.</summary>
    public long MsSinceSound => Environment.TickCount64 - Volatile.Read(ref _lastSound);

    /// <summary>
    /// Call about once a second. Keeps the current device while it is audible; otherwise moves
    /// to the output device with the highest peak level, or - when nothing is playing anywhere -
    /// stays put, or starts on the default output device.
    /// </summary>
    public void EnsureRunning()
    {
        var devices = new List<MMDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) devices.Add(d);
            var current = Running ? devices.FirstOrDefault(d => d.ID == _deviceId) : null;
            if (current != null && MsSinceSound < KeepMs) return;

            var chosen = Pick(devices, devices.Select(Peak).ToList(), current, DefaultId(enumerator, Role.Multimedia));
            if (chosen == current)
            {
                if (chosen == null) Stop(); // no usable output device at the moment
                return;
            }
            devices.Remove(chosen!); // the capture owns it from here on
            Start(chosen!);
        }
        catch
        {
            // audio service hiccup: try again next time
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    /// <summary>The loudest device that is playing, else the current one, else the default output device.</summary>
    static MMDevice? Pick(IReadOnlyList<MMDevice> devices, IReadOnlyList<float> peaks, MMDevice? current, string? defaultId)
    {
        int best = -1;
        for (int i = 0; i < devices.Count; i++)
            if (peaks[i] > PlayingPeak && (best < 0 || peaks[i] > peaks[best])) best = i;
        return best >= 0 ? devices[best] : current ?? devices.FirstOrDefault(d => d.ID == defaultId);
    }

    static float Peak(MMDevice device)
    {
        try { return device.AudioMeterInformation.MasterPeakValue; }
        catch { return 0; }
    }

    static string? DefaultId(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return device.ID;
        }
        catch
        {
            return null; // no output device
        }
    }

    void Start(MMDevice device)
    {
        Stop();
        _device = device; // from here on Stop() cleans up whatever was created
        try
        {
            var capture = new WasapiLoopbackCapture(device);
            _capture = capture;
            var format = capture.WaveFormat;
            _float = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                     (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
            _bytesPerSample = format.BitsPerSample / 8;
            _channels = format.Channels;
            if (!(_float && _bytesPerSample == 4) && !(!_float && _bytesPerSample is 2 or 3 or 4))
                throw new NotSupportedException($"Unsupported audio format {format}");
            SampleRate = format.SampleRate;
            Format = Describe(format);
            DeviceName = device.FriendlyName;
            _deviceId = device.ID;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => { if (ReferenceEquals(_capture, capture)) _stopped = true; };
            _stopped = false;
            capture.StartRecording();
        }
        catch
        {
            Stop();
        }
    }

    static string Describe(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        return $"{format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {(isFloat ? "float" : "PCM")}";
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        _deviceId = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnData;
            try { capture.StopRecording(); } catch { /* already gone */ }
            try { capture.Dispose(); } catch { /* already gone */ }
        }
        try { _device?.Dispose(); } catch { /* already gone */ }
        _device = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _channels * _bytesPerSample;
        if (frameBytes == 0) return;
        var buf = e.Buffer;
        bool loud = false;
        lock (_gate)
        {
            for (int o = 0; o + frameBytes <= e.BytesRecorded; o += frameBytes)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    int i = o + c * _bytesPerSample;
                    sum += _float ? BitConverter.ToSingle(buf, i)
                        : _bytesPerSample == 2 ? BitConverter.ToInt16(buf, i) / 32768f
                        : _bytesPerSample == 3 ? ((buf[i] << 8 | buf[i + 1] << 16 | buf[i + 2] << 24) >> 8) / 8388608f
                        : BitConverter.ToInt32(buf, i) / 2147483648f;
                }
                float mono = sum / _channels;
                _ring[_pos] = mono;
                _pos = (_pos + 1) % _ring.Length;
                if (mono > SoundThreshold || mono < -SoundThreshold) loud = true;
            }
        }
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastData, now);
        if (loud) Volatile.Write(ref _lastSound, now);
    }

    /// <summary>
    /// Copies the most recent samples (oldest first). Returns false when nothing has arrived
    /// lately - loopback capture delivers no data at all while nothing is playing.
    /// </summary>
    public bool CopyLatest(float[] destination)
    {
        if (!Running || Environment.TickCount64 - Volatile.Read(ref _lastData) > 150) return false;
        lock (_gate)
        {
            int n = _ring.Length;
            Array.Copy(_ring, _pos, destination, 0, n - _pos);
            Array.Copy(_ring, 0, destination, n - _pos, _pos);
        }
        return true;
    }

    public void Dispose() => Stop();

    /// <summary>--audio-devices: lists the active output devices and which one would be listened to right now.</summary>
    public static void PrintDevices(TextWriter output)
    {
        var devices = new List<MMDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? multimedia = DefaultId(enumerator, Role.Multimedia), communications = DefaultId(enumerator, Role.Communications);
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) devices.Add(d);

            // The meter shows the peak of the last few milliseconds, so watch it for a second.
            var peaks = new float[devices.Count];
            for (int sample = 0; sample < 10; sample++)
            {
                for (int i = 0; i < devices.Count; i++) peaks[i] = Math.Max(peaks[i], Peak(devices[i]));
                Thread.Sleep(100);
            }
            var chosen = Pick(devices, peaks, null, multimedia);

            output.WriteLine($"Active output devices: {devices.Count} (peak = highest meter reading during 1 s; playing means above {PlayingPeak})");
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                string format;
                try
                {
                    using var client = d.AudioClient;
                    format = Describe(client.MixFormat);
                }
                catch (Exception e)
                {
                    format = "mix format unavailable: " + e.Message;
                }
                var roles = new List<string>();
                if (d.ID == multimedia) roles.Add("default multimedia");
                if (d.ID == communications) roles.Add("default communications");
                output.WriteLine($"{(d == chosen ? " -> " : "    ")}{d.FriendlyName}");
                output.WriteLine($"      {format}; peak {peaks[i].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}" +
                                 (roles.Count > 0 ? "; " + string.Join(", ", roles) : ""));
            }
            output.WriteLine(chosen == null ? "No output device to listen to."
                : $"Would listen to: {chosen.FriendlyName} ({(peaks[devices.IndexOf(chosen)] > PlayingPeak ? "the loudest device that is playing" : "nothing is playing; this is the default output device")})");
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }
}
