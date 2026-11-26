using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class ConnectSettings : CommandSettings, IDeviceSettings
    {
        [CommandOption("-d|--device")]
        [Description("Target device")]
        public required string Device { get; init; }
    }

    public class ConnectCommand : AmpCommand<ConnectSettings>
    {
        protected override void Run(FenderAmp amp, ConnectSettings s)
        {
            RunAsync(amp, s).GetAwaiter().GetResult();
        }
        public async Task<int> RunAsync(FenderAmp amp, ConnectSettings s)
        {
            string? hello = amp.Hello();
            if (hello != null)
            {
                amp.StartHeartbeat();
                var _ = new WebSocketServer(amp).Run(8080);
                amp.Sync();
                await new TuiSession(amp).Run();
                
                AnsiConsole.Cursor.Show();
                AnsiConsole.Clear();
            }
            
            return 0;
        }
    }
