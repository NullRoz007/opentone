# Mustang LT25: firmware map (2.1.4)

These notes come from static disassembly of `lt25-2.1.4.bin` (Thumb-2, capstone). None of it is tested on hardware unless a row says so. Addresses are runtime (flash) addresses. Subtract `0x08020200` to get the file offset.

## Layout

| Item | Value | Notes |
|---|---|---|
| Load address | `0x08020200` | From the RTTI pointers and `VTOR` set in SystemInit (`0x80fb988`). Not `0x08000000` |
| Bootloader | below `0x08020200` | Never written by an update. Update mode still works while the app hangs at boot, so a bad app image can be recovered |
| Image end | `0x081ef100` (file size `0x1cef00`) | The last 4 bytes are the CRC |
| Reset handler | `0x08145bf4` | Copies `.data`, zeroes `.bss`, calls SystemInit `0x80fb924`, then init_array `0x8173b44`, then main `0x80fb34c` |
| `.data` | LMA `0x081e39d8`, VMA `0x20000000`, `0xb6f0` bytes | Initial values of globals. Patching here changes power-on RAM state |
| `.bss` | `0x2000b6f0`–`0x2000ece0` | DTCM above this is in use (several references), so it is not free |
| Stack top | `0x24080000` | AXI SRAM |
| MCU | STM32H7-class, 2 MB flash, 128 KB sectors (inferred) | |
| Free space inside the image | 52 bytes at `0x081ef0c8`–`0x081ef0fc` | Zero padding between `.data` and the CRC. The other zero runs are live data |
| Space after the image | `0x081ef100`–`0x08200000`, about 69 KB (if the flash is 2 MB) | Whether the bootloader accepts a longer image is **untested**. The Begin frame carries no size |

Integrity: the app has no references to the CRC peripheral or to its own end address, so the bootloader is what checks the trailer CRC. No other checksum or signature was found in the app.

## Data flash (external, 2 MB)

`ProtectedSlotWriter` / `FlashWriter`: 512 slots of `0x1000` bytes, pages of `0x100`. Each slot holds one NUL-terminated string.

| Slot | Use |
|---|---|
| 0–59 | User presets (JSON). Read by `0x8101814`, which clamps the index to 59 |
| 100–116 (`0x64`+n) | Config values as strings. Defaults are written by `0x8101954` (`"0"`, `"1"`, `"15.0"`, …) |
| `0x67`+n | A second config range (`0x8101cfc`) |
| 400 (`0x190`) | Restore journal. Holds `"FMIC PRESET RESTORE"` while a restore runs and is cleared by `0x8101668` when it finishes |
| `0x1f4` / `0x1f5` / `0x1f6` | Journaled-write backup / target index / `"FMIC SLOT READY"`. Replayed at boot after a power loss |
| `0x1f7`–`0x1f9` | Factory-test scratch slots |
| `0x1fa` | `"FMIC FLASH IS FORMATTED"` marker. A mismatch at boot erases the whole chip (`0x8102b04`) |

Helpers: `0x8102c30(out, writer, slot)` reads a slot (empty string if blank). `0x8102c68(writer, slot, string)` does a journaled write.

## Boot

| Address | Behaviour |
|---|---|
| `0x80f12b0(idx)` | Reads a front-panel button straight from GPIO. 0=PC12, 1=PD2, 2=PD4, 3=PD5, 4=PD6 (active high), 5=PC15, 6=PD7 (active high). Physical names not mapped yet |
| `0x80fa8e0` | Button 3 held at power-on: factory test mode. Buttons 1 and 2 held together: force a restore of all 60 presets |
| `0x81263b0` | Boot splash. Calls `checkPresetRestore(force)` at `0x8102258`. It restores all presets when forced, otherwise only blank slots, and shows `RESTORESPLASH` "PRESET nn / 60" |
| Factory preset table | `0x81bb7c0`: 30 pointers to preset JSON. Slots 31–60 use the `EMPTY` template at `0x81a6708` |

Boot-time patches (`opentone patch --force-restore` / `--reformat-flash`):

- Force restore: `0x80fa8f6` `cbz` becomes `nop`, and `0x80fa904` becomes `movs r6,#1`. The force flag is then always set, so every boot restores all presets. **Tested on hardware:** it recovered an amp bricked by a malformed preset (a 4-character name in compact JSON).
- Reformat flash: changes the marker string to `...FORMATTEX`. The first boot erases the data flash. Not yet run on hardware.

## USB request dispatch

`MessageCommManager` handler at `0x8107f78`. `tbh` at `0x8107faa` switches on the request's protobuf field number (3–202).

| Register at dispatch | Meaning |
|---|---|
| `r4` | `MessageCommManager*` |
| `sb` | `Message**`. `[[sb]+4]` is the field number, `[[sb]+8]` is the first varint of the sub-message (for example the slot number for 105) |

Handled field numbers (37 of them): 3, 4, 5, 6, 8, 10, 11, 13, 15, 30, 33, 34, 39, 41, 43, 45, 51, 53, 54, 55, 57, 58, 60, 62, 101, 102, 104, 105, 106, 108, 110, 113, 114, 115, 116, 201, 202. Only 9 of these appear in the captures; the rest still need mapping. Every other number goes to the **default case `0x8108fdc`**, which logs "Unsupported Message from Tone" and sends a status reply.

