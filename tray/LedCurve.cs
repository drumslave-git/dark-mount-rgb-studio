namespace DarkMountStudio;

/// <summary>
/// Makes the keyboard show colours the way a screen does. A screen turns a colour value into
/// light along the sRGB curve: value 20 of 255 gives 0.7 % of full light. The keyboard's LEDs
/// are linear: 20 gives 8 %, so a colour that is nearly black in the preview glows visibly and
/// mixed colours come out paler. Each value is therefore passed through the sRGB curve before
/// it is sent. Values 1-6 become 0; the LEDs have nothing dimmer than 1/255 to show them with.
/// Only what goes to the keyboard is changed: the effects, the preview and --dump-frame are not.
/// </summary>
static class LedCurve
{
    static readonly byte[] Table = Build();

    static byte[] Build()
    {
        var table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double c = v / 255.0;
            double light = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            table[v] = (byte)Math.Round(light * 255);
        }
        return table;
    }

    /// <summary>The LED value that shows colour value v as a screen would.</summary>
    public static byte Of(byte v) => Table[v];

    /// <summary>Converts a whole frame; led must be as long as rgb.</summary>
    public static void Apply(ReadOnlySpan<byte> rgb, Span<byte> led)
    {
        for (int i = 0; i < rgb.Length; i++) led[i] = Table[rgb[i]];
    }
}
