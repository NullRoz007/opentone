using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTone;

/// <summary>What a <see cref="FirmwareJsonEntry"/> holds.</summary>
public enum FirmwareJsonKind
{
    /// <summary>Factory preset 1-30, used when a restore fills slots 1-30.</summary>
    FactoryPreset,
    /// <summary>The "EMPTY" template, used when a restore fills slots 31-60.</summary>
    EmptyPreset,
    /// <summary>A DSP model definition (defaults, UI metadata).</summary>
    Model,
}

/// <summary>A NUL-terminated JSON string inside the firmware image.</summary>
public sealed class FirmwareJsonEntry
{
    public FirmwareJsonKind Kind { get; init; }
    /// <summary>1-30 for factory presets, 0 otherwise.</summary>
    public int Slot { get; init; }
    /// <summary>Preset display name or model FenderId.</summary>
    public string Label { get; init; } = "";
    /// <summary>Offset of the first byte in the image file.</summary>
    public int Offset { get; init; }
    /// <summary>Current string length (excluding the NUL terminator).</summary>
    public int Length { get; internal set; }
    /// <summary>
    /// Largest length the string may have: its original length plus the zero padding before the
    /// next 4-byte boundary, keeping one NUL terminator.
    /// </summary>
    public int Capacity { get; init; }
    /// <summary>False when another pointer in the image points into the middle of this string.</summary>
    public bool Editable { get; init; }

    internal JsonNode OriginalTree { get; init; } = null!;
    internal List<(object[] Path, JsonNode? Value)> Edits { get; } = new();
}

/// <summary>Validation rule for one parameter value.</summary>
public sealed record ParamRule(JsonValueKind Kind, double? Min, double? Max, bool IntegerOnly, IReadOnlyList<string>? Allowed, string Source)
{
    public string Describe() => Kind switch
    {
        JsonValueKind.Number => $"{(IntegerOnly ? "integer" : "number")} {Fmt(Min)}..{Fmt(Max)} ({Source})",
        JsonValueKind.True or JsonValueKind.False => "true | false",
        JsonValueKind.String => $"one of: {string.Join(", ", Allowed ?? [])} ({Source})",
        _ => "not editable",
    };

    private static string Fmt(double? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "?";
}

/// <summary>One parameter of a preset node or model, as shown by <see cref="FirmwarePatcher.GetParameters"/>.</summary>
public sealed record ParamInfo(string NodeId, string FenderId, string Param, string Value, ParamRule Rule);

/// <summary>An uncompressed RGB565 (little-endian, row-major) bitmap drawn by the firmware's Image widget.</summary>
public sealed record FirmwareImage(string Name, string Description, int Offset, int Width, int Height)
{
    public int ByteLength => Width * Height * 2;
}

/// <summary>Code patches that change boot behaviour.</summary>
public enum BootPatch
{
    /// <summary>Restore all 60 presets from the factory set on every boot (same as holding buttons 1+2).</summary>
    ForceRestore,
    /// <summary>Invalidate the data-flash marker so the next boot erases the external flash, then restores presets.</summary>
    ReformatFlash,
}

/// <summary>
/// Guarded, byte-preserving editor for an LT25 firmware image. Every edit is checked against the
/// constraints learned from the firmware (see docs/mustang-lt25-firmware-map.md), and
/// <see cref="Build"/> re-validates the whole image before returning it.
/// </summary>
public sealed class FirmwarePatcher
{
    public const uint LoadAddress = 0x08020200;
    public const int PresetNameLength = 16;
    public const int VersionLength = 5;
    public const int FactoryPresetCount = 30;
    /// <summary>A preset is stored in one 4 KB data-flash slot, including its NUL.</summary>
    public const int DataFlashSlotSize = 0x1000;

    private static readonly Regex PresetNamePattern = new("^[A-Z0-9 ]{16}$");
    private static readonly Regex VersionPattern = new(@"^[0-9A-Z.\-]{5}$");

    // movs r0,#2; bl btn; mov r6,r0; cbz r0,..; mov r0,r4; bl ..; movs r0,#1; bl btn; mov r6,r0
    private static readonly byte[] ForceRestoreSignature =
        Convert.FromHexString("0220f6f7defc064630b12046f1f7f7ff0120f6f7d6fc0646");
    private static readonly (int At, byte[] Old, byte[] New)[] ForceRestoreEdits =
    [
        (8,  [0x30, 0xB1], [0x00, 0xBF]),   // cbz r0      -> nop
        (22, [0x06, 0x46], [0x01, 0x26]),   // mov r6, r0  -> movs r6, #1
    ];
    private static readonly byte[] FormattedMarker = Encoding.ASCII.GetBytes("FMIC FLASH IS FORMATTED\0");

    /// <summary>
    /// Bitmaps whose size and position are fixed by an Image(x, y, w, h) call. The signature is the code
    /// that passes the dimensions; the image is only offered when that code is found exactly once and
    /// the data address is referenced the expected number of times.
    /// </summary>
    private static readonly (string Name, string Description, uint Address, int Width, int Height, int PointerCount, byte[] Signature)[] KnownImages =
    [
        ("splash", "Startup splash logo, drawn at x=0, y=57 on the 128x160 screen", 0x081C3260, 128, 43, 2,
            Convert.FromHexString("2b22002103f11c002360e5618023606104f12000217600923922")),   // h=0x2b ... w=0x80 ... y=0x39
        ("tone", "Fender Tone icon (inferred: shown when Fender Tone connects), drawn at x=31, y=36", 0x081C03C0, 66, 58, 2,
            Convert.FromHexString("3a21242218460593009142231f21")),                           // h=0x3a, y=0x24, w=0x42, x=0x1f
    ];

