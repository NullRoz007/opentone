# OpenTone

An open-source command-line tool for the Fender Mustang LT25, built on a reverse-engineered USB protocol and firmware. It is not affiliated with or endorsed by Fender.

## Status

Early and experimental. The USB protocol is documented from captures of the official Fender Tone app, and the firmware from disassembling version 2.1.4, so only behaviour seen there is documented.

| Area | State |
|---|---|
| Read presets, nodes, parameters and the firmware version | Implemented |
| Change parameters, models and the active preset | Implemented |
| Live session with a terminal UI and a WebSocket server | Implemented |
| Extract a firmware image from a capture of an official update | Working |
| Inspect and patch firmware images (presets, splash logo, version, boot patches) | Working |
| Build a custom firmware from a folder of presets and a logo | Working |
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

Commands that talk to the amp take `-d, --device <device>`. Run `opentone list` to see the attached amps and their names: `mustang-lt25` in normal mode, `lt-dfu` in update mode.

| Command | Description |
|---|---|
| `list` | List attached Fender devices and their mode |
| `connect` | Open a live session (see below) |
| `listen <playbackid> [captureid]` | Play the amp's USB audio through a playback device. IDs come from `opentone list`. Without a capture ID, the amp given with `-d` is found by its USB VID, PID and serial (Windows/WASAPI only for now) |
| `firmware` | Show the firmware version |
| `get <type> <arg1> [arg2]` | Read `param <node> <param>`, `preset <slot>` or `node <node>`. Presets and nodes print as plain JSON |
| `set <type> <arg1> [arg2] [arg3]` | Set `param <node> <param> <value>`, `preset <slot>` or `model <node> <model>` |
| `call <fn> [r0..r3]` | Call a firmware function. Needs a firmware with the debug hook (see the firmware map); `get mem <addr> [len]` and `set mem <addr> <hex>` read and write memory the same way |
| `dump <capture> <output>` | Extract the firmware from a capture of an official update. No amp needed |
| `patch <input> [output] [options]` | Inspect a firmware image or write a patched copy (see below). No amp needed except for `--pull` |
| `flash <path>` | Flash a firmware file (update mode only). `--yes` skips the prompt |

Nodes are `stomp`, `mod`, `amp`, `delay` and `reverb`.

```
opentone list
opentone get preset 1 -d mustang-lt25 > preset1.json
opentone get param amp gain -d mustang-lt25
opentone set preset 3 -d mustang-lt25
opentone listen <playbackid> -d mustang-lt25
```

### Live session

```
opentone connect -d mustang-lt25
```

Connects to the amp, starts the keepalive, starts a WebSocket server on port 8080, syncs with the amp and opens the terminal UI. The amp commands (`firmware`, `get`, `set`) can be run live inside the session.

The messages used by the WebSocket server are documented in [WebSocket messages](docs/websocket-messages.md).

## Firmware

### Dumping firmware from a capture

Capture an official Fender Tone update with Wireshark and USBPcap (Windows). Select the USB root hub the amp is on, enable capture from newly connected devices, and start the capture before you put the amp in update mode. Then:

```
opentone dump update-capture.pcapng lt25-2.1.4.bin
```

This checks the CRC and saves the image.

### Inspecting and patching

`patch` reads a `.bin` (or a capture directly). Without an output path it only inspects. Every edit is checked against the rules the firmware relies on, and the whole image is re-validated before it is written. See the [firmware map](docs/mustang-lt25-firmware-map.md) for the rules.

| Option | Effect |
|---|---|
| `--info` | Everything readable from the image: checksums and whether it is a known official build, version, memory layout, USB details, boot patches, presets, models, images and build details |
| `--list` | Factory presets (slots 1-30, and the `empty` template for 31-60), version, models and images |
| `--show <slot\|empty\|model>` | A preset's or model's parameters, with current and allowed values |
| `--rename SLOT=NAME` | Rename a preset. Names are 16 characters of A-Z, 0-9 and space; shorter names are padded. `LINE1\|LINE2` pads each half of the two-line display to 8 |
| `--set SLOT:NODE.PARAM=VALUE` | Change a preset parameter, keeping its type and allowed range |
| `--model-default MODEL.PARAM=VALUE` | Change the value a model starts with when selected |
| `--pull AMPSLOT[=FWSLOT]` | Read a preset from the amp (1-60 or `current`) and write it into a firmware slot |
| `--import FWSLOT=FILE` | Write a preset JSON file (for example from `get preset`) into a firmware slot |
| `--export-image NAME=FILE` | Save a bitmap as 24-bit BMP: `splash` (the 128x43 startup logo) or `tone` (66x58) |
| `--image NAME=FILE` | Replace a bitmap with a 24- or 32-bit BMP of the same size |
| `--fw-version X.Y.Z` | Change the version string (exactly 5 characters) |
| `--force-restore` | Boot patch: restore all presets from the factory set on every boot |
| `--reformat-flash` | Boot patch: erase the amp's preset and settings memory on the next boot |
| `--hook FIELD=FILE` | Compile a C hook and install it on a USB message field the firmware doesn't handle (see below). Repeatable |
| `--dry-run` | Validate without writing |

```
opentone patch lt25-2.1.4.bin --info
opentone patch lt25-2.1.4.bin custom.bin --rename "2=MY|LEAD" --set 2:amp.gain=0.8 --image splash=logo.bmp
```

