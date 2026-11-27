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
| Space after the image | `0x081ef100`–`0x08200000`, about 69 KB | **Tested:** the bootloader accepts a longer image (stock + 4 KB booted). Move the CRC to the new end and keep the size a multiple of 32 |

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

Details from the code and hardware:
- **Slot count:** the writer's slot count (`writer+0x1c`) is 500, so the slot API covers 0-499. The journal, factory scratch and marker slots (500+) are outside it.
- **Usage:** the code only reads and writes slots 0-59, 100-116 (both config ranges map there) and 400. A hardware sample of slots 60, 99, 117, 150, 199, 200, 300, 399, 401 and 499 found them all blank.
- **Not binary-safe:** the write copies the data with `strcpy` (`0x81750d0`) into a zeroed 4 KB buffer and programs it page by page (`0x81033d4`), and accepts at most 4,094 bytes. The read (`0x8102b58`) copies the whole slot with the raw read `0x810335c(address, length, buffer)` and then uses `strlen`. Both stop at the first zero byte.

OpenTone assets use slots 120-399, one COBS-encoded chunk per slot (no zero bytes). See `hooks/firmware.h`, "Assets in the data flash".

## Boot

| Address | Behaviour |
|---|---|
| `0x80f12b0(idx)` | Reads a front-panel button straight from GPIO. **Mapped on hardware:** 0 = Edit (PC12), 1 = Back (PD2), 2 = Save (PD4), 3 = Menu (PD5), 4 = Tap (PD6, active high). 5 = PC15 and 6 = PD7 (active high) did not change with any button; 5 reads true at rest |
| `0x80fa8e0` | Menu (button 3) held at power-on: factory test mode. Back + Save (buttons 1 and 2) held together: force a restore of all 60 presets |
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

## Application object

The `MessageCommManager*` passed to every USB handler (and to hooks) holds a pointer to the main application object at offset 0. Both live on `main`'s stack (the main loop, `0x80fa1b8`, has a 0x4a4c-byte frame), not in globals. The debug hook's CONTEXT op returns their addresses at runtime: on 2.1.4 the manager is `0x2407cacc` and the app is `0x2407eba0`.

**Tested on hardware through `call` and `get mem`:**
- session flag (`01` while connected), current index and the modified flag;
- firmware version (`"2.1.4"`, a small-string `std::string`);
- stored preset 1 (2,178 bytes of JSON), live preset (2,131 bytes);
- data-flash slot 100 (`"0"`);
- preset select: setting the index and emitting `mgr+0x20` loaded slot 2 (the live JSON changed to 2,345 bytes), then slot 1 again. There is no RTOS: everything, including hooks, runs in that main loop.

| Offset | Contents | Evidence |
|---|---|---|
| app `+0` | DSP/audio host (getter `0x80ec8e0` returns app itself) | live preset JSON `0x80fe178(out, app)`, set model `0x80fd0a8` |
| app `+0x27c` | Linear gain, set from dB by `0x80fcd10` | handler 114: dB stored at `+0x13d4` (clamped 0..30 after adding 15) |
| app `+0x1240` | Data-flash manager (getter `0x80ec8e4`) | `0x8101814(out, fdm, slot)` reads a preset |
| app `+0x1244` | SlotWriter: read `0x8102c30(out, writer, slot)`, journaled write `0x8102c68(writer, slot, const std::string*)` | slot count at `writer+0x1c` (512) |
| app `+0x132c` | `vector<int>`: user slot order, index to data-flash slot | `0x80ecd8c(out, app, index)` |
| app `+0x1340` | Preset-modified flag (`0x80ecb64` / `0x80ecb4c`) | set after a JSON load (handler 45) |
| app `+0x1344` | Current preset index 0..59 (`0x80ecb7c` / `0x80ecb6c`, clamped, no side effects) | handlers 15, 33, 41, 55 |
| app `+0x1390` | Firmware version `std::string` (`0x80eca2c(out, app)`) | handler 102 |
| app `+0x13dd` | Session active (`0x80ecd6c`) | gate in every handler |
| mgr `+0x20` | Signal emitted after the preset index changes (`0x80f448c` emits a signal: calls each `std::function`) | handlers 33 and 15 |

