using System.Buffers.Binary;
using HidSharp;

namespace DarkMountStudio;

/// <summary>The keyboard answered a command with an error status.</summary>
sealed class QLinkException(byte feature, byte command, byte status)
    : IOException($"The keyboard refused command {feature}:{command} (status {status})")
{
    public byte Status { get; } = status;
}

/// <summary>
/// The Dark Mount's vendor channel ("QLink": HID interface MI_02, usage page 0xFF00), the one the
/// official web configurator talks to. Only what the display keys need is here.
///
/// Every packet is 64 bytes: [0] data length + 6, [1] sequence (bit 7: more packets follow),
/// [2] session, [3] status, [4] request id, [5] feature, [6] command, [7..] data, [62..63] CRC-16/Modbus
/// of [0..61], low byte first. The answer to a command carries the same request id, feature and
/// command; a long answer goes on in further packets whose data starts at [3].
/// Not thread safe.
/// </summary>
sealed class QLink : IDisposable
{
    const int Vid = 0x373F, Pid = 0x0001, VendorUsagePage = 0xFF00, PacketLength = 64;
    const byte FeatureRoot = 1, FeatureNumpad = 32;
    const byte OpenSession = 1, CloseSession = 2, ActiveSession = 6;
    const byte NumpadState = 1, SetImage = 2, GetImage = 3;
    const byte ClientWeb = 2, SessionActive = 1;
    const int AnswerTimeoutMs = 3000;
    const int WriteChunk = 49, ReadChunk = 54; // what fits next to the key id and offset of SetImage / GetImage
    public const int ImageHeaderLength = 9;    // total size incl. header (u32), width (u16), height (u16), format (u8)
    public const byte FormatJpeg = 3;

    readonly HidStream _stream;
    readonly byte[] _out, _in;
    byte _session, _own, _request;

    QLink(HidStream stream, int outLength, int inLength)
    {
        _stream = stream;
        _stream.ReadTimeout = 500;
        _out = new byte[outLength];
        _in = new byte[inLength];
        _request = (byte)Random.Shared.Next(1, 200);
        Open();
    }

