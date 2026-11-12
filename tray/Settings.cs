using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DarkMountStudio;

/// <summary>Parameters of one effect; only the ones the effect uses are set.</summary>
sealed class EffectParams
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Speed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Scale { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Angle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Density { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Width { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Arms { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Gain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Fade { get; set; }
    /// <summary>"Random colours": random bright colours in place of the palette's (effects with a palette only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Random { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Palette { get; set; }

    public double? Get(string name) => name switch
    {
        "speed" => Speed, "scale" => Scale, "angle" => Angle, "density" => Density,
        "width" => Width, "arms" => Arms, "gain" => Gain, "fade" => Fade, _ => null,
    };

    public void Set(string name, double? value)
    {
        switch (name)
        {
            case "speed": Speed = value; break;
            case "scale": Scale = value; break;
            case "angle": Angle = value; break;
            case "density": Density = value; break;
            case "width": Width = value; break;
            case "arms": Arms = value; break;
            case "gain": Gain = value; break;
            case "fade": Fade = value; break;
        }
    }
}

sealed class Zones
{
    public bool Keys { get; set; } = true;
    public bool Bars { get; set; } = true;
    public bool Numpad { get; set; } = true;
}

/// <summary>One layer of a layered preset: an effect with its own parameters, shown on its own group of lights.</summary>
sealed class Layer
{
    public string Name { get; set; } = "";
    public string Effect { get; set; } = "wave";
    public EffectParams Params { get; set; } = new();
    /// <summary>Lamp ids, ascending. The page keeps a light in one layer only; if it is in several, the later layer wins.</summary>
    public int[] Lamps { get; set; } = [];
}

/// <summary>A named set of layers. Never modified once parsed; changes replace the whole list of presets.</summary>
sealed class LayeredPreset
{
    public string Name { get; set; } = "";
    public List<Layer> Layers { get; set; } = new();
}

/// <summary>
/// The settings object shared with the settings page (JSON names are the camelCase property names).
/// Instances are treated as immutable once published by <see cref="SettingsStore"/>.
/// </summary>
sealed partial class Settings
{
    public bool Enabled { get; set; } = true;
    public string Effect { get; set; } = "wave";
    public Dictionary<string, EffectParams> Params { get; set; } = new();
    public int Brightness { get; set; } = 100;
    public Zones Zones { get; set; } = new();
    public int NumpadSide { get; set; } = 1; // 1 = left, 2 = right
    public Dictionary<string, string> Paint { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> PaintPresets { get; set; } = new(); // name -> paint map
    public string PaintColor { get; set; } = "#ff6a13"; // the colour the page paints with next
    public bool HandBackWhenSilent { get; set; } = true;
    /// <summary>Send colours through <see cref="LedCurve"/>, so the keyboard shows them as the preview does.</summary>
    public bool Gamma { get; set; } = true;
    public string Mode { get; set; } = "effect"; // "effect" = Effect on every light, "layers" = the selected layered preset
    public int LayerPreset { get; set; }         // index into LayerPresets
    public List<LayeredPreset> LayerPresets { get; set; } = new();

    /// <summary>The layers to draw, or null when a single effect runs on the whole board.</summary>
    [JsonIgnore]
    public List<Layer>? ActiveLayers =>
        Mode == "layers" && LayerPresets.Count > 0 ? LayerPresets[Math.Clamp(LayerPreset, 0, LayerPresets.Count - 1)].Layers : null;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Same names, compact; used by the HTTP API.</summary>
    public static readonly JsonSerializerOptions JsonCompact = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    /// <summary>Defaults, with every effect's parameters filled in.</summary>
    public static Settings Defaults()
    {
        var s = new Settings();
        foreach (var e in Effects.All) s.Params[e.Id] = DefaultParams(e);
        return s;
    }

    static EffectParams DefaultParams(EffectDef e)
    {
        var p = new EffectParams { Palette = (string[]?)e.Defaults.Palette?.Clone() };
        if (p.Palette != null) p.Random = false;
        foreach (string name in e.Params) p.Set(name, e.Defaults.Get(name));
        return p;
    }

    /// <summary>
    /// Brings settings saved by an older version up to date. Stripes' dark stripe used to default
    /// to #141414, which looks black on a screen but glows on the keyboard's LEDs; a palette that
    /// is still exactly that old default becomes the new one, with a truly black stripe.
    /// </summary>
    public void Migrate()
    {
        string[] old = ["#ff6a13", "#141414"];
        var stripes = Effects.ById["stripes"];
        void Fix(EffectParams p)
        {
            if (p.Palette != null && p.Palette.SequenceEqual(old)) p.Palette = (string[])stripes.Defaults.Palette!.Clone();
        }
        if (Params.TryGetValue("stripes", out var mine)) Fix(mine);
        foreach (var preset in LayerPresets)
            foreach (var layer in preset.Layers)
                if (layer.Effect == "stripes") Fix(layer.Params);
    }

    public Settings Clone()
    {
        var s = (Settings)MemberwiseClone();
        s.Zones = new Zones { Keys = Zones.Keys, Bars = Zones.Bars, Numpad = Zones.Numpad };
        s.Paint = new Dictionary<string, string>(Paint);
        s.PaintPresets = PaintPresets.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        s.LayerPresets = new List<LayeredPreset>(LayerPresets); // the presets themselves are never modified
        s.Params = Params.ToDictionary(kv => kv.Key, kv =>
        {
            var p = new EffectParams { Palette = (string[]?)kv.Value.Palette?.Clone(), Random = kv.Value.Random };
            foreach (string name in Effects.ParamRanges.Keys) p.Set(name, kv.Value.Get(name));
            return p;
        });
        return s;
    }

    /// <summary>
    /// Applies any subset of the settings from JSON. Top-level keys replace, except `params`
    /// (merged per effect and per key) and `zones` (merged per key). Unknown or badly typed
    /// values are ignored, numbers are clamped to their range.
    /// </summary>
    public void Apply(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in patch.EnumerateObject())
        {
            var v = prop.Value;
            switch (prop.Name)
            {
                case "enabled" when IsBool(v): Enabled = v.GetBoolean(); break;
                case "handBackWhenSilent" when IsBool(v): HandBackWhenSilent = v.GetBoolean(); break;
                case "gamma" when IsBool(v): Gamma = v.GetBoolean(); break;
                case "mode" when v.ValueKind == JsonValueKind.String && v.GetString() is "effect" or "layers":
                    Mode = v.GetString()!;
                    break;
                case "layerPreset" when v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int index):
                    LayerPreset = index;
                    break;
                case "layerPresets" when v.ValueKind == JsonValueKind.Array:
                    LayerPresets = ParseLayerPresets(v);
                    break;
                case "effect" when v.ValueKind == JsonValueKind.String && Effects.ById.ContainsKey(v.GetString()!):
                    Effect = v.GetString()!;
                    break;
                case "brightness" when v.ValueKind == JsonValueKind.Number:
                    Brightness = (int)Math.Round(Math.Clamp(v.GetDouble(), 5, 100));
                    break;
                case "numpadSide" when v.ValueKind == JsonValueKind.Number && v.GetDouble() is 1 or 2:
                    NumpadSide = (int)v.GetDouble();
                    break;
                case "zones" when v.ValueKind == JsonValueKind.Object:
                    foreach (var z in v.EnumerateObject())
                    {
                        if (!IsBool(z.Value)) continue;
                        if (z.Name == "keys") Zones.Keys = z.Value.GetBoolean();
                        else if (z.Name == "bars") Zones.Bars = z.Value.GetBoolean();
                        else if (z.Name == "numpad") Zones.Numpad = z.Value.GetBoolean();
                    }
                    break;
                case "params" when v.ValueKind == JsonValueKind.Object:
                    foreach (var e in v.EnumerateObject())
                    {
                        if (!Effects.ById.TryGetValue(e.Name, out var def) || e.Value.ValueKind != JsonValueKind.Object) continue;
                        ApplyParams(def, Params[def.Id], e.Value);
                    }
                    break;
                case "paint" when v.ValueKind == JsonValueKind.Object:
                    Paint = ParsePaint(v);
                    break;
                case "paintColor" when v.ValueKind == JsonValueKind.String && HexColour().IsMatch(v.GetString()!):
                    PaintColor = v.GetString()!.ToLowerInvariant();
                    break;
                case "paintPresets" when v.ValueKind == JsonValueKind.Object:
                    // Replaces all presets. Names are trimmed, 1-40 characters; at most 50 presets.
                    var presets = new Dictionary<string, Dictionary<string, string>>();
                    foreach (var kv in v.EnumerateObject())
                    {
                        string name = kv.Name.Trim();
                        if (name.Length is < 1 or > MaxPresetName || kv.Value.ValueKind != JsonValueKind.Object) continue;
                        if (presets.Count >= MaxPresets && !presets.ContainsKey(name)) continue;
                        presets[name] = ParsePaint(kv.Value);
                    }
                    PaintPresets = presets;
                    break;
            }
        }
        LayerPreset = Math.Clamp(LayerPreset, 0, Math.Max(0, LayerPresets.Count - 1));
    }

    /// <summary>Merges one effect's parameters from JSON into p: only the parameters that effect has, clamped to their range.</summary>
    static void ApplyParams(EffectDef def, EffectParams p, JsonElement values)
    {
        foreach (var kv in values.EnumerateObject())
        {
            if (kv.Name == "palette")
            {
                if (def.Defaults.Palette != null && ParsePalette(kv.Value) is { } palette) p.Palette = palette;
            }
            else if (kv.Name == "random")
            {
                if (def.Defaults.Palette != null && IsBool(kv.Value)) p.Random = kv.Value.GetBoolean();
            }
            else if (kv.Value.ValueKind == JsonValueKind.Number && def.Params.Contains(kv.Name))
            {
                var (min, max) = Effects.ParamRanges[kv.Name];
                p.Set(kv.Name, Math.Clamp(kv.Value.GetDouble(), min, max));
            }
        }
    }

    const int MaxPresets = 50, MaxPresetName = 40;
    public const int MaxLayerPresets = 20, MaxLayers = 16, MaxLampId = 2047;

    /// <summary>
    /// [{ name, layers: [{ name, effect, params, lamps: [lamp ids] }] }]. Names are trimmed and cut to 40
    /// characters (an empty one gets a numbered name), a layer with an unknown effect is left out,
    /// parameters start from the effect's defaults, lamp ids are sorted with duplicates removed.
    /// </summary>
    static List<LayeredPreset> ParseLayerPresets(JsonElement v)
    {
        static string Name(JsonElement owner, string fallback)
        {
            string name = owner.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            return name.Length == 0 ? fallback : name.Length > MaxPresetName ? name[..MaxPresetName].TrimEnd() : name;
        }

        var presets = new List<LayeredPreset>();
        foreach (var p in v.EnumerateArray())
        {
            if (presets.Count >= MaxLayerPresets) break;
            if (p.ValueKind != JsonValueKind.Object) continue;
            var preset = new LayeredPreset { Name = Name(p, $"Layered preset {presets.Count + 1}") };
            if (p.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in layers.EnumerateArray())
                {
                    if (preset.Layers.Count >= MaxLayers) break;
                    if (l.ValueKind != JsonValueKind.Object || !l.TryGetProperty("effect", out var id) ||
                        id.ValueKind != JsonValueKind.String || !Effects.ById.TryGetValue(id.GetString()!, out var def)) continue;
                    var layer = new Layer { Name = Name(l, $"Layer {preset.Layers.Count + 1}"), Effect = def.Id, Params = DefaultParams(def) };
                    if (l.TryGetProperty("params", out var values) && values.ValueKind == JsonValueKind.Object) ApplyParams(def, layer.Params, values);
                    if (l.TryGetProperty("lamps", out var lamps) && lamps.ValueKind == JsonValueKind.Array)
                    {
                        var ids = new SortedSet<int>();
                        foreach (var lamp in lamps.EnumerateArray())
                            if (lamp.ValueKind == JsonValueKind.Number && lamp.TryGetInt32(out int lampId) && lampId is >= 0 and <= MaxLampId) ids.Add(lampId);
                        layer.Lamps = ids.ToArray();
                    }
                    preset.Layers.Add(layer);
                }
            }
            presets.Add(preset);
        }
        return presets;
    }

    /// <summary>A paint map { "lampId": "#rrggbb" }; entries that are not valid are left out.</summary>
    static Dictionary<string, string> ParsePaint(JsonElement v)
    {
        var paint = new Dictionary<string, string>();
        foreach (var kv in v.EnumerateObject())
        {
            if (kv.Value.ValueKind == JsonValueKind.String && HexColour().IsMatch(kv.Value.GetString()!) &&
                int.TryParse(kv.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id <= MaxLampId)
                paint[id.ToString(CultureInfo.InvariantCulture)] = kv.Value.GetString()!.ToLowerInvariant();
        }
        return paint;
    }

    static bool IsBool(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False;

    static string[]? ParsePalette(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array) return null;
        int n = v.GetArrayLength();
        if (n < 1 || n > 16) return null;
        var colours = new string[n];
        int i = 0;
        foreach (var c in v.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.String || !HexColour().IsMatch(c.GetString()!)) return null;
            colours[i++] = c.GetString()!.ToLowerInvariant();
        }
        return colours;
    }

    public static double[] HexToRgb(string hex) =>
    [
        Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16),
    ];
}

