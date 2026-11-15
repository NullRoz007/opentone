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
}