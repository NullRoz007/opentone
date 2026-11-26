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
                case "mem":
                    return Task.FromResult(AmpActions.Peek(amp, a[1], a.Length > 2 ? a[2] : "16"));
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
                case "mem":
                    return Task.FromResult(AmpActions.Poke(amp, a[1], string.Concat(a[2..])));
                default:
                    throw new Exception("Unknown type: "+a[0]);
            }
        },
        ["call"] = (amp, a) => Task.FromResult(AmpActions.Call(amp, a[0], a[1..])),
        ["listen"] = async (amp, a) =>
        {
            if (a.Length < 1) return "usage: listen <playbackid> [captureid]";
            object captureId = a.Length > 1 ? a[1] : ListenCommand.FindCaptureDevice(amp);
            using AudioCapture c = new(captureId, a[0]);

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