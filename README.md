# OpenTone

An open-source command-line tool for the Fender Mustang LT25, built on a reverse-engineered USB protocol. It is not affiliated with or endorsed by Fender.

## Status

Early and experimental. The protocol is documented from USB captures of the official Fender Tone app, so only behaviour seen in those captures is documented.

| Area | State |
|---|---|
| Read presets, nodes, parameters and the firmware version | Implemented |
| Change parameters, models and the active preset | Implemented |
| Live session with a terminal UI and a WebSocket server | Implemented |
| Extract a firmware image from a capture of an official update | Working |
| Flash firmware (update mode) | Working, see the warning below |

## Requirements

- .NET SDK
- A Mustang LT25
- USB access through libusb (LibUsbDotNet). On Windows the amp must be bound to a libusb-compatible driver such as WinUSB. On Linux you may need a udev rule.

## Build

```
dotnet build
dotnet run -- --help
```

## Commands

Every command except `list` needs `-d, --device <device>`. Run `opentone list` to see the attached Fender devices and the names to pass. An amp in update mode appears as a separate device from a normal one.

| Command | Description |
|---|---|
| `list` | List attached Fender devices |
| `connect` | Open a live session (see below) |
| `firmware` | Print the firmware version |
| `get <type> <arg1> [arg2]` | Read `param <node> <param>`, `preset <slot>` or `node <node>` |
| `set <type> <arg1> [arg2] [arg3]` | Set `param <node> <param> <value>`, `preset <slot>` or `model <node> <model>` |
| `dump <input> <output>` | Extract the firmware from a capture and save it |
| `patch <input> [output] [options]` | Inspect a firmware image or write a patched copy (see below) |
| `flash <path>` | Flash a firmware file (update mode only). `--yes` skips the prompt |

Nodes are `stomp`, `mod`, `amp`, `delay` and `reverb`.

```
opentone list
opentone get preset 1 -d <device>
opentone get param amp gain -d <device>
opentone set preset 3 -d <device>
```

### Live session

```
opentone connect -d <device>
```

Connects to the amp, starts the keepalive, starts a WebSocket server on port 8080, syncs with the amp and opens the terminal UI. Every command except `dump` and `flash` can be run live inside the session.

The messages used by the WebSocket server are documented in [WebSocket messages](docs/websocket-messages.md).

### Dumping firmware from a capture

Capture an official Fender Tone update with Wireshark and USBPcap (Windows). Select the USB root hub the amp is on, enable capture from newly connected devices, and start the capture before you put the amp in update mode. Then:

```
opentone dump update-capture.pcapng firmware.bin
```

This checks the CRC and saves the image. No amp needs to be connected.

### Patching firmware

`patch` inspects and edits an image (a `.bin`, or a capture directly). Every edit is checked against the rules the firmware relies on, and the result is re-validated before it is written. See [firmware map](docs/mustang-lt25-firmware-map.md) for the rules.

| Option | Effect |
|---|---|
| `--info` | Everything readable from the image: CRC, SHA-256 and whether it is a known official build, version and product strings, memory layout (from the reset handler), USB descriptor and handled request types, boot-patch state, presets, model menu, images (factory or modified) and build details |
| `--list` | Factory presets (slots 1-30 and the `empty` template for 31-60), version, models and images |
| `--show <slot\|empty\|model>` | Parameters with their current values and allowed values |
| `--rename SLOT=NAME` | Rename a preset. Names are 16 characters of A-Z, 0-9 and space; shorter names are padded. `LINE1\|LINE2` pads each half to 8 |
| `--set SLOT:NODE.PARAM=VALUE` | Change a preset parameter, keeping its type and the model's range |
| `--model-default MODEL.PARAM=VALUE` | Change the default value a model gets when selected |
| `--pull AMPSLOT[=FWSLOT]` | Read a preset from the amp (1-60 or `current`, normal mode) and write it into a firmware slot |
| `--import FWSLOT=FILE` | The same, from a preset JSON file |
| `--export-image NAME=FILE` / `--image NAME=FILE` | Save or replace a bitmap (`splash`: the 128x43 startup logo, `tone`: 66x58) as 24-bit BMP. Same size only |
| `--fw-version X.Y.Z` | Change the version string (exactly 5 characters) |
| `--force-restore` | Boot patch: restore all presets from the factory set on every boot. Flash stock firmware afterwards |
| `--reformat-flash` | Boot patch: erase the data flash (presets and settings) on the next boot |
| `--dry-run` | Validate without writing |

```
opentone patch lt25-2.1.4.bin custom.bin --rename "2=MY|LEAD" --set 2:amp.gain=0.8 --image splash=logo.bmp
```

Factory presets only reach the amp's preset memory through a factory restore (Settings > Factory Restore, or `--force-restore`). A preset that would grow past its slot's space in the image is refused.

### Flashing firmware

Put the amp in update mode, close Fender Tone, then:

```
opentone flash firmware.bin -d <device>
```

The command validates the image, shows the device details and asks for confirmation before it writes anything.

## Warning: flashing firmware

Flashing writes to the amp's internal flash and cannot be undone. An interrupted update may leave the amp unusable.

- Do not unplug the cable, power off the amp or let the computer sleep until it finishes. A full update takes about 3 minutes, and the progress bar may sit at 0% for up to 15 seconds at the start.
- Close Fender Tone and any other program that uses the amp.
- The only integrity check verified is the CRC at the end of the image. Whether the amp checks anything else is not known, so modified images are at your own risk.
- If something goes wrong, run the official Fender Tone update to restore a complete image.

This software comes with no warranty.

## Documentation

- [USB protocol](docs/mustang-lt25-usb-protocol.md): device modes, report framing, commands, update protocol and firmware image format.
- [Node models and parameters](docs/mustang-lt25-nodes.md): the effect and amp models and parameters found in the 60 presets.