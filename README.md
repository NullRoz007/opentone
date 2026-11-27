# OpenTone

An open-source command-line tool for the Fender Mustang LT25, built on a reverse-engineered USB protocol and firmware. It is not affiliated with or endorsed by Fender.

## Status

Early and experimental. The USB protocol is documented from captures of the official Fender Tone app and from the protobuf schema in the firmware, and the firmware from disassembling version 2.1.4.

| Area | State |
|---|---|
| Read presets, nodes, parameters and the firmware version | Implemented |
| Change parameters, models and the active preset | Implemented |
| Live session with a terminal UI and a WebSocket server | Implemented |
| Play the amp's USB audio | Implemented |
| Extract a firmware image from a capture of an official update | Working |
| Inspect and patch firmware images (presets, splash logo, version, boot patches) | Working |
| Build a custom firmware from a folder of presets and a logo | Working |
| Flash firmware (update mode) | Working, see the warning below |
| Your own C code in the firmware (hooks): USB commands, boot and timer hooks, drawing, data-flash assets | Working, tested on an LT25 |

## Requirements

- .NET SDK
- A Mustang LT25
- USB access through libusb (LibUsbDotNet). On Windows the amp must be bound to a libusb-compatible driver such as WinUSB. On Linux you may need a udev rule.
- For hooks only: the Arm GNU toolchain (`arm-none-eabi-gcc`)

## Build

```
dotnet build
dotnet run -- --help
```

## Quick start

```
opentone list                                        # attached amps and audio devices
opentone get preset 1 -d mustang-lt25 > preset1.json
opentone set preset 3 -d mustang-lt25
opentone connect -d mustang-lt25                     # live session
opentone patch lt25-2.1.4.bin --info                 # inspect a firmware image
```

## Warning

Flashing firmware writes to the amp's internal flash. An interrupted update may leave the amp unusable until you flash it again, and modified images are at your own risk. Read [Flashing firmware](docs/firmware.md#flashing-firmware) first. This software comes with no warranty.

## Documentation

| Document | Contents |
|---|---|
| [Commands](docs/commands.md) | Every command, the `get`/`set` types and the live session |
| [Firmware](docs/firmware.md) | Dumping, patching, building a custom firmware, flashing and recovery |
| [Hooks](docs/hooks.md) | Your own C code in the firmware: hook kinds, the firmware API, the screen and data-flash assets |
| [USB protocol](docs/mustang-lt25-usb-protocol.md) | Device modes, report framing, the message schema, the update protocol and the image format |
| [Firmware map](docs/mustang-lt25-firmware-map.md) | Image layout, data flash, boot, the main loop, USB handling, the display, hooks and the patching rules |
| [Models and parameters](docs/mustang-lt25-models.md) | The effect and amp models and parameters found in the presets |
| [WebSocket messages](docs/websocket-messages.md) | The live session's WebSocket API |
