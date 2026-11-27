/*
 * Firmware API for hooks: Mustang LT25 firmware 2.1.4.
 *
 * Two layers:
 *   ot_*  readable wrappers. Use these.
 *   fw_*  the raw firmware functions they call, linked from hooks/runtime/lt25-2.1.4.sym. The patcher
 *         links a symbol only if the image has the expected code at its address, so a hook that uses
 *         one fails to link on an unrecognised build instead of calling the wrong code.
 *
 * Status of each function (most come from static disassembly):
 *   [tested]   called on hardware through the debug hook
 *   [code]     behaviour read from the disassembly and from how the firmware itself calls it
 *   [inferred] meaning guessed from context; the mechanics are from the code
 *
 * Not available: logging. The firmware's logger (0x80ea45c) is compiled out and does nothing.
 *
 * The firmware is a bare-metal main loop (no RTOS). Hooks run inside it, so anything that waits
 * (ot_delay_ms, long loops) stalls USB, audio control and the display until it returns.
 */
#ifndef OPENTONE_FIRMWARE_H
#define OPENTONE_FIRMWARE_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

typedef struct ot_manager ot_manager;   /* MessageCommManager: passed to every hook */
typedef struct ot_app ot_app;           /* the main application object, *(ot_app **)manager */

/* libstdc++ std::string (32-bit, small-string optimised). Only used to talk to firmware functions. */
typedef struct {
    char *data;
    uint32_t size;
    union { char local[16]; uint32_t capacity; } u;
} ot_std_string;

/* ---- Raw firmware functions ----------------------------------------------------------------------- */

uint32_t fw_hal_get_tick(void);
void     fw_hal_delay(uint32_t ms);
uint32_t fw_gpio_read(volatile void *port, uint16_t pin);
void     fw_gpio_write(volatile void *port, uint16_t pin, uint32_t state);
void     fw_gpio_toggle(volatile void *port, uint16_t pin);
uint32_t fw_button_read(uint32_t index);
size_t   fw_strlen(const char *s);
void     fw_delete(void *p);
void     fw_signal_emit(void *signal);

uint32_t fw_app_preset_index(ot_app *app);
void     fw_app_set_preset_index(ot_app *app, uint32_t index);
uint32_t fw_app_preset_modified(ot_app *app);
void     fw_app_set_preset_modified(ot_app *app, uint32_t modified);
void     fw_app_firmware_version(ot_std_string *out, ot_app *app);
void     fw_app_stored_preset(ot_std_string *out, ot_app *app, uint32_t index);
void     fw_dsp_current_preset(ot_std_string *out, ot_app *app);

void     fw_flash_read_slot(ot_std_string *out, void *writer, uint32_t slot);
uint32_t fw_flash_write_slot(void *writer, uint32_t slot, const ot_std_string *data);

void     fw_send_preset(ot_manager *mgr, uint32_t slot);
void     fw_send_current_preset(ot_manager *mgr, uint32_t envelope);
void     fw_send_selected_preset(ot_manager *mgr, uint32_t envelope);

void     fw_lcd_fill(uint32_t colour);
void     fw_lcd_set_pixel(uint32_t x, uint32_t y, uint32_t colour);
void     fw_lcd_line(int32_t x0, int32_t y0, int32_t x1, int32_t y1, uint32_t colour);
void     fw_lcd_fill_rect(uint32_t x, uint32_t y, uint32_t w, uint32_t h, uint32_t colour);

void    *fw_malloc(size_t size);
void     fw_free(void *p);

/* Object layout used below (from the code; 2.1.4 only). */
#define OT_APP_DATA_FLASH_WRITER   0x1244   /* SlotWriter inside the data-flash manager at +0x1240 */
#define OT_MGR_PRESET_SELECTED     0x20     /* Signal emitted after the current preset index changes */

static inline uint32_t ot__le32(const uint8_t *p) { return p[0] | p[1] << 8 | p[2] << 16 | (uint32_t)p[3] << 24; }
static inline uint16_t ot__le16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }

/* Copies a firmware std::string into buf (NUL-terminated, truncated to n-1) and frees it.
 * Returns the full length, so a return value >= n means the copy was truncated. */
static inline size_t ot__take_string(ot_std_string *s, char *buf, size_t n)
{
    size_t len = s->size;
    if (buf && n) {
        size_t copy = len < n - 1 ? len : n - 1;
        for (size_t i = 0; i < copy; i++) buf[i] = s->data[i];
        buf[copy] = 0;
    }
    if (s->data != s->u.local) fw_delete(s->data);
    return len;
}