The firmware holds factory presets only for slots 1-30; slots 31-60 all come from the one `empty` template. Each slot has a fixed amount of space, so a preset that would grow past it is refused.

### Hooks: your own code in the firmware

`--hook FIELD=FILE` compiles a C file and installs it as the handler for a USB message field. The field must be one the firmware decodes but doesn't handle; `patch --info` lists them under "Hook slots". All hooks are linked into one block placed after the stock image, and the dispatcher's "unsupported message" case branches to it. The firmware addresses the code needs are found in the image itself, so an unrecognised build is refused rather than mis-patched.

Needs the Arm GNU toolchain (`arm-none-eabi-gcc`) on PATH, for example `scoop bucket add extras` then `scoop install gcc-arm-none-eabi`.

```
opentone patch lt25-2.1.4.bin debug.bin --hook 12=hooks/debug.c
opentone flash debug.bin -d lt-dfu
opentone get mem 0x08020200 16 -d mustang-lt25          # read memory
opentone call 0x80ecd6c 0x0801ee26 -d mustang-lt25     # call a firmware function
```

| File | Purpose |
|---|---|
| `hooks/opentone.h` | Hook API: the message layout, `OT_HOOK(mgr, request)`, `ot_reply_new` and `ot_send`, and the rules hooks must follow |
| `hooks/debug.c` | PEEK, POKE and CALL over field 12. Used by `get mem`, `set mem` and `call` |
| `hooks/runtime/` | The entry stub, shared helpers and linker script |

Hooks can't use writable globals, because nothing initialises RAM for them, and the link fails if they do. They run on the amp's USB message task, so keep them short. Reading, writing or calling a bad address hangs the amp until you power-cycle it.

### Building a custom firmware

`Build-OpenTone-Firmware.ps1` (Windows) and `build-opentone-firmware.sh` (Linux, bash 4+) build an image in one step:

- Every preset in `presets/lt25/` is imported into the slot named by its file name: `05 DREAMY.json` goes to slot 5.
  - Slots 31-60 share one template in the firmware, so files for those slots must be identical, and one of them sets the template.
  - A file starting with `empty` sets the template directly.
- The splash logo is replaced with `assets/splash-red.bmp`, which must be 128x43. The other `assets/splash-*.bmp` files are alternatives.
- The debug hook (`hooks/debug.c`) is installed on field 12, which needs `arm-none-eabi-gcc` (see [Hooks](#hooks-your-own-code-in-the-firmware)). `-NoHooks` / `--no-hooks` leaves it out, and `-Hook` / `--hook FIELD=FILE` installs other hooks instead.
- Everything is applied in one `patch` run, so the build either passes every check or writes nothing.

```
./Build-OpenTone-Firmware.ps1                                   # PowerShell
./build-opentone-firmware.sh                                    # bash
./Build-OpenTone-Firmware.ps1 -Presets ./presets/custom -NoHooks
```

The output goes to `fw/lt25-2.1.4-opentone.bin`. Both scripts also take the base firmware, output, preset folder and splash as options, plus a dry run (`-DryRun` / `--dry-run`).

There are two preset folders:

| Folder | Contents |
|---|---|
| `presets/lt25/` | The factory presets as read from an LT25, slots 01-30 |
| `presets/custom/` | 30 presets built from the firmware's own factory presets, so each one uses only models and parameters the firmware ships |

`presets/custom/make_custom_presets.py` generates the custom set from the design list in the script and fits each preset into a slot with enough space:

```
python presets/custom/make_custom_presets.py fw/lt25-2.1.4.bin presets/custom
```

### Flashing firmware

Put the amp in update mode, close Fender Tone, then:

```
opentone flash firmware.bin -d lt-dfu
```

The command validates the image, shows the image and device details, and asks for confirmation before it writes anything.

Flashing replaces the firmware but not the presets stored on the amp. To load the presets from a new image, run Settings > Factory Restore on the amp. This replaces all 60 slots.

## Warning: flashing firmware

Flashing writes to the amp's internal flash and cannot be undone. An interrupted update may leave the amp unusable.

- Do not unplug the cable, power off the amp or let the computer sleep until it finishes. A full update takes about 3 minutes, and the progress bar may sit at 0% for up to 15 seconds at the start.
- Close Fender Tone and any other program that uses the amp.
- The only integrity check verified is the CRC at the end of the image. Whether the amp checks anything else is not known, so modified images are at your own risk.
- If something goes wrong, run the official Fender Tone update to restore a complete image.

This software comes with no warranty.

### Recovering from a bad preset

A broken preset in the amp's memory can stop it at start-up (the display lights but buttons and USB don't respond). Update mode still works, so:

1. Flash an image with `--force-restore` applied: `opentone patch lt25-2.1.4.bin restore.bin --force-restore`.
2. Power the amp on. It restores all 60 presets from the image's factory set.
3. Flash a normal image again, otherwise every start-up restores the presets.

If the amp still stops before the restore, use `--reformat-flash` instead, which erases the preset memory on the next start-up.

## Documentation

- [USB protocol](docs/mustang-lt25-usb-protocol.md): device modes, report framing, commands, update protocol and firmware image format.
- [Firmware map](docs/mustang-lt25-firmware-map.md): image layout, preset storage, boot behaviour, USB request handling, images and the patching rules.
- [Models and parameters](docs/mustang-lt25-models.md): the effect and amp models and parameters found in the presets.
- [WebSocket messages](docs/websocket-messages.md): the live session's WebSocket API.