using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;

public interface IDeviceSettings
    {
        string Device { get; }
    }

    public abstract class AmpCommand<TSettings> : Command<TSettings>
        where TSettings : CommandSettings, IDeviceSettings
    {
        public override int Execute(
            CommandContext context,
            TSettings settings,
            CancellationToken cancellation)
        {
            using var amp = FenderAmp.Connect(settings.Device);
            if(amp.GetDeviceName() != FenderDevices.LT_UPDATER_DEVICE_NAME) {
                if (amp.Hello() == null)
                {
                    Ui.Message("Error", "No reply to hello. Close Fender Tone and check the amp is connected and in normal mode.", Ui.Level.Error);
                    return 1;
                }
            }

            Run(amp, settings);
            return 0;
        }

        protected abstract void Run(FenderAmp amp, TSettings settings);
    }