    private readonly byte[] original;
    private readonly byte[] image;
    private readonly List<(int Start, int End, string What)> allowedRegions = new();
    private readonly Dictionary<(string FenderId, string Param), List<JsonNode>> observed = new();
    private readonly Dictionary<string, List<JsonNode>> observedByParam = new();
    private readonly int versionOffset;
    private readonly HashSet<BootPatch> bootPatches = new();

    public IReadOnlyList<FirmwareJsonEntry> Presets { get; }
    public FirmwareJsonEntry EmptyPreset { get; }
    public IReadOnlyDictionary<string, FirmwareJsonEntry> Models { get; }
    public string Version => ReadCString(image, versionOffset);
    public IReadOnlyList<FirmwareImage> Images { get; }
    public List<string> Changes { get; } = new();

    public FirmwarePatcher(byte[] firmware)
    {
        if (!FenderFirmware.ValidateFirmware(firmware, out uint expected, out uint calculated))
            throw new InvalidDataException($"Image CRC mismatch: calculated {calculated:X8}, expected {expected:X8}");

        original = (byte[])firmware.Clone();
        image = (byte[])firmware.Clone();

        var pointers = CollectPointers(image);
        Presets = FindPresetTable(pointers);
        EmptyPreset = FindEmptyPreset(pointers);
        Models = FindModels(pointers);
        versionOffset = FindVersion();
        Images = FindImages();

        foreach (var e in Presets.Append(EmptyPreset).Concat(Models.Values))
            allowedRegions.Add((e.Offset, e.Offset + e.Capacity + 1, $"{e.Kind} {e.Label}"));
        allowedRegions.Add((versionOffset, versionOffset + VersionLength, "version"));
        allowedRegions.Add((image.Length - 4, image.Length, "CRC"));

        CollectObservedValues();
    }

    // ---- discovery -----------------------------------------------------------------------

    private static string ReadCString(byte[] data, int offset)
    {
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) throw new InvalidDataException($"Unterminated string at 0x{offset:X}");
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private int ToOffset(uint address) =>
        address >= LoadAddress && address < LoadAddress + image.Length - 4 ? (int)(address - LoadAddress) : -1;

    /// <summary>Every 2-byte-aligned 32-bit word that points into the image.</summary>
    private HashSet<uint> CollectPointers(byte[] data)
    {
        var set = new HashSet<uint>();
        for (int i = 0; i + 4 <= data.Length - 4; i += 2)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            if (v >= LoadAddress && v < LoadAddress + data.Length) set.Add(v);
        }
        return set;
    }

    private FirmwareJsonEntry? TryReadJson(int offset, FirmwareJsonKind kind, int slot, HashSet<uint> pointers, Func<JsonNode, string?> label)
    {
        if (offset < 0 || image[offset] != (byte)'{') return null;
        int end = Array.IndexOf(image, (byte)0, offset);
        if (end < 0 || end - offset >= DataFlashSlotSize) return null;

        JsonNode? tree;
        try { tree = JsonNode.Parse(Encoding.ASCII.GetString(image, offset, end - offset)); }
        catch (JsonException) { return null; }
        if (tree is not JsonObject) return null;

        string? name = label(tree);
        if (name == null) return null;

        // Slack: zero bytes after the terminator up to the next 4-byte boundary.
        int pad = 0;
        while (((end + 1 + pad) % 4) != 0 && end + 1 + pad < image.Length - 4 && image[end + 1 + pad] == 0) pad++;

        // A pointer into the middle of the string would see shifted bytes after an edit.
        bool interior = false;
        for (int i = offset + 1; i <= end && !interior; i++)
            interior = pointers.Contains(LoadAddress + (uint)i);

        return new FirmwareJsonEntry
        {
            Kind = kind, Slot = slot, Label = name, Offset = offset,
            Length = end - offset, Capacity = end - offset + pad,
            Editable = !interior, OriginalTree = tree,
        };
    }

    private static string? PresetLabel(JsonNode n) =>
        (string?)n["nodeType"] == "preset" ? (string?)n["info"]?["displayName"] : null;

    private static string? ModelLabel(JsonNode n) =>
        (string?)n["nodeType"] == "dspUnit" ? (string?)n["FenderId"] : null;

    private List<FirmwareJsonEntry> FindPresetTable(HashSet<uint> pointers)
    {
        var tables = new List<List<FirmwareJsonEntry>>();
        for (int t = 0; t + 4 * FactoryPresetCount <= image.Length - 4; t += 4)
        {
            int first = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(t)));
            if (first < 0 || image[first] != (byte)'{') continue;