    /// <summary>Opens the channel and a session on it, or returns null when the keyboard is not there.</summary>
    public static QLink? TryOpen()
    {
        foreach (var device in DeviceList.Local.GetHidDevices(Vid, Pid))
        {
            int outLength, inLength;
            try
            {
                outLength = device.GetMaxOutputReportLength();
                inLength = device.GetMaxInputReportLength();
                if (outLength < PacketLength + 1 || inLength < PacketLength + 1 || !IsVendor(device)) continue;
            }
            catch
            {
                continue;
            }
            if (!device.TryOpen(out var stream)) continue;
            try
            {
                return new QLink(stream, outLength, inLength);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        return null;
    }

    static bool IsVendor(HidDevice d) =>
        d.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Any(usage => usage >> 16 == VendorUsagePage));

    // A session as the official web app opens one: four random bytes and the client type. When a
    // browser tab with that app holds the active session, it is shared, as the app itself does.
    void Open()
    {
        Span<byte> hello = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(hello, (uint)Random.Shared.Next(10000, 100000));
        hello[4] = ClientWeb;
        byte[] answer = Command(FeatureRoot, OpenSession, hello, session: 0);
        if (answer.Length < 6) throw new IOException("Unexpected answer when opening a session");
        _own = _session = answer[4];
        if (answer[5] == SessionActive) return;
        byte[] active = Command(FeatureRoot, ActiveSession, []);
        if (active.Length < 2 || active[1] != ClientWeb) throw new IOException("Another program is using the keyboard's settings");
        _session = active[0];
    }

    /// <summary>Whether the numpad module, which has the display keys, is attached.</summary>
    public bool NumpadAttached() => Command(FeatureNumpad, NumpadState, []) is [not 0, ..];

    /// <summary>The header of the picture stored for a display key: total size including the header, width, height, format.</summary>
    public (int Size, int Width, int Height, int Format) ImageHeader(int keyId)
    {
        byte[] h = ReadImage(keyId, 0, ImageHeaderLength);
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(h), BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(6)), h[8]);
    }

    /// <summary>
    /// Whether a display key holds exactly these JPEG bytes, by their size and three samples of
    /// them (start, middle, end) rather than reading all of them, which would take a second or two.
    /// </summary>
    public bool Holds(int keyId, byte[] jpeg, int width, int height)
    {
        var (size, w, h, format) = ImageHeader(keyId);
        if (size != jpeg.Length + ImageHeaderLength || w != width || h != height || format != FormatJpeg) return false;
        foreach (int at in new[] { 0, jpeg.Length / 2, jpeg.Length - ReadChunk })
        {
            int start = Math.Max(0, at), n = Math.Min(ReadChunk, jpeg.Length - start);
            if (!ReadImage(keyId, ImageHeaderLength + start, n).AsSpan().SequenceEqual(jpeg.AsSpan(start, n))) return false;
        }
        return true;
    }

    byte[] ReadImage(int keyId, int offset, int count)
    {
        Span<byte> ask = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(ask, (ushort)keyId);
        BinaryPrimitives.WriteUInt32LittleEndian(ask[2..], (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(ask[6..], (uint)count);
        byte[] data = Command(FeatureNumpad, GetImage, ask);
        if (data.Length < count) throw new IOException("Short answer reading a display key");
        return data[..count];
    }

    /// <summary>
    /// Stores a JPEG for a display key, which shows it at once. Takes about 10 ms per 49 bytes.
    /// Stops early, leaving a partly written picture, as soon as `carryOn` says false.
    /// </summary>
    public bool WriteImage(int keyId, byte[] jpeg, int width, int height, Func<bool> carryOn)
    {
        Span<byte> packet = stackalloc byte[6 + WriteChunk];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)keyId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[6..], (uint)(jpeg.Length + ImageHeaderLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet[10..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[12..], (ushort)height);
        packet[14] = FormatJpeg;
        Command(FeatureNumpad, SetImage, packet[..(6 + ImageHeaderLength)]);
        for (int at = 0; at < jpeg.Length; at += WriteChunk)
        {
            if (!carryOn()) return false;
            int n = Math.Min(WriteChunk, jpeg.Length - at);
            BinaryPrimitives.WriteUInt32LittleEndian(packet[2..], (uint)(ImageHeaderLength + at));
            jpeg.AsSpan(at, n).CopyTo(packet[6..]);
            Command(FeatureNumpad, SetImage, packet[..(6 + n)]);
        }
        return true;
    }

    /// <summary>Sends a command and waits for its answer's data. Throws <see cref="QLinkException"/> on an error status.</summary>
    byte[] Command(byte feature, byte command, ReadOnlySpan<byte> data, byte? session = null)
    {
        if (data.Length > PacketLength - 9) throw new ArgumentException("Too much data for one packet", nameof(data));
        _request = (byte)(_request % 255 + 1);
        Array.Clear(_out);
        var p = _out.AsSpan(1, PacketLength); // [0] is the report id, 0
        p[0] = (byte)(data.Length + 6);
        p[2] = session ?? _session;
        p[4] = _request;
        p[5] = feature;
        p[6] = command;
        data.CopyTo(p[7..]);
        BinaryPrimitives.WriteUInt16LittleEndian(p[62..], Crc16(p[..62]));
        _stream.Write(_out);

        long deadline = Environment.TickCount64 + AnswerTimeoutMs;
        List<byte>? answer = null;
        byte status = 0;
        while (true)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"The keyboard did not answer command {feature}:{command}");
            int n;
            try { n = _stream.Read(_in); }
            catch (TimeoutException) { continue; }
            if (n < PacketLength + 1) continue;
            var r = _in.AsSpan(1, PacketLength);
            int end = Math.Min(r[0] + 1, PacketLength - 2);
            if (answer == null)
            {
                if (r[4] != _request || r[5] != feature || r[6] != command) continue; // a notification, or another program's traffic
                status = r[3];
                answer = new List<byte>(end > 7 ? r[7..end].ToArray() : []);
            }
            else if (end > 3)
            {
                answer.AddRange(r[3..end].ToArray());
            }
            if ((r[1] & 0x80) == 0) break;
        }
        if (status != 0) throw new QLinkException(feature, command, status);
        return answer.ToArray();
    }

    static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    public void Dispose()
    {
        try { Command(FeatureRoot, CloseSession, [_own], session: _own); }
        catch { /* unplugged, or the session timed out already */ }
        _stream.Dispose();
    }
}
