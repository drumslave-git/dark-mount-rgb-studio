namespace DarkMountStudio;

/// <summary>Where a light physically sits, in key units (1u = 19.05 mm).</summary>
readonly record struct LampPos(double X, double Y, bool Bar, bool Numpad);

/// <summary>
/// Physical positions of the Dark Mount's lights, looked up from each lamp's "binding" number.
/// A straight port of web/src/lib/geometry.ts (the positions the keyboard reports itself are inaccurate).
/// </summary>
static class Geometry
{
    // 0 is the left edge of Esc / the centre line of the F row.
    static readonly double[] Rows = { 0, 1.2, 2.2, 3.2, 4.2, 5.2 }; // F row, numbers, QWERTY, ASDF, ZXCV, bottom

    static readonly Dictionary<int, (double X, double Y)> Keys = new();
    static readonly Dictionary<int, (double X, double Y)> Pad = new();

    // Edge light bars follow the outline of each case.
    const double Top = -1.96, Bottom = 5.88;
    const int Side = 9;
    const double Gap = 0.1; // the numpad module sits flush against the main block

    readonly record struct Box(double X0, double X1, int Across, int First);
    static readonly Box Main = new(-0.34, 18.58, 23, 106);
    static readonly Box PadBox = new(-0.38, 4.4, 7, 170);

    static Geometry()
    {
        // Main block. Binding numbers are the keyboard's own key ids.
        Row(Rows[1], 1, 0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5, 10.5, 11.5, 12.5, 14); // ` 1..0 - = Bksp
        Row(Rows[2], 15, 0.75, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14.25); // Tab q..p [ ] \
        Row(Rows[3], 29, 0.875, 2.25, 3.25, 4.25, 5.25, 6.25, 7.25, 8.25, 9.25, 10.25, 11.25, 12.25, 13.875); // Caps a..l ; ' Enter
        Row(Rows[4], 42, 1.125, 2.75, 3.75, 4.75, 5.75, 6.75, 7.75, 8.75, 9.75, 10.75, 11.75, 13.625); // LShift z../ RShift
        Keys[54] = (0.625, Rows[5]); // LCtrl
        Keys[103] = (1.875, Rows[5]); // Win
        Keys[56] = (3.125, Rows[5]); // LAlt
        Keys[57] = (6.875, Rows[5]); // Space
        Keys[58] = (10.625, Rows[5]); // RAlt
        Keys[104] = (11.875, Rows[5]); // right Win
        Keys[55] = (13.125, Rows[5]); // Fn
        Keys[59] = (14.375, Rows[5]); // RCtrl
        Keys[105] = (1.75, Rows[4]); // ISO key (sits under the left Shift on ANSI boards)
        Row(Rows[0], 87, 0.5, 2.5, 3.5, 4.5, 5.5, 7, 8, 9, 10, 11.5, 12.5, 13.5, 14.5, 15.75, 16.75, 17.75); // Esc F1..F12 PrtSc ScrLk Pause
        Keys[60] = (15.75, Rows[1]); // Ins
        Keys[61] = (15.75, Rows[2]); // Del
        Keys[62] = (15.75, Rows[5]); // Left
        Keys[63] = (16.75, Rows[1]); // Home
        Keys[64] = (16.75, Rows[2]); // End
        Keys[65] = (16.75, Rows[4]); // Up
        Keys[66] = (16.75, Rows[5]); // Down
        Keys[67] = (17.75, Rows[1]); // PgUp
        Keys[68] = (17.75, Rows[2]); // PgDn
        Keys[69] = (17.75, Rows[5]); // Right

        // Numpad module, in its own coordinates (ids run down each column).
        Pad[70] = (0.5, Rows[1]); Pad[71] = (0.5, Rows[2]); Pad[72] = (0.5, Rows[3]); Pad[73] = (0.5, Rows[4]); // NumLk 7 4 1
        Pad[74] = (1.5, Rows[1]); Pad[75] = (1.5, Rows[2]); Pad[76] = (1.5, Rows[3]); Pad[77] = (1.5, Rows[4]); Pad[78] = (1, Rows[5]); // / 8 5 2 0
        Pad[79] = (2.5, Rows[1]); Pad[80] = (2.5, Rows[2]); Pad[81] = (2.5, Rows[3]); Pad[82] = (2.5, Rows[4]); Pad[83] = (2.5, Rows[5]); // * 9 6 3 .
        Pad[84] = (3.5, Rows[1]); Pad[85] = (3.5, 2.7); Pad[86] = (3.5, 4.7); // - + Enter
    }

    static void Row(double y, int first, params double[] xs)
    {
        for (int i = 0; i < xs.Length; i++) Keys[first + i] = (xs[i], y);
    }

    // Edge LEDs are numbered clockwise: top (left to right), right side (down),
    // bottom (right to left), left side (up).
    static (double X, double Y)? Perimeter(Box box, int index)
    {
        double SideY(int k) => Top + (k + 1) / (double)(Side + 1) * (Bottom - Top);
        double TopX(int k) => box.X0 + k / (double)(box.Across - 1) * (box.X1 - box.X0);
        if (index < box.Across) return (TopX(index), Top);
        index -= box.Across;
        if (index < Side) return (box.X1, SideY(index));
        index -= Side;
        if (index < box.Across) return (TopX(box.Across - 1 - index), Bottom);
        index -= box.Across;
        if (index < Side) return (box.X0, SideY(Side - 1 - index));
        return null;
    }

    /// <summary>
    /// Position of a lamp binding, or null when the binding is unknown.
    /// padSide: 1 = numpad attached on the left, 2 = on the right.
    /// </summary>
    public static LampPos? LampPosition(int binding, int padSide)
    {
        double padShift = padSide == 2 ? Main.X1 + Gap - PadBox.X0 : Main.X0 - Gap - PadBox.X1;
        if (Keys.TryGetValue(binding, out var k)) return new LampPos(k.X, k.Y, false, false);
        if (Pad.TryGetValue(binding, out var p)) return new LampPos(p.X + padShift, p.Y, false, true);
        int perMain = 2 * (Main.Across + Side);
        if (binding >= Main.First && binding < Main.First + perMain && Perimeter(Main, binding - Main.First) is { } m)
            return new LampPos(m.X, m.Y, true, false);
        int perPad = 2 * (PadBox.Across + Side);
        if (binding >= PadBox.First && binding < PadBox.First + perPad && Perimeter(PadBox, binding - PadBox.First) is { } q)
            return new LampPos(q.X + padShift, q.Y, true, true);
        return null;
    }

    /// <summary>
    /// Rough placement for a lamp whose binding is not in the tables above: the position the
    /// keyboard reported (micrometres), scaled onto the main block.
    /// </summary>
    public static LampPos FallbackPosition(uint xUm, uint yUm) => new(
        Main.X0 + (xUm - 100000.0) / 333790 * (Main.X1 - Main.X0),
        Top + yUm / 140000.0 * (Bottom - Top),
        false, false);
}