            var entries = new List<FirmwareJsonEntry>();
            for (int i = 0; i < FactoryPresetCount; i++)
            {
                int off = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(t + 4 * i)));
                var e = TryReadJson(off, FirmwareJsonKind.FactoryPreset, i + 1, pointers, PresetLabel);
                if (e == null) break;
                entries.Add(e);
            }
            if (entries.Count == FactoryPresetCount) tables.Add(entries);
        }

        if (tables.Count != 1)
            throw new NotSupportedException($"Expected exactly one factory preset table, found {tables.Count}. Unsupported firmware.");
        return tables[0];
    }

    private List<int> FindAll(byte[] needle)
    {
        var hits = new List<int>();
        var span = image.AsSpan(0, image.Length - 4);
        for (int i = 0; ;)
        {
            int j = span[i..].IndexOf(needle);
            if (j < 0) return hits;
            hits.Add(i + j);
            i += j + 1;
        }
    }

    private FirmwareJsonEntry FindEmptyPreset(HashSet<uint> pointers)
    {
        var tableOffsets = Presets.Select(p => p.Offset).ToHashSet();
        var found = FindAll(Encoding.ASCII.GetBytes("{\"nodeType\": \"preset\""))
            .Concat(FindAll(Encoding.ASCII.GetBytes("{\"info\": {\"displayName\"")))
            .Where(o => !tableOffsets.Contains(o))
            .Select(o => TryReadJson(o, FirmwareJsonKind.EmptyPreset, 0, pointers, PresetLabel))
            .Where(e => e != null && e.Label.Trim() == "EMPTY")
            .ToList();

        if (found.Count != 1)
            throw new NotSupportedException($"Expected exactly one EMPTY preset template, found {found.Count}. Unsupported firmware.");
        return found[0]!;
    }

    private Dictionary<string, FirmwareJsonEntry> FindModels(HashSet<uint> pointers)
    {
        var models = new Dictionary<string, FirmwareJsonEntry>();
        // Some definitions start with other keys (e.g. "fixme"), so find the marker anywhere and
        // walk back to the start of the NUL-terminated string that contains it.
        var starts = FindAll(Encoding.ASCII.GetBytes("\"nodeType\":\"dspUnit\""))
            .Select(i => Array.LastIndexOf(image, (byte)0, i) + 1)
            .Distinct();
        foreach (int o in starts)
        {
            var e = TryReadJson(o, FirmwareJsonKind.Model, 0, pointers, ModelLabel);
            if (e == null) continue;
            if (!models.TryAdd(e.Label, e))
                throw new NotSupportedException($"Model {e.Label} is defined twice. Unsupported firmware.");
        }
        if (models.Count == 0) throw new NotSupportedException("No DSP model definitions found. Unsupported firmware.");
        return models;
    }

    private int FindVersion()
    {
        // The version sits in an 8-byte slot directly before "mustang-lt-25".
        var hits = FindAll(Encoding.ASCII.GetBytes("\0mustang-lt-25\0")).ToList();
        if (hits.Count != 1) throw new NotSupportedException("Could not locate the version string. Unsupported firmware.");
        int slot = hits[0] + 1 - 8;
        string v = ReadCString(image, slot);
        if (v.Length != VersionLength) throw new NotSupportedException($"Unexpected version string '{v}'. Unsupported firmware.");
        return slot;
    }

    private List<FirmwareImage> FindImages()
    {
        var found = new List<FirmwareImage>();
        foreach (var k in KnownImages)
        {
            int offset = ToOffset(k.Address);
            if (offset < 0 || offset + k.Width * k.Height * 2 > image.Length - 4) continue;
            if (FindAll(k.Signature).Count != 1) continue;

            int refs = 0;
            for (int i = 0; i + 4 <= image.Length - 4; i += 2)
                if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i)) == k.Address) refs++;
            if (refs != k.PointerCount) continue;

            found.Add(new FirmwareImage(k.Name, k.Description, offset, k.Width, k.Height));
        }
        return found;
    }

    public FirmwareImage GetImage(string name) =>
        Images.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown or unsupported image '{name}'. Available: {(Images.Count > 0 ? string.Join(", ", Images.Select(i => i.Name)) : "none for this firmware")}.");

    /// <summary>Reads an image as 0xRRGGBB pixels.</summary>
    public int[] ReadImage(FirmwareImage img)
    {
        var px = new int[img.Width * img.Height];
        for (int i = 0; i < px.Length; i++)
            px[i] = Bmp.FromRgb565(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(img.Offset + i * 2)));
        return px;
    }

    /// <summary>
    /// Replaces an image's pixels. The new image must have exactly the same dimensions, since the size
    /// and position are compiled into the drawing code and the data is packed between other data.
    /// </summary>
    public void WriteImage(FirmwareImage img, int width, int height, int[] pixels, string source)
    {
        if (width != img.Width || height != img.Height)
            throw new ArgumentException($"{img.Name} must be exactly {img.Width}x{img.Height} pixels, {source} is {width}x{height}.");
        if (pixels.Length != width * height) throw new ArgumentException($"{source}: pixel count does not match its size.");

        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(img.Offset + i * 2), Bmp.ToRgb565(pixels[i]));
        allowedRegions.Add((img.Offset, img.Offset + img.ByteLength, $"image {img.Name}"));
        Changes.Add($"image {img.Name}: replaced with {source} ({width}x{height})");
    }

    private void CollectObservedValues()
    {
        void Add(string fenderId, string param, JsonNode? v)
        {
            if (v == null) return;
            var key = (fenderId, param);
            if (!observed.TryGetValue(key, out var list)) observed[key] = list = new();
            list.Add(v.DeepClone());
            if (!observedByParam.TryGetValue(param, out var all)) observedByParam[param] = all = new();
            all.Add(v.DeepClone());
        }

        foreach (var p in Presets.Append(EmptyPreset))
            foreach (var node in p.OriginalTree["audioGraph"]?["nodes"]?.AsArray() ?? [])
                if (node?["dspUnitParameters"] is JsonObject ps)
                    foreach (var kv in ps) Add((string?)node["FenderId"] ?? "", kv.Key, kv.Value);

        foreach (var m in Models.Values)
            if (m.OriginalTree["defaultDspUnitParameters"] is JsonObject ds)
                foreach (var kv in ds) Add(m.Label, kv.Key, kv.Value);
    }

    // ---- lookup ----------------------------------------------------------------------------

    /// <summary>Resolves "1".."30" to a factory preset, or "empty" to the EMPTY template.</summary>
    public FirmwareJsonEntry GetPreset(string slot)
    {
        if (slot.Equals("empty", StringComparison.OrdinalIgnoreCase)) return EmptyPreset;
        if (int.TryParse(slot, out int n) && n >= 1 && n <= FactoryPresetCount) return Presets[n - 1];
        throw new ArgumentException($"Preset slot must be 1-{FactoryPresetCount} or 'empty' (slots 31-60), got '{slot}'.");
    }

    public FirmwareJsonEntry GetModel(string fenderId) =>
        Models.TryGetValue(fenderId, out var m) ? m
        : Models.TryGetValue("DUBS_" + fenderId, out m) ? m
        : throw new ArgumentException($"Unknown model '{fenderId}'.");

    /// <summary>Finds the entry that starts at an image offset (for the JsonString-based wrappers).</summary>
    public FirmwareJsonEntry GetEntryAt(int offset) =>
        Presets.Append(EmptyPreset).Concat(Models.Values).FirstOrDefault(e => e.Offset == offset)
        ?? throw new ArgumentException($"No editable preset or model starts at 0x{offset:X}.");

    private JsonNode CurrentTree(FirmwareJsonEntry e) =>
        JsonNode.Parse(Encoding.ASCII.GetString(image, e.Offset, e.Length))!;

    public string GetJson(FirmwareJsonEntry e) => Encoding.ASCII.GetString(image, e.Offset, e.Length);

    /// <summary>Lists every editable parameter of a preset (per node) or a model (defaults).</summary>
    public IReadOnlyList<ParamInfo> GetParameters(FirmwareJsonEntry e)
    {
        var tree = CurrentTree(e);
        var result = new List<ParamInfo>();
        if (e.Kind == FirmwareJsonKind.Model)
        {
            foreach (var kv in tree["defaultDspUnitParameters"]?.AsObject() ?? [])
                result.Add(new ParamInfo("defaults", e.Label, kv.Key, kv.Value?.ToJsonString() ?? "null", RuleFor(e.Label, kv.Key, kv.Value)));
            return result;
        }
        foreach (var node in tree["audioGraph"]?["nodes"]?.AsArray() ?? [])
        {
            string nodeId = (string?)node?["nodeId"] ?? "?";
            string fenderId = (string?)node?["FenderId"] ?? "?";
            foreach (var kv in node?["dspUnitParameters"]?.AsObject() ?? [])
                result.Add(new ParamInfo(nodeId, fenderId, kv.Key, kv.Value?.ToJsonString() ?? "null", RuleFor(fenderId, kv.Key, kv.Value)));
        }
        return result;
    }

    // ---- rules -----------------------------------------------------------------------------

    private ParamRule RuleFor(string fenderId, string param, JsonNode? current)
    {
        JsonValueKind kind = current?.GetValueKind() ?? JsonValueKind.Null;
        if (kind is JsonValueKind.True or JsonValueKind.False) return new ParamRule(JsonValueKind.True, null, null, false, null, "bool");

        bool integerOnly = kind == JsonValueKind.Number && !current!.ToJsonString().Any(c => c is '.' or 'e' or 'E');
        var ui = Models.TryGetValue(fenderId, out var model)
            ? model.OriginalTree["ui"]?["uiParameters"]?.AsArray().FirstOrDefault(p => (string?)p?["controlId"] == param)
            : null;

        if (kind == JsonValueKind.Number)
        {
            if (ui?["min"] is JsonValue mn && ui["max"] is JsonValue mx)
                return new ParamRule(kind, mn.GetValue<double>(), mx.GetValue<double>(), integerOnly, null, "model UI range");

            if (ui?["listItems"] is JsonArray items)
                return new ParamRule(kind, 0, items.Count - 1, true, null, "list index");

            var values = Observed(fenderId, param).Where(v => v.GetValueKind() == JsonValueKind.Number)
                .Select(v => v.GetValue<double>()).ToList();
            if (values.Count == 0) return new ParamRule(JsonValueKind.Undefined, null, null, false, null, "no reference values");
            if (values.All(v => v >= 0 && v <= 1)) return new ParamRule(kind, 0, 1, integerOnly, null, "factory values are 0..1");
            return new ParamRule(kind, values.Min(), values.Max(), integerOnly, null, "range seen in factory data");
        }

        if (kind == JsonValueKind.String)
        {
            // Only values the firmware itself uses for this parameter name.
            var allowed = observedByParam.TryGetValue(param, out var all)
                ? all.Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>())
                    .Distinct().Order(StringComparer.Ordinal).ToList()
                : new List<string>();
            return new ParamRule(kind, null, null, false, allowed, "values used in factory data");
        }

        return new ParamRule(JsonValueKind.Undefined, null, null, false, null, $"unsupported type {kind}");
    }

    private IEnumerable<JsonNode> Observed(string fenderId, string param) =>
        observed.TryGetValue((fenderId, param), out var l) ? l
        : observedByParam.TryGetValue(param, out var all) ? all : [];

    /// <summary>Checks a value against a rule and returns its JSON text.</summary>
    private static (string Json, JsonNode Node, string? Note) FormatValue(ParamRule rule, string value, string what)
    {
        switch (rule.Kind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                if (!bool.TryParse(value, out bool b)) throw new ArgumentException($"{what}: expected true or false, got '{value}'.");
                return (b ? "true" : "false", JsonValue.Create(b), null);

            case JsonValueKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                    throw new ArgumentException($"{what}: expected a number, got '{value}'.");
                if (rule.IntegerOnly && d != Math.Floor(d))
                    throw new ArgumentException($"{what}: expected an integer, got '{value}'.");
                if (d < rule.Min || d > rule.Max)
                    throw new ArgumentException($"{what}: {value} is outside {rule.Describe()}.");
                // Factory data uses at most 6 decimals and never exponents.
                string text = rule.IntegerOnly
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture);
                double stored = double.Parse(text, CultureInfo.InvariantCulture);
                string? note = Math.Abs(stored - d) > 1e-12 ? $"rounded to {text}" : null;
                return (text, JsonValue.Create(stored), note);

            case JsonValueKind.String:
                if (rule.Allowed == null || !rule.Allowed.Contains(value))
                    throw new ArgumentException($"{what}: '{value}' is not allowed; {rule.Describe()}.");
                return (JsonSerializer.Serialize(value), JsonValue.Create(value), null);

            default:
                throw new ArgumentException($"{what}: this parameter cannot be edited ({rule.Source}).");
        }
    }

    // ---- low-level edit ----------------------------------------------------------------------

    /// <summary>Finds the raw byte span of the scalar value at <paramref name="path"/> (exactly one match).</summary>
    private static (int Start, int Length, JsonTokenType Type) FindValueSpan(byte[] json, object[] path)
    {
        var reader = new Utf8JsonReader(json);
        var frames = new List<(bool IsArray, int Index, string? Prop, object? Segment)>();
        (int, int, JsonTokenType)? match = null;

        object? NextSegment()
        {
            if (frames.Count == 0) return null;
            var f = frames[^1];
            if (f.IsArray) { f.Index++; frames[^1] = f; return f.Index; }
            return f.Prop;
        }

        bool Matches(object? segment)
        {
            var full = frames.Skip(1).Select(f => f.Segment).Append(segment).ToList();
            return full.Count == path.Length && full.Zip(path).All(z => Equals(z.First, z.Second));
        }

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    var top = frames[^1]; top.Prop = reader.GetString(); frames[^1] = top;
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    object? seg = NextSegment();
                    if (frames.Count > 0 && Matches(seg)) throw new ArgumentException("Target is an object or array, not a value.");
                    frames.Add((reader.TokenType == JsonTokenType.StartArray, -1, null, seg));
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    break;
                default:
                    object? s = NextSegment();
                    if (!Matches(s)) break;
                    if (match != null) throw new InvalidDataException("Target appears more than once in the JSON.");
                    int start = (int)reader.TokenStartIndex;
                    int length = reader.TokenType == JsonTokenType.String ? reader.ValueSpan.Length + 2 : reader.ValueSpan.Length;
                    match = (start, length, reader.TokenType);
                    break;
            }
        }

        return match ?? throw new ArgumentException($"'{string.Join(".", path)}' was not found.");
    }

    private void ReplaceValue(FirmwareJsonEntry e, object[] path, string newJson, JsonNode newNode, string description)
    {
        if (!e.Editable)
            throw new InvalidOperationException($"{e.Kind} '{e.Label}' is referenced from inside its text and cannot be edited safely.");

        byte[] current = image.AsSpan(e.Offset, e.Length).ToArray();
        var (start, length, _) = FindValueSpan(current, path);
        byte[] replacement = Encoding.ASCII.GetBytes(newJson);

        byte[] next = [.. current.AsSpan(0, start), .. replacement, .. current.AsSpan(start + length)];
        if (next.Length > e.Capacity)
            throw new InvalidOperationException(
                $"{description}: the new value needs {next.Length - e.Length} more byte(s), but {e.Kind} '{e.Label}' " +
                $"only has {e.Capacity - e.Length} spare. Choose a shorter value (fewer digits or characters).");

        // Write in place; the firmware reads with strlen, so NUL-fill whatever the string gave up.
        next.CopyTo(image, e.Offset);
        Array.Clear(image, e.Offset + next.Length, e.Capacity + 1 - next.Length);
        e.Length = next.Length;
        e.Edits.Add((path, newNode));
        Changes.Add(description);
    }

    // ---- edits ----------------------------------------------------------------------------------

    /// <summary>
    /// Builds a 16-character display name. "LINE1|LINE2" pads each half to 8 characters (the display
    /// shows the name as two 8-character halves); otherwise the name is padded on the right.
    /// </summary>
    public static string FormatPresetName(string input)
    {
        string name = input.ToUpperInvariant();
        int bar = name.IndexOf('|');
        if (bar >= 0)
        {
            string a = name[..bar], b = name[(bar + 1)..];
            if (a.Length > 8 || b.Length > 8) throw new ArgumentException("Each half of LINE1|LINE2 may be at most 8 characters.");
            name = a.PadRight(8) + b.PadRight(8);
        }
        if (name.Length > PresetNameLength) throw new ArgumentException($"Preset names are at most {PresetNameLength} characters, got {name.Length}.");
        return name.PadRight(PresetNameLength);
    }

    /// <summary>Renames a preset. The name must be exactly 16 characters of A-Z, 0-9 and space.</summary>
    public void SetPresetName(FirmwareJsonEntry preset, string name)
    {
        if (preset.Kind == FirmwareJsonKind.Model) throw new ArgumentException("Models have no preset name.");
        if (name.Length != PresetNameLength)
            throw new ArgumentException($"Preset names must be exactly {PresetNameLength} characters (pad with spaces), got {name.Length}: '{name}'.");
        if (!PresetNamePattern.IsMatch(name))
            throw new ArgumentException($"Preset names may only contain A-Z, 0-9 and spaces (the characters used by the factory presets): '{name}'.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Preset name cannot be blank.");

        ReplaceValue(preset, ["info", "displayName"], JsonSerializer.Serialize(name), JsonValue.Create(name),
            $"{Describe(preset)}: name '{preset.Label}' -> '{name}'");
    }

    /// <summary>Changes one parameter of one node in a preset, e.g. node "amp", param "gain".</summary>
    public string? SetPresetParam(FirmwareJsonEntry preset, string nodeId, string param, string value)
    {
        if (preset.Kind == FirmwareJsonKind.Model) throw new ArgumentException("Use SetModelDefault for models.");
        var nodes = CurrentTree(preset)["audioGraph"]?["nodes"]?.AsArray()
            ?? throw new InvalidDataException("Preset has no audioGraph.nodes.");
        int index = nodes.Select((n, i) => (n, i)).Where(x => (string?)x.n?["nodeId"] == nodeId).Select(x => x.i).DefaultIfEmpty(-1).First();
        if (index < 0) throw new ArgumentException($"{Describe(preset)} has no node '{nodeId}'. Nodes: {string.Join(", ", nodes.Select(n => (string?)n?["nodeId"]))}.");

        var node = nodes[index]!;
        string fenderId = (string?)node["FenderId"] ?? "";
        var parameters = node["dspUnitParameters"]?.AsObject();
        if (parameters == null || !parameters.TryGetPropertyValue(param, out var currentValue))
            throw new ArgumentException($"{nodeId} ({fenderId}) has no parameter '{param}'. Parameters: {string.Join(", ", parameters?.Select(p => p.Key) ?? [])}.");

        var rule = RuleFor(fenderId, param, currentValue);
        string what = $"{Describe(preset)} {nodeId}.{param}";
        var (json, newNode, note) = FormatValue(rule, value, what);
        ReplaceValue(preset, ["audioGraph", "nodes", index, "dspUnitParameters", param], json, newNode,
            $"{what} ({fenderId}): {currentValue?.ToJsonString()} -> {json}");
        return note;
    }

    /// <summary>Changes a model's default parameter value (used when that model is selected).</summary>
    public string? SetModelDefault(FirmwareJsonEntry model, string param, string value)
    {
        if (model.Kind != FirmwareJsonKind.Model) throw new ArgumentException("Not a model.");
        var defaults = CurrentTree(model)["defaultDspUnitParameters"]?.AsObject();
        if (defaults == null || !defaults.TryGetPropertyValue(param, out var currentValue))
            throw new ArgumentException($"{model.Label} has no default '{param}'. Parameters: {string.Join(", ", defaults?.Select(p => p.Key) ?? [])}.");

        var rule = RuleFor(model.Label, param, currentValue);
        string what = $"{model.Label} default {param}";
        var (json, newNode, note) = FormatValue(rule, value, what);
        ReplaceValue(model, ["defaultDspUnitParameters", param], json, newNode, $"{what}: {currentValue?.ToJsonString()} -> {json}");
        return note;
    }

    /// <summary>
    /// Changes the firmware version string. It must stay exactly 5 characters: the title screen
    /// copies it with a fixed length of 5.
    /// </summary>
    public void SetVersion(string version)
    {
        if (version.Length != VersionLength)
            throw new ArgumentException($"The version must be exactly {VersionLength} characters (the title screen shows exactly {VersionLength}), got '{version}'.");
        if (!VersionPattern.IsMatch(version))
            throw new ArgumentException($"The version may only contain 0-9, A-Z, '.' and '-': '{version}'.");

        string old = Version;
        Encoding.ASCII.GetBytes(version).CopyTo(image, versionOffset);
        Changes.Add($"version: {old} -> {version}");
    }

    /// <summary>Applies a boot-behaviour code patch. Refuses unless the expected code is found exactly once.</summary>
    public void Apply(BootPatch patch)
    {
        if (!bootPatches.Add(patch)) return;
        switch (patch)
        {
            case BootPatch.ForceRestore:
            {
                var hits = FindAll(ForceRestoreSignature).ToList();
                if (hits.Count != 1)
                    throw new NotSupportedException($"Force-restore: expected the boot button code once, found it {hits.Count} times. Unsupported firmware or already patched.");
                foreach (var (at, old, @new) in ForceRestoreEdits)
                {
                    int o = hits[0] + at;
                    if (!image.AsSpan(o, old.Length).SequenceEqual(old)) throw new InvalidDataException("Force-restore: unexpected instruction bytes.");
                    @new.CopyTo(image, o);
                    allowedRegions.Add((o, o + @new.Length, "force-restore"));
                }
                Changes.Add("boot: restore all presets on every boot (reflash stock firmware afterwards)");
                break;
            }
            case BootPatch.ReformatFlash:
            {
                var hits = FindAll(FormattedMarker).ToList();
                if (hits.Count != 1)
                    throw new NotSupportedException($"Reformat-flash: expected the data-flash marker once, found it {hits.Count} times.");
                int o = hits[0] + FormattedMarker.Length - 2;      // last character before the NUL
                image[o] = (byte)'X';
                allowedRegions.Add((o, o + 1, "reformat-flash"));
                Changes.Add("boot: erase the data flash on the next boot (all presets and settings are lost)");
                break;
            }
        }
    }

    // ---- whole-preset import ----------------------------------------------------------------

    /// <summary>
    /// Serialises JSON exactly like the factory data: one space after each colon, no other whitespace,
    /// keys in their given order, scalars written as they are.
    /// </summary>
    public static string ToFactoryJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        void Write(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(JsonSerializer.Serialize(kv.Key)).Append(": ");
                        Write(kv.Value);
                    }
                    sb.Append('}');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i]); }
                    sb.Append(']');
                    break;
                case null:
                    sb.Append("null");
                    break;
                default:
                    if (n.GetValueKind() == JsonValueKind.String)
                    {
                        string s = n.GetValue<string>();
                        if (s.Any(c => c < 0x20 || c > 0x7E || c is '"' or '\\'))
                            throw new ArgumentException($"String '{s}' contains characters the factory data never uses.");
                        sb.Append('"').Append(s).Append('"');
                    }
                    else sb.Append(n.ToJsonString());
                    break;
            }
        }
        Write(node);
        return sb.ToString();
    }

    /// <summary>
    /// Replaces a factory preset (or the EMPTY template) with a preset read from the amp.
    /// The incoming preset must have the same graph (nodes and connections) as the slot,
    /// every model must exist in the firmware, and every parameter must pass the same rules as
    /// <see cref="SetPresetParam"/>. The slot's own info (preset_id, flags, timestamps) is kept;
    /// only the display name is taken from the incoming preset. The result is written in the
    /// exact factory format and must fit the slot.
    /// </summary>
    public List<string> ImportPreset(FirmwareJsonEntry slot, JsonNode incoming, string source)
    {
        if (slot.Kind == FirmwareJsonKind.Model) throw new ArgumentException("Presets can only be imported into preset slots.");
        if (!slot.Editable) throw new InvalidOperationException($"{Describe(slot)} cannot be edited safely.");
        var notes = new List<string>();

        // Guard: the serialiser must reproduce this slot's current text byte for byte.
        var current = CurrentTree(slot);
        if (ToFactoryJson(current) != GetJson(slot))
            throw new NotSupportedException($"{Describe(slot)} is not in the expected factory format; refusing to rewrite it.");

        if (incoming is not JsonObject src || (string?)src["nodeType"] != "preset")
            throw new ArgumentException($"{source} is not a preset.");
        FenderPreset.FromNode(src);                         // structural parse

        foreach (var key in new[] { "nodeType", "nodeId", "version", "numInputs", "numOutputs" })
            if (!JsonNode.DeepEquals(src[key], current[key]))
                throw new ArgumentException($"{source}: '{key}' is {src[key]?.ToJsonString()}, the firmware uses {current[key]?.ToJsonString()}.");

        var srcGraph = src["audioGraph"]?.AsObject() ?? throw new ArgumentException($"{source} has no audioGraph.");
        if (!JsonNode.DeepEquals(Canonical(srcGraph["connections"]), Canonical(current["audioGraph"]?["connections"])))
            throw new ArgumentException($"{source}: its signal routing differs from the firmware preset's; only the standard chain is supported.");

        var srcNodes = srcGraph["nodes"]?.AsArray() ?? throw new ArgumentException($"{source} has no nodes.");
        var curIds = (current["audioGraph"]?["nodes"]?.AsArray() ?? []).Select(n => (string?)n?["nodeId"]).Order().ToList();
        var srcIds = srcNodes.Select(n => (string?)n?["nodeId"]).Order().ToList();
        if (!curIds.SequenceEqual(srcIds))
            throw new ArgumentException($"{source}: nodes are {string.Join(",", srcIds)}, expected {string.Join(",", curIds)}.");

        // Rebuild each node, validating and normalising every parameter.
        var nodes = new JsonArray();
        foreach (var n in srcNodes)
        {
            string nodeId = (string?)n?["nodeId"] ?? "";
            string fenderId = (string?)n?["FenderId"] ?? "";
            if (!Models.TryGetValue(fenderId, out var model))
                throw new ArgumentException($"{source} {nodeId}: model '{fenderId}' does not exist in this firmware.");

            var outNode = new JsonObject();
            foreach (var kv in n!.AsObject())
            {
                if (kv.Key != "dspUnitParameters") { outNode[kv.Key] = kv.Value?.DeepClone(); continue; }

                var outParams = new JsonObject();
                foreach (var p in kv.Value?.AsObject() ?? [])
                {
                    bool known = model.OriginalTree["defaultDspUnitParameters"]?[p.Key] != null || observed.ContainsKey((fenderId, p.Key));
                    if (!known) throw new ArgumentException($"{source} {nodeId}.{p.Key}: {fenderId} has no such parameter in this firmware.");

                    var rule = RuleFor(fenderId, p.Key, p.Value);
                    string text = p.Value?.GetValueKind() switch
                    {
                        JsonValueKind.String => p.Value.GetValue<string>(),
                        _ => p.Value?.ToJsonString() ?? "null",
                    };
                    var (_, node, note) = FormatValue(rule, text, $"{source} {nodeId}.{p.Key}");
                    if (note != null) notes.Add($"{nodeId}.{p.Key} {note}");
                    outParams[p.Key] = node;
                }
                outNode[kv.Key] = outParams;
            }
            nodes.Add(outNode);
        }

        // Keep the slot's info, except the display name.
        var info = current["info"]!.DeepClone().AsObject();
        string name = (string?)src["info"]?["displayName"] ?? throw new ArgumentException($"{source} has no displayName.");
        string formatted = FormatPresetName(name.TrimEnd());
        if (!PresetNamePattern.IsMatch(formatted))
            throw new ArgumentException($"{source}: name '{name}' has characters outside A-Z, 0-9 and space. Rename it on the amp first.");
        if (formatted != name) notes.Add($"name normalised to '{formatted}'");
        info["displayName"] = formatted;

        // Same top-level key order as the slot's current preset.
        var result = new JsonObject();
        foreach (var kv in current.AsObject())
        {
            result[kv.Key] = kv.Key switch
            {
                "info" => info,
                "audioGraph" => new JsonObject { ["connections"] = srcGraph["connections"]!.DeepClone(), ["nodes"] = nodes },
                _ => kv.Value?.DeepClone(),
            };
        }
        // Keep audioGraph's key order as in the slot.
        if (current["audioGraph"] is JsonObject cg && cg.First().Key == "nodes")
            result["audioGraph"] = new JsonObject { ["nodes"] = nodes.DeepClone(), ["connections"] = srcGraph["connections"]!.DeepClone() };

        byte[] text2 = Encoding.ASCII.GetBytes(ToFactoryJson(result));
        if (text2.Length > slot.Capacity)
        {
            var fits = Presets.Append(EmptyPreset).Where(p => p.Capacity >= text2.Length).Select(p => p.Kind == FirmwareJsonKind.EmptyPreset ? "empty" : p.Slot.ToString());
            throw new InvalidOperationException(
                $"{source} is {text2.Length} bytes in factory format, but {Describe(slot)} holds at most {slot.Capacity}. " +
                $"Slots large enough: {(fits.Any() ? string.Join(", ", fits) : "none")}.");
        }

        text2.CopyTo(image, slot.Offset);
        Array.Clear(image, slot.Offset + text2.Length, slot.Capacity + 1 - text2.Length);
        slot.Length = text2.Length;
        slot.Edits.Add(([], result.DeepClone()));
        Changes.Add($"{Describe(slot)}: replaced '{slot.Label}' with {source} '{formatted}' ({text2.Length}/{slot.Capacity} bytes)");
        return notes;
    }

    /// <summary>Connections as a sorted set, so order differences don't matter.</summary>
    private static JsonArray Canonical(JsonNode? connections) =>
        new((connections?.AsArray() ?? []).Select(c => c?.ToJsonString()).Order(StringComparer.Ordinal)
            .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    /// <summary>Problems found in the image as loaded (e.g. a preset name that would brick the amp).</summary>
    public IReadOnlyList<string> Problems =>
        Presets.Append(EmptyPreset)
            .Where(p => !PresetNamePattern.IsMatch(p.Label))
            .Select(p => $"{Describe(p)} has an invalid name '{p.Label}' ({p.Label.Length} characters); a restore from this image may brick the amp.")
            .Concat(Presets.Append(EmptyPreset).Where(p => ToFactoryJson(p.OriginalTree) != GetJson(p))
                .Select(p => $"{Describe(p)} is not in the factory JSON format."))
            .ToList();

    /// <summary>"stock", "applied" or "not found" for a boot patch in the current image.</summary>
    public string GetBootPatchState(BootPatch patch)
    {
        switch (patch)
        {
            case BootPatch.ForceRestore:
            {
                if (FindAll(ForceRestoreSignature).Count == 1) return "stock";
                byte[] patched = (byte[])ForceRestoreSignature.Clone();
                foreach (var (at, _, @new) in ForceRestoreEdits) @new.CopyTo(patched, at);
                return FindAll(patched).Count == 1 ? "applied" : "not found";
            }
            case BootPatch.ReformatFlash:
            {
                if (FindAll(FormattedMarker).Count == 1) return "stock";
                byte[] patched = (byte[])FormattedMarker.Clone();
                patched[^2] = (byte)'X';
                return FindAll(patched).Count == 1 ? "applied" : "not found";
            }
            default:
                return "not found";
        }
    }

    private static string Describe(FirmwareJsonEntry e) => e.Kind switch
    {
        FirmwareJsonKind.FactoryPreset => $"preset {e.Slot}",
        FirmwareJsonKind.EmptyPreset => "empty preset (slots 31-60)",
        _ => $"model {e.Label}",
    };

    // ---- build ----------------------------------------------------------------------------------

    /// <summary>
    /// Writes the CRC and re-validates the result: every changed byte must lie in an approved region,
    /// every edited JSON must equal the original with only the requested values changed, every preset
    /// name must still be valid, and the image must parse again from scratch.
    /// </summary>
    public byte[] Build()
    {
        byte[] result = (byte[])image.Clone();
        FenderFirmware.ValidateFirmware(result, out _, out uint crc);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(result.Length - 4), crc);

        if (result.Length != original.Length) throw new InvalidOperationException("Image size changed.");
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == original[i]) continue;
            if (!allowedRegions.Any(r => i >= r.Start && i < r.End))
                throw new InvalidOperationException($"Byte 0x{i:X} changed outside any approved region.");
        }

        foreach (var e in Presets.Append(EmptyPreset).Concat(Models.Values))
        {
            var expected = e.OriginalTree.DeepClone();
            foreach (var (path, value) in e.Edits)
            {
                if (path.Length == 0) expected = value!.DeepClone();     // whole-preset import
                else SetAtPath(expected, path, value?.DeepClone());
            }
            var actual = JsonNode.Parse(Encoding.ASCII.GetString(result, e.Offset, e.Length))!;
            if (!JsonNode.DeepEquals(expected, actual))
                throw new InvalidOperationException($"{Describe(e)}: edited JSON differs from the intended change.");
            if (Array.IndexOf(result, (byte)0, e.Offset) != e.Offset + e.Length)
                throw new InvalidOperationException($"{Describe(e)}: terminator is not where expected.");
        }

        var check = new FirmwarePatcher(result);            // re-discovers everything from scratch
        foreach (var p in check.Presets.Append(check.EmptyPreset))
        {
            if (!PresetNamePattern.IsMatch(p.Label))
                throw new InvalidOperationException($"{Describe(p)}: name '{p.Label}' is not 16 characters of A-Z, 0-9 and space.");
            FenderPreset.Parse(check.GetJson(p));
        }
        if (check.Version.Length != VersionLength) throw new InvalidOperationException("Version string length changed.");

        return result;
    }

    private static void SetAtPath(JsonNode root, object[] path, JsonNode? value)
    {
        JsonNode node = root;
        for (int i = 0; i < path.Length - 1; i++)
            node = path[i] is int idx ? node[idx]! : node[(string)path[i]]!;
        if (path[^1] is int last) node[last] = value; else node[(string)path[^1]] = value;
    }
}
