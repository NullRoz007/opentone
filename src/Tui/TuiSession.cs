using OpenTone;

public sealed class TuiSession(FenderAmp amp)
{
    private readonly TuiView _view = new();
    private readonly TuiCommandRegistry _commands = new();
    private string _output = "...";

    public void Run()
    {
        _view.Draw(amp, _output);

        while (true)
        {
            _view.PositionPrompt();
            var line = Console.ReadLine();
            Console.CursorVisible = false;

            if (line is null or "quit") break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            _output = parts[0] == "help"
                ? _commands.Help
                : _commands.Execute(amp, parts[0], parts[1..]);

            _view.Draw(amp, _output);
        }
    }
}