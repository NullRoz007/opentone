using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class ConnectSettings : CommandSettings
    {
        [CommandArgument(0, "<device>")]
        [Description("Device name (see 'tone list')")]
        public string Device { get; init; } = FenderDevices.LT25;
    }

    public class ConnectCommand : Command<ConnectSettings>
    {
        public override int Execute(
            CommandContext context,
            ConnectSettings settings,
            CancellationToken cancellationToken)
        {
            using var amp = FenderAmp.Connect(settings.Device);

            string? hello = amp.Hello();

            if (hello != null)
            {
                amp.StartHeartbeat();
                var _ = new WebSocketServer(amp).Run(8080);

                string? sync = amp.Sync();
                new TuiSession(amp).Run();
                
                AnsiConsole.Cursor.Show();
                AnsiConsole.Clear();

                
            }
            
            return 0;
        }
    }
