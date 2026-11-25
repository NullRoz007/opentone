using OpenTone;

public sealed class TuiCommandRegistry
{
    private readonly Dictionary<string, Func<FenderAmp, string[], string>> _handlers = new()
    {
        ["firmware"]  = (amp, _) => AmpActions.GetFirmware(amp),
        ["get"] = (amp, a) =>
        {
            switch(a[0])
            {
                case "param":
                    return AmpActions.GetParam(amp, a[1], a[2]);
                case "preset":
                    return AmpActions.GetPreset(amp, int.Parse(a[1]));
                case "node":
                    return AmpActions.ViewNode(amp, a[1]);
                default:
                    throw new Exception("Unknown type: "+a[0]);
            }
        },
        ["set"] = (amp, a) =>
        {
            switch(a[0])
            {
                case "param":
                    return AmpActions.SetParamAuto(amp, a[1], a[2], a[3]);
                case "preset":
                    return AmpActions.SetPreset(amp, int.Parse(a[1]));
                case "model":
                    return AmpActions.SetModel(amp, a[1], a[2]);
                default:
                    throw new Exception("Unknown type: "+a[0]);
            }
        }
    };

    public string Help => string.Join(", ", _handlers.Keys.Append("quit"));

    private static (string[] Args, string? Path) ParsePipe(string[] args)
    {
        int idx = Array.IndexOf(args, ">");
        if (idx == -1) return (args, null);

        if (idx + 1 >= args.Length)
            throw new FormatException("missing file name after '>'");

        string path = string.Join(' ', args[(idx + 1)..]);
        return (args[..idx], path);
    }

    private static string WriteToFile(string path, string output, bool append)
    {
        if (append) File.AppendAllText(path, output + Environment.NewLine);
        else        File.WriteAllText(path, output + Environment.NewLine);

        return $"{(append ? "appended" : "wrote")} {output.Length} chars to {path}";
    }

    public string Execute(FenderAmp amp, string name, string[] args)
    {
        if (!_handlers.TryGetValue(name, out var run)) return "unknown command";

        try
        {
            var (cmdArgs, pipe) = ParsePipe(args);
            string output = run(amp, cmdArgs);

            if (pipe is null) return output;

            try { return WriteToFile(pipe, output, false); }
            catch (Exception e) when (
                e is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return "file error: " + e.Message;
            }
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { return "bad arguments: " + e.Message; }
        catch (Exception e) when (e is IOException or TimeoutException)
        { return "amp error: " + e.Message; }
    }
}