/// <summary>
/// Holds the current settings, hands out immutable snapshots and saves changes to
/// settings.json a moment after the last change. The app is portable: the file sits next
/// to the exe, and nothing is kept anywhere else on the PC.
/// </summary>
sealed class SettingsStore : IDisposable
{
    public static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    readonly object _gate = new();
    readonly string? _path;
    readonly System.Threading.Timer _saveTimer;
    volatile Settings _current;
    bool _dirty;

    /// <summary>Raised (on the changing thread) after the settings changed.</summary>
    public event Action? Changed;

    public Settings Current => _current;

    /// <summary>persist = false starts from the defaults and keeps everything in memory (used by the self-test).</summary>
    public SettingsStore(bool persist = true)
    {
        _path = persist ? DefaultPath : null;
        _saveTimer = new System.Threading.Timer(_ => Flush());
        var s = Settings.Defaults();
        try
        {
            if (persist && File.Exists(DefaultPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(DefaultPath));
                s.Apply(doc.RootElement);
                s.Migrate();
            }
        }
        catch
        {
            // unreadable settings: start from the defaults
        }
        _current = s;
    }

    public Settings Update(Action<Settings> change)
    {
        Settings s;
        lock (_gate)
        {
            s = _current.Clone();
            change(s);
            _current = s;
            _dirty = true;
            _saveTimer.Change(500, Timeout.Infinite);
        }
        Changed?.Invoke();
        return s;
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            if (_path == null) return;
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_current, Settings.Json));
                File.Move(tmp, _path, true);
            }
            catch
            {
                // settings stay in memory; the next change tries again
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