/* ---- Time ----------------------------------------------------------------------------------------- */

/* [tested] Milliseconds since boot. Wraps after about 49 days. */
static inline uint32_t ot_millis(void) { return fw_hal_get_tick(); }

/* [tested] Waits ms milliseconds (3000 measured as 3004-3020 ms on the amp's tick). Blocks the whole
 * firmware meanwhile (see the note at the top). There is no watchdog, so long waits don't reset the amp. */
static inline void ot_delay_ms(uint32_t ms) { fw_hal_delay(ms); }

/* ---- GPIO (STM32H7) ------------------------------------------------------------------------------- */

#define OT_GPIO(letter) ((volatile void *)(0x58020000u + 0x400u * (uint32_t)((letter) - 'A')))
#define OT_PIN(n)       ((uint16_t)(1u << (n)))

/* [tested] Pin level: true when high. Example: ot_gpio_read(OT_GPIO('D'), OT_PIN(2)). */
static inline bool ot_gpio_read(volatile void *port, uint16_t pin) { return fw_gpio_read(port, pin) != 0; }

/* [tested] Drives an output pin (tested on an input pin's output register, so nothing was driven). Only for pins the firmware configured as outputs; writing pins it
 * uses for other things (display, codec, flash) will confuse it. */
static inline void ot_gpio_write(volatile void *port, uint16_t pin, bool high) { fw_gpio_write(port, pin, high); }
static inline void ot_gpio_toggle(volatile void *port, uint16_t pin) { fw_gpio_toggle(port, pin); }

/* [tested] Front-panel buttons, as read by the boot code. Returns true while the button is held.
 * Mapped on hardware by pressing each one:
 *   0 Edit (PC12, low when held)   1 Back (PD2, low)    2 Save (PD4, low)    3 Menu (PD5, low)
 *   4 Tap (PD6, high)
 * Inputs 5 (PC15) and 6 (PD7) are read by the same code but did not change when any button was pressed;
 * 5 reads true at rest. Their purpose is unknown.
 * Boot combinations (docs): Back + Save held at power-on restores all presets; Menu enters factory test. */
enum { OT_BUTTON_EDIT = 0, OT_BUTTON_BACK = 1, OT_BUTTON_SAVE = 2, OT_BUTTON_MENU = 3, OT_BUTTON_TAP = 4 };
static inline bool ot_button(uint32_t index) { return index <= 6 && fw_button_read(index) != 0; }

/* ---- Application state ---------------------------------------------------------------------------- */

static inline ot_app *ot_app_of(ot_manager *mgr) { return *(ot_app **)mgr; }

/* [tested] True while a host (Fender Tone or OpenTone) has an open session. Hooks only run inside one. */
static inline bool ot_session_active(ot_manager *mgr) { return *((uint8_t *)ot_app_of(mgr) + 0x13dd) != 0; }

/* [tested] The current preset slot, 1-60. */
static inline uint32_t ot_current_preset(ot_manager *mgr) { return fw_app_preset_index(ot_app_of(mgr)) + 1; }

/* [tested] Selects preset slot 1-60 the way the USB "select preset" command (field 33) does: set the
 * index, then emit the preset-selected signal that loads it and updates the display. Tested: the live
 * preset changed to the selected slot's and back. The host is not
 * told; call ot_report_selected_preset() afterwards if it should be. */
static inline void ot_select_preset(ot_manager *mgr, uint32_t slot)
{
    if (slot < 1 || slot > 60) return;
    fw_app_set_preset_index(ot_app_of(mgr), slot - 1);
    fw_signal_emit((uint8_t *)mgr + OT_MGR_PRESET_SELECTED);
}

/* [tested] Sends the select-preset reply (field 37) the firmware sends after field 33, so a connected host follows. */
static inline void ot_report_selected_preset(ot_manager *mgr) { fw_send_selected_preset(mgr, 2); }

/* [tested] True if the current preset has unsaved edits. */
static inline bool ot_preset_modified(ot_manager *mgr) { return fw_app_preset_modified(ot_app_of(mgr)) != 0; }

/* [tested] The firmware version, e.g. "2.1.4". Returns its length. */
static inline size_t ot_firmware_version(ot_manager *mgr, char *buf, size_t n)
{
    ot_std_string s;
    fw_app_firmware_version(&s, ot_app_of(mgr));
    return ot__take_string(&s, buf, n);
}

/* ---- Presets -------------------------------------------------------------------------------------- */
/* Preset JSON is 1.9-2.5 KB. The hook runs on the main stack, so prefer a buffer from fw_new() or the
 * reply message over a large local array. */

