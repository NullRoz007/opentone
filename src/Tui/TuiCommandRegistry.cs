using OpenTone;
using Spectre.Console;

public sealed class TuiCommandRegistry
{
    private readonly Dictionary<string, Func<FenderAmp, string[], Task<string>>> _handlers = new()
    {
        ["firmware"]  = (amp, _) =>  Task.FromResult(AmpActions.GetFirmware(amp)),
        ["get"] = (amp, a) =>
        {
            switch(a[0])
            {
                case "param":
                    return Task.FromResult(AmpActions.GetParam(amp, a[1], a[2]));
                case "preset":
                    return Task.FromResult(AmpActions.GetPreset(amp, int.Parse(a[1])));
                case "node":
                    return Task.FromResult(AmpActions.ViewNode(amp, a[1]));
                default:
                    throw new Exception("Unknown type: "+a[0]);
            }
        },
        ["set"] = (amp, a) =>
        {
            switch(a[0])
            {
                case "param":
                    return Task.FromResult(AmpActions.SetParamAuto(amp, a[1], a[2], a[3]));
                case "preset":
                    return Task.FromResult(AmpActions.SetPreset(amp, int.Parse(a[1])));
                case "model":
                    return Task.FromResult(AmpActions.SetModel(amp, a[1], a[2]));
                default:
                    throw new Exception("Unknown type: "+a[0]);
            }
        },
        ["listen"] = async (amp, a) =>
        {
            AudioCapture c = new(
                a[0],
                a[1] ?? ""
            );

            return await AudioActions.Listen(c);
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

    public async Task<string> Execute(FenderAmp amp, string name, string[] args)
    {
        if (!_handlers.TryGetValue(name, out var run)) return "unknown command";

        try
        {
            var (cmdArgs, pipe) = ParsePipe(args);
            string output = await run(amp, cmdArgs);

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