Other functions:

| Address | Function | Status |
|---|---|---|
| `0x8028c24` / `0x8028c30` | `HAL_GetTick` (uwTick `0x2000b70c`) / `HAL_Delay` | GetTick tested on hardware |
| `0x802b060` / `0x802b06c` / `0x802b078` | `HAL_GPIO_ReadPin` / `WritePin` / `TogglePin` | code |
| `0x80f12b0` | Front-panel input 0-6 | tested: 0 Edit, 1 Back, 2 Save, 3 Menu, 4 Tap; 5, 6 unknown (5 reads 1 at rest) |
| `0x8107110` / `0x81072d8` / `0x8107ad8` | Send preset N (31) / current preset (32) / select-preset reply to the host | code |
| `0x8028ac0`, `0x81744f8`, `0x81745c0` | `strlen`, `memmove`, `memset` | code |
| `0x8146efc` / `0x8145e08` / `0x8145da0` | operator new / delete / sized delete | code |
| `0x80ea45c` | Logger: compiled out (returns immediately) | code |

These are the functions in `hooks/firmware.h`, linked from `hooks/runtime/lt25-2.1.4.sym`.

Not wrapped yet: `0x80ed714(app, index, std::string*, 1)` (store a preset, handler 55) and `0x80f07a0(app, std::string*, 1)` (load preset JSON as current, handlers 45, 55, 58). The meaning of their last argument is unknown.

## Display

The screen is driven by the STM32H7's LTDC (`0x50001000`) from a frame buffer in AXI SRAM. These values were read from the LTDC registers on hardware:

| Register | Value | Meaning |
|---|---|---|
| `AWCR` | `0x009d00a3` | 128 x 160 active area |
| `L1WHPCR` / `L1WVPCR` | `0x009d001e` / `0x00a30004` | layer 1 covers the whole area |
| `L1PFCR` | 2 | RGB565 |
| `L1CFBAR` | `0x240000e0` | frame buffer |
| `L1CFBLR` / `L1CFBLNR` | `0x01000107` / 160 | 256-byte pitch, 160 lines |

The panel is set up over SPI once at boot (`0x810edb8`: reset on PB15, then commands such as `0x3a = 0x55`, 16-bit colour). After that it only shows the buffer.

Pixel (x, y) is at `0x240000e0 + (y * 128 + x) * 2`, with (0, 0) at the top left. **Tested:** writing 20 rows into the buffer showed a band along the top of the screen in the expected left/right order. Writes show immediately.

The firmware redraws the whole screen on UI events: turning a knob, pressing a button, or a host connecting (every OpenTone command sends hello and sync). That replaces anything else in the buffer. Between events, a drawing stays.

Drawing functions (none check bounds):

| Address | Function |
|---|---|
| `0x810ef6c` | `fill(colour)`: whole screen. Tested |
| `0x810ef8c` | `set_pixel(x, y, colour)` |
| `0x810ef9c` | `line(x0, y0, x1, y1, colour)`: Bresenham, colour on the stack |
| `0x810f16c` | `fill_rect(x, y, w, h, colour)`: colour on the stack |
| `0x810f0d4` | Dashed horizontal line `(x, row_offset, length, colour)`. The second argument is `y * 128`, not `y`. It alternates the colour with its inverse every 5 pixels |
| `0x810f11c` | Dashed vertical line `(x, y, length, colour)` |
| `0x810f1a8`, `0x810f2a8` | Text: `(x, y, string, length)`, then the colour and a size on the stack. The size picks one of two fonts (`0x81be944` / `0x81bc714`). Not mapped further yet |

`hooks/firmware.h` wraps fill, pixel, rectangle and line as `ot_lcd_*` with clipping.

## Main loop