/* [tested] The stored JSON of preset slot 1-60, read from the data flash through the user's slot order.
 * Returns its length (0 for an empty slot). */
static inline size_t ot_stored_preset(ot_manager *mgr, uint32_t slot, char *buf, size_t n)
{
    /* The firmware maps an out-of-range index to slot 0, so check here. */
    if (slot < 1 || slot > 60) { if (buf && n) buf[0] = 0; return 0; }
    ot_std_string s;
    fw_app_stored_preset(&s, ot_app_of(mgr), slot - 1);
    return ot__take_string(&s, buf, n);
}

/* [tested] The live preset (including unsaved edits) as JSON, as sent for USB field 32. */
static inline size_t ot_current_preset_json(ot_manager *mgr, char *buf, size_t n)
{
    ot_std_string s;
    fw_dsp_current_preset(&s, ot_app_of(mgr));
    return ot__take_string(&s, buf, n);
}

/* [tested] Sends preset slot 1-60 (field 31) or the live preset (field 32) to the host, as the firmware
 * does for requests 105 and 104. */
static inline void ot_send_preset(ot_manager *mgr, uint32_t slot) { fw_send_preset(mgr, slot); }
static inline void ot_send_current_preset(ot_manager *mgr) { fw_send_current_preset(mgr, 2); }

/* ---- Data flash (external, 4 KB slots, one NUL-terminated string each) ----------------------------- */
/* The slot API covers slots 0-499 (the writer's slot count, at writer+0x1c, is 500). Slots 500+ hold the
 * write journal (0x1f4-0x1f6), factory scratch (0x1f7-0x1f9) and the "FMIC FLASH IS FORMATTED" marker
 * (0x1fa); they are outside this API. Used slots (docs/mustang-lt25-firmware-map.md): 0-59 presets,
 * 100-116 and 0x67+ config, 400 restore journal. */

/* [tested] Reads a data-flash slot (0-499). Returns its length; 0 for a blank or out-of-range slot. */
static inline size_t ot_flash_read(ot_manager *mgr, uint32_t slot, char *buf, size_t n)
{
    ot_std_string s;
    fw_flash_read_slot(&s, (uint8_t *)ot_app_of(mgr) + OT_APP_DATA_FLASH_WRITER, slot);
    return ot__take_string(&s, buf, n);
}

/* [tested] Writes a string to a data-flash slot (0-499) with the firmware's journaled write, which
 * survives a power cut mid-write. Returns false if the slot is out of range. Writing "" (len 0) blanks
 * a slot as far as ot_flash_read is concerned. Tested on unused slot 450: written, read back, blanked.
 * The firmware reads these slots at boot: a malformed preset or config can stop the amp starting.
 * That is recoverable (docs/firmware.md, "Recovering from a bad preset"); keep presets in the factory JSON format. The amp does not reload a slot it has already read. */
static inline bool ot_flash_write(ot_manager *mgr, uint32_t slot, const char *data, size_t len)
{
    ot_std_string s = { (char *)data, (uint32_t)len, { { 0 } } };   /* read-only view; not freed */
    return fw_flash_write_slot((uint8_t *)ot_app_of(mgr) + OT_APP_DATA_FLASH_WRITER, slot, &s) != 0;
}

/* ---- Display ------------------------------------------------------------------------------------- */
/* The screen is a 128 x 160 RGB565 frame buffer in RAM that the LTDC scans out continuously, so anything
 * written shows immediately. (0, 0) is the top left; tested by writing a pattern to the buffer.
 * The firmware redraws the whole screen on UI events (turning a knob, pressing a button, a host
 * connecting), which replaces anything a hook drew. Between events, the drawing stays.
 * The firmware's drawing functions don't check bounds; the ot_lcd_* wrappers clip, so use them. */

#define OT_LCD_WIDTH  128
#define OT_LCD_HEIGHT 160

/* RGB565 from 8-bit channels. */
static inline uint16_t ot_rgb(uint8_t r, uint8_t g, uint8_t b)
{
    return (uint16_t)(((r & 0xf8) << 8) | ((g & 0xfc) << 3) | (b >> 3));
}
enum {
    OT_BLACK = 0x0000, OT_WHITE = 0xffff, OT_RED = 0xf800, OT_GREEN = 0x07e0, OT_BLUE = 0x001f,
    OT_YELLOW = 0xffe0, OT_CYAN = 0x07ff, OT_MAGENTA = 0xf81f, OT_GREY = 0x8410,
};