| Helper | Signature (inferred) |
|---|---|
| `0x80ecd6c([r4])` | Gate: returns 0 if no session. Every handler calls it first |
| `0x81043d4(msg*, std::string*, 2)` | Builds a response with a string field (used by 102 → 103) |
| `0x8104564(msg*, uint8 value, 2)` | Builds a status response (one byte) |
| `0x8105c00(r4, msg*)` | Queues the response for sending |
| `0x8146efc` / `0x8145da0` / `0x8145e08` | operator new / sized delete / delete |
| `0x81745c0`, `0x81744f8` | memset, memcpy (inferred) |

std::string is libstdc++ with SSO: `{char* p, size_t len, char buf[16]}`.

## Safe to edit (data only)

| Target | Rule |
|---|---|
| Factory preset JSON | Keep the byte length (no relocation). Keep `displayName` **exactly 16 characters, space padded**, and keep Fender's formatting. The brick used a 4-character name, compact JSON and trailing spaces. The likely crash is the UI splitting the name into two 8-character lines (unconfirmed). Only read during a restore, so an edit has no effect until a restore runs |
| DSP model defaults JSON (`0x81a7e00`…, table at `0x81eded8`) | Same length rules. Used when a model is selected |
| Version string `0x81a77b8` | The title screen copies it with a **hard-coded length of 5** (`0x812b26a`). Keep 5 characters |
| USB product string `0x81c60f8` | Same length. Do not change VID/PID (driver binding and Fender Tone) |
| UI strings | Same length or shorter with a NUL, within the display width |
| `.data` initial values | Changes power-on globals. Map the variable first |

Avoid the data-flash marker strings unless you intend their side effect.

`opentone patch` puts all of this into practice in `src/Fender/FirmwarePatcher.cs`:
- Structures are found from the image itself: the preset pointer table, the EMPTY template, every `"nodeType":"dspUnit"` model (some start with a `"fixme"` key), the version slot, and code signatures.
- Edits replace only the value's bytes. A preset may shrink, with the rest NUL-filled (the firmware reads with `strlen`). It may grow only into its zero padding up to the next 4-byte boundary (0-3 bytes).
- Value rules:
  - numbers use the model's UI `min`/`max`
  - list indices use `listItems`
  - hidden numbers use 0..1 if every factory value is in 0..1, otherwise the range the factory data uses
  - strings must be a value the factory data already uses for that parameter
- Imported presets (`--pull`, `--import`) are serialised in the factory format: `": "` after keys and no other whitespace. All 31 factory presets re-serialise byte for byte.
- `Build()` checks that:
  - every changed byte lies in an approved region
  - every edited JSON equals the original apart from the requested values
  - every name is still valid
  - the image parses again from scratch

## Images

`Image` widget (vtable `0x81befb8`):
- `Image(x, y, w, h)` is `0x810f458`, `setData(ptr)` is `0x810f490`, and `draw` is `0x810f3fc`.
- Pixels are uncompressed RGB565, little-endian, row-major, drawn one at a time through `setPixel` at `0x810ef8c`.
- The screen is 128x160 (LTDC).

| Name | Data | Size | Position | Used by |
|---|---|---|---|---|
| `splash` | `0x081c3260` (11,008 bytes) | 128x43 | x=0, y=57 | StartupSplash page (`0x814075c`), the white Fender logo on black |
| `tone` | `0x081c03c0` (7,656 bytes) | 66x58 | x=31, y=36 | Page at `0x812bb..`, the Fender Tone app icon (inferred: shown when Tone connects) |

Each image's size and position are immediates in the drawing code, and its data is packed directly against other data. In-place replacement at the same size is therefore pure data. A larger or full-screen image needs the space after the image (see "Adding code") plus a change to the constructor immediates and the data pointer.

## Adding code (plan, untested)

1. **Space:** first find out whether the bootloader accepts an image longer than stock. Flash stock plus a block of zeros with the CRC recomputed, and check that it boots. If it does, put the code after `.data` and move the CRC to the new end. If not, only the 52-byte tail is available.
2. **Hook:** replace `ldr r0,[r4]; bl 0x80ecd6c` (6 bytes) at the default case `0x8108fdc` with a `b.w` to the new code plus a `nop`. The new code checks `[[sb]+4]` against custom field numbers (unused numbers in 3–202, or anything above 202). For any other number it re-runs the two displaced instructions and does `b.w 0x8108fe2`.
3. **First command:** a memory peek. Read the address and length from the request, hex-encode the bytes into a std::string, then call `0x81043d4` and `0x8105c00`. That gives a read primitive for mapping everything else, using only reply formats the firmware already sends.
4. **Toolchain:** `arm-none-eabi-gcc -mthumb -mcpu=cortex-m7`, linked at a fixed address, calling firmware functions by absolute address. Add a patch step to OpenTone that applies the hook and the blob and rewrites the CRC.