`0x80fa1b8(r0 = config)` is the main loop. It runs once, and never returns:
- **Setup:** sets up the controller on its stack (`sp+0x84`, which contains the application object and the MessageCommManager), calls `0x810ef40` (display start) once, then loops.
- **Every pass:** calls `0x80f1f48(controller)`, then waits for the HAL tick to change.
- **Once per millisecond:** USB message processing (`0x8106538`, via `0x80f2e24(controller)`, which returns `controller + 0x30d0`, the MessageCommManager), session timers, the request dispatcher (`0x8107f78`, from `0x80fa2a8`), the UI update `0x80f5650`, and finally `0x80f2b68(controller)` (timers, then `0x812a2d8(controller + 4)`).

Hook entry points (`--hook boot=` / `tick=`), found by exact byte signatures:
- `0x80fa1e8 bl 0x810ef40` becomes `bl ot_boot_entry`. That copies hook `.data`, zeroes `.bss`, then calls display start.
- `0x80fa268 bl 0x80f2b68` becomes `bl ot_tick_entry`. That calls the original first, then the boot hooks once and the tick hooks every time.

Hook RAM is DTCM `0x20018000`-`0x2001f000`. The firmware's `.bss` ends at `0x2000ece0`, and nothing above it is addressed: the only literal is the reset handler's `.bss` end, `.data` has no pointers there, and on hardware the region held unchanged power-on contents. The top of DTCM held the bootloader's stack (return addresses below `0x08020200`), which is dead once the app runs.

## Adding code: USB debug hook (tested on hardware)

Goal: call firmware functions over USB. Built as `fw/lt25-2.1.4-usbcall.bin` by a scratch script (keystone + capstone), not yet part of `opentone patch`.

### Where the code goes

The stock image ends at `0x081ef100`. The hook is appended there, the old CRC slot is zeroed, and the CRC moves to the new end (image grows by 4,096 bytes, still a multiple of 32). Nothing inside the stock image moves.

- Flash: the image already reaches bank 2, so the part has 2 MB and `0x081ef100`–`0x08200000` exists.
- MPU: `main` (`0x80fb34c`) configures only the D2/D3 SRAM regions (`0x30000000`, `0x30020000`, `0x30040000`, `0x38000000`) and enables it with `PRIVDEFENA` (`MPU_CTRL = 5`), so flash keeps the default executable mapping.
- **Tested on hardware:** the bootloader accepts an image longer than stock. `fw/lt25-2.1.4-grow-test.bin` (stock + 4,096 bytes of padding, CRC at the new end) flashed and booted normally.

### Carrier message

Field 12 is `{1: bytes[500]}` in the schema (see the USB protocol document) but has no case in the dispatcher, so it decodes normally and lands in the default case. Its `bytes` type carries binary arguments and replies of up to 500 bytes.

**Tested on hardware with stock 2.1.4:** a field 12 request gets the default case's field 200 `{1: 0}` reply, and the amp keeps working. So the request decodes and reaches the default case.

### Hook

`0x8108fdc` (default case, `ldr r0,[r4]; bl 0x80ecd6c`) becomes `b.w 0x081ef100; nop`. The hook runs the same session check. If the request is field 12 it calls the handler, then jumps to the dispatcher exit `0x8108174`. Otherwise it resumes the default case at `0x8108fe8`. `r4` (`MessageCommManager*`) and `sb` (`Message**`) are callee-saved and set in the prologue, so they are valid on every path into the default case.

The handler builds its reply like the firmware's own builders (for example `0x8104564`): operator new `0x8146efc` (0xfb0 bytes), memset `0x81745c0`, envelope 2, `which` 12. Then it sends it with `0x8105c00(this, &unique_ptr)` and frees it with `0x8145da0` if still owned. 272 bytes in all.

| Request bytes | Op | Reply payload |
|---|---|---|
| `01 00 00 00` addr(u32) len(u16) | PEEK, len ≤ 496 | the bytes |
| `02 00 00 00` addr(u32) len(u16) `00 00` data | POKE | none |
| `03 00 00 00` fn(u32) r0 r1 r2 r3 (u32 each) | CALL, Thumb bit added | r0, r1 |