/* [tested] The frame buffer, from the LTDC layer 1 address register (0x240000e0 on 2.1.4). Pixel (x, y)
 * is ot_lcd_framebuffer()[y * OT_LCD_WIDTH + x]. */
static inline volatile uint16_t *ot_lcd_framebuffer(void) { return (volatile uint16_t *)*(volatile uint32_t *)0x500010acu; }

/* [tested] Fills the whole screen with one colour (red and blue fills tested). */
static inline void ot_lcd_clear(uint16_t colour) { fw_lcd_fill(colour); }

/* [code] Sets one pixel. Off-screen pixels are ignored. */
static inline void ot_lcd_pixel(int32_t x, int32_t y, uint16_t colour)
{
    if (x >= 0 && y >= 0 && x < OT_LCD_WIDTH && y < OT_LCD_HEIGHT) fw_lcd_set_pixel((uint32_t)x, (uint32_t)y, colour);
}

/* [code] Fills a w x h rectangle at (x, y), clipped to the screen. */
static inline void ot_lcd_rect(int32_t x, int32_t y, int32_t w, int32_t h, uint16_t colour)
{
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > OT_LCD_WIDTH) w = OT_LCD_WIDTH - x;
    if (y + h > OT_LCD_HEIGHT) h = OT_LCD_HEIGHT - y;
    if (w > 0 && h > 0) fw_lcd_fill_rect((uint32_t)x, (uint32_t)y, (uint32_t)w, (uint32_t)h, colour);
}

/* [code] Draws a 1-pixel line. Both ends must be on screen (the firmware's line doesn't clip); lines
 * with an end off screen are not drawn. */
static inline void ot_lcd_line(int32_t x0, int32_t y0, int32_t x1, int32_t y1, uint16_t colour)
{
    if (x0 < 0 || y0 < 0 || x1 < 0 || y1 < 0 ||
        x0 >= OT_LCD_WIDTH || x1 >= OT_LCD_WIDTH || y0 >= OT_LCD_HEIGHT || y1 >= OT_LCD_HEIGHT) return;
    fw_lcd_line(x0, y0, x1, y1, colour);
}

/* [code] Draws an OpenTone .rle image (hooks/tools/bmp2rle.py) with its top-left corner at (x, y), clipped
 * to the screen. Format: "OTR1", u16 width, u16 height, then (u8 count, u16 RGB565 colour) runs, left to
 * right and top to bottom. About 10x smaller than a BMP for flat artwork. Returns false if it isn't one. */
static inline bool ot_lcd_rle(const uint8_t *rle, size_t len, int32_t x, int32_t y)
{
    if (len < 8 || rle[0] != 'O' || rle[1] != 'T' || rle[2] != 'R' || rle[3] != '1') return false;
    int32_t w = ot__le16(rle + 4), h = ot__le16(rle + 6);
    if (w == 0 || h == 0) return false;
    volatile uint16_t *fb = ot_lcd_framebuffer();
    int32_t col = 0, row = 0;
    for (size_t o = 8; o + 3 <= len && row < h; o += 3) {
        uint32_t n = rle[o];
        uint16_t colour = ot__le16(rle + o + 1);
        while (n-- && row < h) {
            int32_t sx = x + col, sy = y + row;
            if (sx >= 0 && sy >= 0 && sx < OT_LCD_WIDTH && sy < OT_LCD_HEIGHT) fb[sy * OT_LCD_WIDTH + sx] = colour;
            if (++col == w) { col = 0; row++; }
        }
    }
    return true;
}

/* [code] Draws an uncompressed 24-bit BMP (a whole .bmp file, e.g. from OT_INCBIN) with its top-left
 * corner at (x, y), clipped to the screen. Returns false if it isn't a 24-bit uncompressed BMP. */
static inline bool ot_lcd_bmp(const uint8_t *bmp, size_t len, int32_t x, int32_t y)
{
    if (len < 54 || bmp[0] != 'B' || bmp[1] != 'M') return false;
    uint32_t offset = ot__le32(bmp + 10);
    int32_t w = (int32_t)ot__le32(bmp + 18), h = (int32_t)ot__le32(bmp + 22);
    if (ot__le16(bmp + 28) != 24 || ot__le32(bmp + 30) != 0 || w <= 0 || h == 0) return false;
    bool bottom_up = h > 0;
    if (h < 0) h = -h;
    uint32_t stride = ((uint32_t)w * 3 + 3) & ~3u;                 /* rows are padded to 4 bytes */
    if (offset + stride * (uint32_t)h > len) return false;

    volatile uint16_t *fb = ot_lcd_framebuffer();
    for (int32_t row = 0; row < h; row++) {
        int32_t sy = y + row;
        if (sy < 0 || sy >= OT_LCD_HEIGHT) continue;
        const uint8_t *src = bmp + offset + stride * (uint32_t)(bottom_up ? h - 1 - row : row);
        for (int32_t col = 0; col < w; col++) {
            int32_t sx = x + col;
            if (sx < 0 || sx >= OT_LCD_WIDTH) continue;
            const uint8_t *p = src + 3 * col;                       /* B, G, R */
            fb[sy * OT_LCD_WIDTH + sx] = ot_rgb(p[2], p[1], p[0]);
        }
    }
    return true;
}

