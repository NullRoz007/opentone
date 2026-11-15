using System.Reflection.Metadata;
using OpenTone;
using Spectre.Console.Cli.Help;

public sealed class TuiCommandRegistry
{
    private readonly Dictionary<string, Func<FenderAmp, string[], string>> _handlers = new()
    {
        ["firmware"]  = (amp, _) => AmpActions.GetFirmware(amp),
        ["getpreset"] = (amp, a) => AmpActions.GetPreset(amp, int.Parse(a[0])),
        ["setpreset"] = (amp, a) => AmpActions.SetPreset(amp, int.Parse(a[0])),
        ["viewnode"]  = (amp, a) => AmpActions.ViewNode(amp, a[0]),
        ["setparam"] = (amp, a) => AmpActions.SetParamAuto(amp, a[0], a[1], a[2]),
        ["getparam"] = (amp, a) => AmpActions.GetParam(amp, a[0], a[1]),
        ["setmodel"] = (amp, a) => AmpActions.SetModel(amp, a[0], a[1]),
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