using System.Text.Json.Nodes;
using OpenTone;

static class AmpActions
{
    public static string GetFirmware(FenderAmp amp) =>
        amp.GetFirmwareVersion()?.ToString() ?? "Unknown";

    public static string GetPreset(FenderAmp amp, int slot) =>
        amp.ReadPreset(slot)?.ToString() ?? "Unknown";

    public static string SetPreset(FenderAmp amp, int slot) =>
        amp.SetPreset(slot)?.ToJson().ToString() ?? "Unknown";

    public static string ViewNode(FenderAmp amp, string nodeId)
    {
        var rootNode = amp.GetCurrentPreset();
        if(rootNode == null) return "No root node";
        FenderPreset current = FenderPreset.FromNode(rootNode);
        DspNode? node = current.FindNode(nodeId ?? "");
        return node?.ToJson().ToString() ?? "Unknown: "+nodeId; //print the node info
    }

    public static string SetParamAuto(FenderAmp amp, string nodeId, string paramId, string text)
    {
        // Bool words are unambiguous; "0"/"1" count only if the preset says the parameter is a bool.
        bool? boolWord = text.ToLowerInvariant() switch
        {
            "true" or "on"   => true,
            "false" or "off" => false,
            _ => null
        };
        bool? boolDigit = text switch { "1" => true, "0" => false, _ => null };

        // The amp's own preset is the authority on the parameter's type.
        JsonValue? current = amp.GetParam(nodeId, paramId) as JsonValue;
        bool isBool   = current?.TryGetValue<bool>(out _)   ?? false;
        bool isNumber = current?.TryGetValue<double>(out _) ?? false;
        bool isString = current?.TryGetValue<string>(out _) ?? false;
        bool known    = isBool || isNumber || isString;

        FenderProtocol.ParamChange? change;

        if (isBool || (!known && boolWord != null))
        {
            bool? value = boolWord ?? boolDigit;
            if (value == null) throw new FormatException($"not a bool: {text}");
            change = amp.SetBool(nodeId, paramId, value.Value);
        }
        else if (isNumber || (!known && float.TryParse(text, out _)))
        {
            change = amp.SetFloat(nodeId, paramId, float.Parse(text));
        }
        else
        {
            throw new NotSupportedException($"No wire encoding for value: " + text);
        }

        return ViewNode(amp, nodeId);
    }

    public static string GetParam(FenderAmp amp, string nodeId, string paramId)
    {
        return amp.GetParam(nodeId, paramId)?.ToJsonString() ?? "Unknown";
    }

    public static string SetModel(FenderAmp amp, string nodeId, string fenderId)
    {
        FenderProtocol.ModelChange? result;
        result = amp.SetModel(nodeId, fenderId);
        return ViewNode(amp, nodeId);
    }

    // ---- Debug hook (needs a firmware built with it) ----

    /// <summary>Parses "0x..." as hex, anything else as decimal.</summary>
    public static uint ParseU32(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(text[2..], 16)
            : uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Hex dump of <paramref name="length"/> bytes at <paramref name="address"/>, 16 per line.</summary>
    public static string Peek(FenderAmp amp, string address, string length)
    {
        uint start = ParseU32(address);
        byte[] data = amp.Peek(start, (int)ParseU32(length));
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            var line = data.AsSpan(i, Math.Min(16, data.Length - i));
            string hex = string.Join(' ', line.ToArray().Select(b => b.ToString("x2")));
            string ascii = new(line.ToArray().Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.').ToArray());
            sb.AppendLine($"{start + (uint)i:x8}  {hex,-47}  {ascii}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Writes hex bytes (e.g. "deadbeef" or "de ad be ef") at the address.</summary>
    public static string Poke(FenderAmp amp, string address, string hex)
    {
        byte[] data = Convert.FromHexString(hex.Replace(" ", "").Replace("0x", ""));
        amp.Poke(ParseU32(address), data);
        return $"wrote {data.Length} bytes at 0x{ParseU32(address):x8}";
    }

    /// <summary>The firmware objects hooks work on (needs a debug hook built with CONTEXT).</summary>
    /// <summary>Stores a file as a data-flash asset (slots 120-399) and reads it back to check it.</summary>
    public static string WriteAsset(FenderAmp amp, string slot, string file)
    {
        byte[] data = File.ReadAllBytes(file);
        int first = int.Parse(slot), slots = FenderAmp.AssetSlots(data.Length);
        amp.WriteAsset(first, data);
        bool same = amp.ReadAsset(first).AsSpan().SequenceEqual(data);
        return $"{Path.GetFileName(file)}: {data.Length} bytes in slots {first}-{first + slots - 1}, " +
               (same ? "read back and verified" : "READ-BACK MISMATCH");
    }

    /// <summary>Reads a data-flash asset into a file.</summary>
    public static string ReadAsset(FenderAmp amp, string slot, string file)
    {
        byte[] data = amp.ReadAsset(int.Parse(slot));
        File.WriteAllBytes(file, data);
        return $"{data.Length} bytes written to {file}";
    }

    public static string Context(FenderAmp amp)
    {
        var (mgr, app) = amp.Context();
        return $"manager = 0x{mgr:x8}\napp     = 0x{app:x8}";
    }

    /// <summary>Calls a firmware function (Thumb bit added automatically) with up to 4 arguments.</summary>
    public static string Call(FenderAmp amp, string function, IEnumerable<string> args)
    {
        var others = new List<(int Field, int Bytes)>();
        var (r0, r1) = amp.Call(ParseU32(function), args.Select(ParseU32).ToArray(), others);
        string sent = others.Count == 0 ? "" :
            "\nalso received: " + string.Join(", ", others.Select(o => $"field {o.Field} ({o.Bytes} bytes)"));
        return $"r0 = 0x{r0:x8} ({r0})\nr1 = 0x{r1:x8} ({r1}){sent}";
    }
}