using System.Buffers.Binary;
using HidSharp;

namespace DarkMountStudio;

/// <summary>One entry of the keyboard's lamp table: reported position (micrometres) and binding.</summary>
readonly record struct RawLamp(uint X, uint Y, int Binding);

/// <summary>
/// The Dark Mount's HID LampArray interface (usage page 0x59). Everything is a feature report
/// whose first byte is the report id. Not thread safe: the caller serialises access.
/// </summary>
sealed class Keyboard : IDisposable
{
    const int Vid = 0x373F, Pid = 0x0001;
    const int LampArrayUsagePage = 0x59;
    const byte ReportAttributes = 1, ReportLampRequest = 2, ReportLampResponse = 3, ReportMultiUpdate = 4, ReportControl = 6;
    const int LampsPerUpdate = 8;
    const int MultiUpdateLength = 3 + LampsPerUpdate * 2 + LampsPerUpdate * 3; // 43 bytes with the report id

    readonly HidStream _stream;
    readonly byte[] _buf; // every report is padded to the interface's longest feature report

    public int LampCount { get; }
    public int MinIntervalUs { get; }
    public string Path { get; }

    Keyboard(HidStream stream, int reportLength, string path)
    {
        _stream = stream;
        _buf = new byte[reportLength];
        Path = path;
        Get(ReportAttributes);
        LampCount = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(1));
        MinIntervalUs = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(19));
        if (LampCount < 1 || LampCount > 2048) throw new IOException($"Unexpected lamp count {LampCount}");
    }

    /// <summary>Opens the keyboard's LampArray interface, or returns null when it is not there.</summary>
    public static Keyboard? TryOpen()
    {
        var candidates = DeviceList.Local.GetHidDevices(Vid, Pid)
            .Select(d => (Device: d, Length: FeatureLength(d)))
            .Where(c => c.Length >= MultiUpdateLength)
            .OrderByDescending(c => IsLampArray(c.Device))
            .ThenBy(c => c.Length)
            .ToList();
        foreach (var (device, length) in candidates)
        {
            HidStream? stream = null;
            try
            {
                if (!device.TryOpen(out stream)) continue;
                return new Keyboard(stream, length, device.DevicePath); // reads report 1 as a check
            }
            catch
            {
                stream?.Dispose();
            }
        }
        return null;
    }

    static int FeatureLength(HidDevice d)
    {
        try { return d.GetMaxFeatureReportLength(); }
        catch { return 0; }
    }

    static bool IsLampArray(HidDevice d)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems
                .Any(item => item.Usages.GetAllValues().Any(usage => usage >> 16 == LampArrayUsagePage));
        }
        catch
        {
            return false;
        }
    }

    void Get(byte reportId)
    {
        Array.Clear(_buf);
        _buf[0] = reportId;
        _stream.GetFeature(_buf);
        if (_buf[0] != reportId) throw new IOException($"Feature report {reportId} came back as {_buf[0]}");
    }

    /// <summary>Cheap round trip that throws once the keyboard has been unplugged.</summary>
    public void Ping() => Get(ReportAttributes);

    /// <summary>Reads position and binding of every lamp. Returns null if the answers look wrong.</summary>
    public RawLamp[]? ReadLamps()
    {
        var lamps = new RawLamp[LampCount];
        for (int i = 0; i < LampCount; i++)
        {
            Array.Clear(_buf);
            _buf[0] = ReportLampRequest;
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(1), (ushort)i);
            _stream.SetFeature(_buf);
            Get(ReportLampResponse);
            if (BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(1)) != i) return null;
            lamps[i] = new RawLamp(
                BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(3)),
                BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(7)),
                _buf[27]);
        }
        return lamps;
    }

    /// <summary>true: the keyboard runs its own effect again. false: we drive the lamps.</summary>
    public void SetAutonomous(bool on)
    {
        Array.Clear(_buf);
        _buf[0] = ReportControl;
        _buf[1] = (byte)(on ? 1 : 0);
        _stream.SetFeature(_buf);
    }

    /// <summary>Sends one frame; rgb holds LampCount * 3 bytes.</summary>
    public void SendFrame(byte[] rgb)
    {
        int n = LampCount;
        for (int start = 0; start < n; start += LampsPerUpdate)
        {
            int c = Math.Min(LampsPerUpdate, n - start);
            Array.Clear(_buf);
            _buf[0] = ReportMultiUpdate;
            _buf[1] = (byte)c;
            _buf[2] = (byte)(start + LampsPerUpdate >= n ? 1 : 0); // LampUpdateComplete on the last chunk
            for (int k = 0; k < c; k++)
            {
                int id = start + k;
                _buf[3 + 2 * k] = (byte)id;
                _buf[4 + 2 * k] = (byte)(id >> 8);
                _buf[19 + 3 * k] = rgb[id * 3];
                _buf[20 + 3 * k] = rgb[id * 3 + 1];
                _buf[21 + 3 * k] = rgb[id * 3 + 2];
            }
            _stream.SetFeature(_buf);
        }
    }

    public void Dispose() => _stream.Dispose();
}
