namespace DarkMountStudio;

/// <summary>
/// A small baseline JPEG encoder for the display keys: 8-bit, three components at full resolution
/// (4:4:4), the standard quantisation and Huffman tables. GDI+ always halves the colour resolution
/// (4:2:0), which smears thin coloured lines on a 120 × 120 key; the keyboard shows 4:4:4 fine
/// (the official configurator writes it).
/// </summary>
static class Jpeg
{
    // Zig-zag position -> index in a block stored row by row (rows = vertical frequency).
    static readonly int[] Natural =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // ITU T.81 Annex K, row by row.
    static readonly int[] LumaQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    static readonly int[] ChromaQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    static readonly byte[] DcLumaBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    static readonly byte[] DcChromaBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    static readonly byte[] AcLumaBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    static readonly byte[] AcLumaValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];
    static readonly byte[] AcChromaBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    static readonly byte[] AcChromaValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    static readonly HuffmanTable DcLuma = new(DcLumaBits, DcValues), AcLuma = new(AcLumaBits, AcLumaValues);
    static readonly HuffmanTable DcChroma = new(DcChromaBits, DcValues), AcChroma = new(AcChromaBits, AcChromaValues);

    // Cos[x * 8 + u] = C(u) / 2 * cos((2x + 1) u pi / 16), C(0) = 1/sqrt(2), else 1: the DCT as two passes of 8 × 8.
    static readonly double[] Cos = Enumerable.Range(0, 64)
        .Select(i => (i % 8 == 0 ? Math.Sqrt(0.5) : 1) * 0.5 * Math.Cos((2 * (i / 8) + 1) * (i % 8) * Math.PI / 16)).ToArray();

    sealed class HuffmanTable
    {
        public readonly byte[] Bits, Values;
        public readonly int[] Code = new int[256], Length = new int[256];

        public HuffmanTable(byte[] bits, byte[] values)
        {
            Bits = bits;
            Values = values;
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++, code <<= 1)
            {
                for (int i = 0; i < bits[length - 1]; i++, k++, code++)
                {
                    Code[values[k]] = code;
                    Length[values[k]] = length;
                }
            }
        }
    }

    sealed class BitWriter(Stream output)
    {
        uint _bits;
        int _count;

        public void Put(int value, int count)
        {
            _bits = (_bits << count) | ((uint)value & ((1u << count) - 1));
            _count += count;
            while (_count >= 8)
            {
                byte b = (byte)(_bits >> (_count - 8));
                output.WriteByte(b);
                if (b == 0xFF) output.WriteByte(0); // byte stuffing
                _count -= 8;
            }
            _bits &= (1u << _count) - 1;
        }

        public void Code(HuffmanTable table, int symbol) => Put(table.Code[symbol], table.Length[symbol]);

        /// <summary>Pads the last byte with ones.</summary>
        public void Flush()
        {
            if (_count > 0) Put((1 << (8 - _count)) - 1, 8 - _count);
        }
    }

    /// <summary>Encodes `rgb` (width × height pixels of 3 bytes, row by row). Quality 1-100 scales the tables as libjpeg does.</summary>
    public static byte[] Encode(byte[] rgb, int width, int height, int quality)
    {
        if (rgb.Length < width * height * 3 || width is < 1 or > 65535 || height is < 1 or > 65535) throw new ArgumentException("Bad image size");
        int[] lumaQuant = Scaled(LumaQuant, quality), chromaQuant = Scaled(ChromaQuant, quality);
        var o = new MemoryStream();
        o.Write([0xFF, 0xD8]); // start of image
        o.Write([0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]); // JFIF 1.1, aspect 1:1
        o.Write([0xFF, 0xDB, 0, 132]); // both quantisation tables, in zig-zag order
        o.WriteByte(0);
        foreach (int k in Natural) o.WriteByte((byte)lumaQuant[k]);
        o.WriteByte(1);
        foreach (int k in Natural) o.WriteByte((byte)chromaQuant[k]);
        // Baseline frame: three components, none subsampled; Y uses table 0, Cb and Cr table 1.
        o.Write([0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);
        o.Write([0xFF, 0xC4, 0x01, 0xA2]); // the four Huffman tables, 418 bytes
        foreach (var (cls, table) in new[] { (0x00, DcLuma), (0x10, AcLuma), (0x01, DcChroma), (0x11, AcChroma) })
        {
            o.WriteByte((byte)cls);
            o.Write(table.Bits);
            o.Write(table.Values);
        }
        o.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]); // start of scan

        var bits = new BitWriter(o);
        var samples = new double[3][];
        for (int c = 0; c < 3; c++) samples[c] = new double[64];
        double[] temp = new double[64], dct = new double[64];
        int[] previousDc = new int[3];
        for (int by = 0; by < height; by += 8)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                // One block of each component; past the right or bottom edge the last pixel repeats.
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int i = (Math.Min(by + y, height - 1) * width + Math.Min(bx + x, width - 1)) * 3;
                        double r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                        samples[0][y * 8 + x] = 0.299 * r + 0.587 * g + 0.114 * b - 128;
                        samples[1][y * 8 + x] = -0.168736 * r - 0.331264 * g + 0.5 * b;
                        samples[2][y * 8 + x] = 0.5 * r - 0.418688 * g - 0.081312 * b;
                    }
                }
                for (int c = 0; c < 3; c++)
                {
                    Dct(samples[c], temp, dct);
                    var quant = c == 0 ? lumaQuant : chromaQuant;
                    previousDc[c] = Block(bits, dct, quant, previousDc[c], c == 0 ? DcLuma : DcChroma, c == 0 ? AcLuma : AcChroma);
                }
            }
        }
        bits.Flush();
        o.Write([0xFF, 0xD9]); // end of image
        return o.ToArray();
    }

    static int[] Scaled(int[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - 2 * quality;
        return table.Select(q => Math.Clamp((q * scale + 50) / 100, 1, 255)).ToArray();
    }

    // dct[v * 8 + u] = sum over y, x of block[y * 8 + x] * Cos[x * 8 + u] * Cos[y * 8 + v]
    static void Dct(double[] block, double[] temp, double[] dct)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++) sum += block[y * 8 + x] * Cos[x * 8 + u];
                temp[y * 8 + u] = sum;
            }
        }
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++) sum += temp[y * 8 + u] * Cos[y * 8 + v];
                dct[v * 8 + u] = sum;
            }
        }
    }

    /// <summary>Quantises and writes one block; returns its DC value for the next block of the component.</summary>
    static int Block(BitWriter bits, double[] dct, int[] quant, int previousDc, HuffmanTable dcTable, HuffmanTable acTable)
    {
        int dc = (int)Math.Round(dct[0] / quant[0], MidpointRounding.AwayFromZero);
        int diff = Math.Clamp(dc - previousDc, -2047, 2047);
        int size = Size(diff);
        bits.Code(dcTable, size);
        Value(bits, diff, size);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int n = Natural[k];
            int ac = Math.Clamp((int)Math.Round(dct[n] / quant[n], MidpointRounding.AwayFromZero), -1023, 1023);
            if (ac == 0)
            {
                run++;
                continue;
            }
            for (; run > 15; run -= 16) bits.Code(acTable, 0xF0); // sixteen zeros
            size = Size(ac);
            bits.Code(acTable, (run << 4) | size);
            Value(bits, ac, size);
            run = 0;
        }
        if (run > 0) bits.Code(acTable, 0x00); // end of block
        return previousDc + diff;
    }

    static int Size(int v)
    {
        v = Math.Abs(v);
        int size = 0;
        for (; v > 0; v >>= 1) size++;
        return size;
    }

    static void Value(BitWriter bits, int v, int size)
    {
        if (size > 0) bits.Put(v < 0 ? v + (1 << size) - 1 : v, size);
    }
}