The reply is `[op][status][0][0][payload]`. Status: 0 ok, 1 bad length, 2 unknown op. Copies use word accesses when the address and length are both 4-aligned, byte accesses otherwise.

OpenTone side: `get mem <addr> [len]`, `set mem <addr> <hex>` and `call <fn> [r0..r3]` (also in the live session). Requests are single 64-byte frames, so a POKE carries at most 41 bytes and OpenTone splits larger writes. On stock firmware these commands report that the hook is missing.

Caveats:
- There is no address check. Reading or calling an invalid address faults, and the amp hangs until a power cycle.
- CALL runs on the USB message task's stack. Functions that block, or that expect to run on another task, may misbehave.

Toolchain note: Keystone 0.9.2 mis-encodes conditional wide branches (`beq.w`/`bne.w`) to absolute addresses. The hook routes them through local `b.w` stubs, and the build script disassembles the blob and checks every branch target.

### Next steps

1. ~~Flash `lt25-2.1.4-grow-test.bin`.~~ Done: longer images are accepted.
2. ~~Flash `lt25-2.1.4-usbcall.bin` and run `opentone get mem 0x08020200 16`.~~ Done: it printed the vector table.
3. ~~Try a harmless CALL~~ Done: `opentone call 0x80ecd6c 0x0801ee26`. That getter returns the byte at `r0 + 0x13dd`, which here is `0x08020203`, the top byte of the initial SP, so r0 should be `0x24`.
4. ~~Move the build into `opentone patch`.~~ Done as `--hook FIELD=FILE` (below).

### Hooks in `opentone patch`

`--hook 12=hooks/debug.c` replaces the scratch build. The debug hook is now C (`hooks/debug.c`) with the same wire format. The pipeline:

1. `FirmwarePatcher.HookSlots()` reads the nanopb schema (`FirmwareInfo.ReadUsbSchema`) and the dispatcher's jump table. A slot is a field that decodes but has no case, excluding 200 (the status reply). On 2.1.4 there are 28: 2, 7, 9, 12 (`bytes[500]`), 14, 31, 32, 35, 36, 37, 38, 40, 42, 44, 46, 47, 50, 52, 56, 59, 61, 63, 100, 103, 107, 109, 111 and 203.
2. `FirmwarePatcher.HookTarget()` finds every address from code, not from constants:
   - the default case (the most common jump-table target);
   - from the default case's own instructions (`ldr r0,[r4]; bl gate; cmp r0,#0; beq.w exit`): the session check, the exit and the resume point (+12);
   - from the default case body: the status-reply builder, `send` and the sized delete. The delete is checked by its `mov.w r1,#0xfb0`;
   - operator new: the first call in the status-reply builder, after `mov.w r0,#0xfb0`.
3. `PatchCommand.CompileHooks` runs `arm-none-eabi-gcc` (Cortex-M7, hard float, `-Os`, freestanding):
   - inputs: `runtime/entry.S`, `runtime/runtime.c`, each hook with `OT_HOOK_FIELD`, `OT_HOOK_NAME` and the slot's sizes, and a generated field-to-hook table;
   - link: `runtime/hooks.ld` at `OT_BASE`, with the firmware symbols passed as `--defsym`. The linker script asserts that the entry point comes first and that there is no `.data` or `.bss`;
   - output: `objcopy` takes `.text` as the blob.
4. `InstallHooks` writes `b.w OT_BASE; nop` over the default case's first 6 bytes. `Build()` then lays out the result: the stock bytes with the CRC slot zeroed, the blob, padding to a multiple of 32, and the new CRC. It re-checks that the branch points at the blob.

The entry stub walks the `{field, hook}` table. On a match it calls `hook(MessageCommManager*, Message*)` and exits the dispatcher. Otherwise it resumes the stock default case, so unknown fields still get the "unsupported" reply.