/* ---- Memory ---------------------------------------------------------------------------------------- */

/* [code] The firmware's heap (newlib malloc/free). ot_malloc returns NULL when out of memory, unlike
 * fw_new (operator new), which never returns NULL and hangs the amp instead. 40 KB allocations worked. */
static inline void *ot_malloc(size_t size) { return fw_malloc(size); }
static inline void ot_free(void *p) { fw_free(p); }

/* ---- Assets in the data flash ---------------------------------------------------------------------- */
/* Large data (images and so on) can live in the external data flash instead of the 69 KB after the
 * firmware image. The firmware uses slots 0-59 (presets), 100-116 (config) and 400 (restore journal);
 * assets use slots 120-399, about 1.1 MB. Upload with:  opentone set asset <slot> <file>
 *
 * Slots hold NUL-terminated strings (the firmware's slot functions use strcpy and strlen), so each
 * slot holds one COBS-encoded chunk, which contains no zero bytes. The stream across consecutive slots
 * is a 16-byte header ("OTA1", u32 length, u32 CRC-32 of the data, u32 0) followed by the data, cut
 * into OT_ASSET_CHUNK-byte chunks. */

#define OT_ASSET_FIRST_SLOT 120
#define OT_ASSET_LAST_SLOT  399
#define OT_ASSET_CHUNK      4064    /* raw bytes per slot; COBS-encoded it stays within the 4094-byte limit */

/* COBS decode; safe in place (output never overtakes input). Returns the decoded length. */
static inline size_t ot__cobs_decode(const uint8_t *in, size_t n, uint8_t *out)
{
    size_t i = 0, o = 0;
    while (i < n) {
        uint8_t code = in[i++];
        for (uint8_t k = 1; k < code && i < n; k++) out[o++] = in[i++];
        if (code != 0xff && i < n) out[o++] = 0;
    }
    return o;
}

static inline uint32_t ot__crc32(const uint8_t *p, size_t n)
{
    uint32_t crc = 0xffffffffu;
    while (n--) {
        crc ^= *p++;
        for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & -(crc & 1));
    }
    return ~crc;
}

/* [code] Loads the asset that starts at `slot` into a new ot_malloc buffer (free it with ot_free) and
 * stores its length in *len. Returns NULL if there is no valid asset there (bad header, missing chunk,
 * CRC mismatch) or not enough memory. Reads one 4 KB slot per chunk, so a 40 KB asset takes 11 reads. */
static inline uint8_t *ot_asset_load(ot_manager *mgr, uint32_t slot, size_t *len)
{
    if (slot < OT_ASSET_FIRST_SLOT || slot > OT_ASSET_LAST_SLOT) return NULL;
    uint8_t *chunk = ot_malloc(4096);
    if (!chunk) return NULL;
    uint8_t *data = NULL;
    size_t total = 0, have = 0;
    uint32_t crc = 0;
    for (uint32_t s = slot; s <= OT_ASSET_LAST_SLOT; s++) {
        size_t n = ot_flash_read(mgr, s, (char *)chunk, 4096);
        if (n == 0 || n >= 4096) break;
        n = ot__cobs_decode(chunk, n, chunk);
        const uint8_t *p = chunk;
        if (s == slot) {
            if (n < 16 || p[0] != 'O' || p[1] != 'T' || p[2] != 'A' || p[3] != '1') break;
            total = ot__le32(p + 4);
            crc = ot__le32(p + 8);
            data = ot_malloc(total ? total : 1);
            if (!data) break;
            p += 16; n -= 16;
        }
        if (n > total - have) n = total - have;
        for (size_t i = 0; i < n; i++) data[have + i] = p[i];
        have += n;
        if (have == total) break;
    }
    ot_free(chunk);
    if (data && (have != total || ot__crc32(data, total) != crc)) { ot_free(data); data = NULL; }
    if (data && len) *len = total;
    return data;
}

#endif
