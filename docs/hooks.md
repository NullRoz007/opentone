# Hooks: your own code in the firmware

`opentone patch` can compile C files and install them in a firmware image. All hooks are linked into one block placed after the stock image. The firmware addresses the code needs are found in the image itself (or in a symbol table that is checked against it), so an unrecognised build is refused rather than mis-patched.

Hooks need the Arm GNU toolchain (`arm-none-eabi-gcc`) on PATH, for example `scoop bucket add extras` then `scoop install gcc-arm-none-eabi`.

## Kinds of hook

| Option | Handler | Runs |
|---|---|---|
| `--hook 12=FILE` | `OT_HOOK(mgr, request)` | When a host sends that USB message field. The field must be one the firmware decodes but doesn't handle: `patch --info` lists them under "Hook slots" (28 on 2.1.4; field 12 carries up to 500 bytes) |
| `--hook boot=FILE` | `OT_BOOT(mgr)` | Once, when the main loop starts. The startup splash and first main screen are drawn after this |
| `--hook tick=FILE` | `OT_TICK(mgr)` | Every millisecond, after the firmware's own update |

Message hooks are reached from the request dispatcher's "unsupported message" case. Boot and tick hooks are reached from two calls in the main loop. The [firmware map](mustang-lt25-firmware-map.md) has the details.

```
opentone patch lt25-2.1.4.bin debug.bin --hook 12=hooks/debug.c
opentone flash debug.bin -d lt-dfu
opentone get mem 0x08020200 16 -d mustang-lt25          # read memory
opentone call 0x80ecd6c 0x0801ee26 -d mustang-lt25     # call a firmware function
opentone get context -d mustang-lt25                    # addresses of the objects firmware.h uses
```

## Files

| File | Purpose |
|---|---|
| `hooks/opentone.h` | Hook API: the hook macros, the message layout, `ot_reply_new` and `ot_send`, `OT_INCBIN`, and the rules hooks must follow |
| `hooks/firmware.h` | Firmware functions: time, GPIO, front-panel buttons, presets, sending presets to the host, the data flash and assets, memory, and drawing on the screen |
| `hooks/debug.c` | The debug hook on field 12: PEEK, POKE, CALL, CONTEXT, and the asset transfer operations. Used by `get`/`set mem`, `call`, `get context` and `get`/`set asset` |
| `hooks/boot-image.c` | Tick hook that shows `assets/hello-red.rle` (embedded in the firmware) full screen once startup has finished |
| `hooks/boot-asset.c` | The same, but the image is loaded from the data flash, so it can change without reflashing |
| `hooks/tools/bmp2rle.py` | Converts a 24-bit BMP to the run-length encoded `.rle` format that `ot_lcd_rle` draws, about 10x smaller for flat artwork |
| `hooks/runtime/` | The entry stubs, shared helpers, linker script and firmware symbol table (`lt25-2.1.4.sym`) |

## The firmware API

`firmware.h` wraps firmware functions in readable `ot_*` calls:

| Area | Examples |
|---|---|
| Time | `ot_millis()`, `ot_delay_ms(ms)` |
| GPIO and buttons | `ot_gpio_read(OT_GPIO('D'), OT_PIN(2))`, `ot_button(OT_BUTTON_TAP)` (Edit, Back, Save, Menu, Tap) |
| Presets | `ot_current_preset(mgr)`, `ot_select_preset(mgr, 5)`, `ot_stored_preset(...)`, `ot_current_preset_json(...)` |
| Host | `ot_send_preset(mgr, slot)`, `ot_send_current_preset(mgr)` |
| Data flash | `ot_flash_read(mgr, slot, buf, n)`, `ot_flash_write(...)`, `ot_asset_load(mgr, slot, &len)` |
| Memory | `ot_malloc(size)`, `ot_free(p)` |
| Screen | `ot_lcd_clear(OT_BLACK)`, `ot_lcd_pixel`, `ot_lcd_rect(x, y, w, h, OT_RED)`, `ot_lcd_line`, `ot_lcd_rle`, `ot_lcd_bmp` |

Each function is marked `[tested]` (called on an amp), `[code]` (read from the disassembly) or `[inferred]`. The addresses come from `hooks/runtime/lt25-2.1.4.sym`. The patcher uses a table only if the image has the expected code at every address, so on another firmware build a hook that uses these functions fails to link rather than calling the wrong code.

The screen is a 128x160 RGB565 frame buffer. A drawing stays until the firmware next redraws the screen: a knob, a button, or a host connecting.

## Assets in the data flash

The amp's external data flash has about 1.1 MB free (slots 120-399), far more than the 69 KB after the firmware.
- `opentone set asset <slot> <file>` stores a file there and reads it back to check it.
- `get asset <slot> <file>` reads one back.
- Both need the debug hook. A hook loads an asset with `ot_asset_load(mgr, slot, &length)`.
- Assets survive firmware updates, but not a data-flash reformat.

```
python hooks/tools/bmp2rle.py assets/hello-red.bmp assets/hello-red.rle
opentone set asset 120 assets/hello-red.rle -d mustang-lt25
opentone patch lt25-2.1.4.bin out.bin --hook 12=hooks/debug.c --hook tick=hooks/boot-asset.c
```

## Rules

- **Globals:** hooks can use global variables. Their RAM is 28 KB of DTCM the firmware doesn't use, set up at every boot. Keep helpers and globals `static`, because all hooks are linked together.
- **Keep them short:** hooks run in the amp's main loop, which has no RTOS, so USB, controls and the display wait for them. A tick hook runs every millisecond.
- **Space:** `OT_INCBIN(name, "file")` embeds a file next to the hook. About 69 KB of flash is free for hook code and data; use assets for anything larger.
- **No address checks:** reading, writing or calling a bad address hangs the amp until you power-cycle it.
