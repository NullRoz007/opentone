# Firmware: dumping, patching, building and flashing

The rules behind every edit are in the [firmware map](mustang-lt25-firmware-map.md).

## Dumping firmware from a capture

Capture an official Fender Tone update with Wireshark and USBPcap (Windows). Select the USB root hub the amp is on, enable capture from newly connected devices, and start the capture before you put the amp in update mode. Then:

```
opentone dump update-capture.pcapng lt25-2.1.4.bin
```

This checks the CRC and saves the image.

## Inspecting and patching

`patch` reads a `.bin` (or a capture directly). Without an output path it only inspects. Every edit is checked against the rules the firmware relies on, and the whole image is re-validated before it is written.

| Option | Effect |
|---|---|
| `--info` | Everything readable from the image: checksums and whether it is a known official build, version, memory layout, USB details and protobuf schema (with the free hook slots), boot patches, presets, models, images and build details |
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
| `--hook FIELD=FILE`, `--hook boot=FILE`, `--hook tick=FILE` | Compile a C hook and install it ([hooks](hooks.md)). Repeatable |
| `--dry-run` | Validate without writing |

```
opentone patch lt25-2.1.4.bin --info
opentone patch lt25-2.1.4.bin custom.bin --rename "2=MY|LEAD" --set 2:amp.gain=0.8 --image splash=logo.bmp
```

The firmware holds factory presets only for slots 1-30; slots 31-60 all come from the one `empty` template. Each slot has a fixed amount of space, so a preset that would grow past it is refused.

## Building a custom firmware

`Build-OpenTone-Firmware.ps1` (Windows) and `build-opentone-firmware.sh` (Linux, bash 4+) build an image in one step:

- Every preset in `presets/lt25/` is imported into the slot named by its file name: `05 DREAMY.json` goes to slot 5.
  - Slots 31-60 share one template in the firmware, so files for those slots must be identical, and one of them sets the template.
  - A file starting with `empty` sets the template directly.
- The splash logo is replaced with `assets/splash-red.bmp`, which must be 128x43. The other `assets/splash-*.bmp` files are alternatives.
- The debug hook (`hooks/debug.c`) is installed on field 12, which needs `arm-none-eabi-gcc` ([hooks](hooks.md)). `-NoHooks` / `--no-hooks` leaves it out, and `-Hook` / `--hook FIELD=FILE` installs other hooks instead.
- Everything is applied in one `patch` run, so the build either passes every check or writes nothing.

```
./Build-OpenTone-Firmware.ps1                                   # PowerShell
./build-opentone-firmware.sh                                    # bash
./Build-OpenTone-Firmware.ps1 -Presets ./presets/custom -NoHooks
```

The output goes to `fw/lt25-2.1.4-opentone.bin`. Both scripts also take the base firmware, output, preset folder and splash as options, plus a dry run (`-DryRun` / `--dry-run`).

| Preset folder | Contents |
|---|---|
| `presets/lt25/` | The factory presets as read from an LT25, slots 01-30 |
| `presets/custom/` | 30 presets built from the firmware's own factory presets, so each one uses only models and parameters the firmware ships |

`presets/custom/make_custom_presets.py` generates the custom set from the design list in the script and fits each preset into a slot with enough space:

```
python presets/custom/make_custom_presets.py fw/lt25-2.1.4.bin presets/custom
```

## Flashing firmware

Put the amp in update mode, close Fender Tone, then:

```
opentone flash firmware.bin -d lt-dfu
```

The command validates the image, shows the image and device details, and asks for confirmation before it writes anything.

Flashing replaces the firmware but not the presets stored on the amp. To load the presets from a new image, run Settings > Factory Restore on the amp. This replaces all 60 slots.

### Warning

Flashing writes to the amp's internal flash and cannot be undone. An interrupted update may leave the amp unusable.

- Do not unplug the cable, power off the amp or let the computer sleep until it finishes. A full update takes about 3 minutes, and the progress bar may sit at 0% for up to 15 seconds at the start.
- Close Fender Tone and any other program that uses the amp.
- The only integrity check verified is the CRC at the end of the image. Whether the amp checks anything else is not known, so modified images are at your own risk.
- Update mode lives in the bootloader, which an update never writes, so a bad application image can be replaced by flashing another one. If something goes wrong, flash a known-good image or run the official Fender Tone update.

This software comes with no warranty.

## Recovering from a bad preset

A broken preset in the amp's memory can stop it at start-up (the display lights but buttons and USB don't respond). Update mode still works, so:

1. Flash an image with `--force-restore` applied: `opentone patch lt25-2.1.4.bin restore.bin --force-restore`.
2. Power the amp on. It restores all 60 presets from the image's factory set.
3. Flash a normal image again, otherwise every start-up restores the presets.

If the amp still stops before the restore, use `--reformat-flash` instead, which erases the preset memory on the next start-up.

The boot code also restores all presets when Back and Save are held at power-on. That comes from the code and hasn't been tried on hardware yet.
