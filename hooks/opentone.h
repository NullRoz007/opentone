/*
 * OpenTone hook API for the Mustang LT25 firmware.
 *
 * Three kinds of hook, one handler per file:
 *
 *   --hook 12=hooks/debug.c      OT_HOOK(mgr, request) { ... }   a USB message field the stock firmware
 *                                                                decodes but does not handle (a "slot")
 *   --hook boot=hooks/x.c        OT_BOOT(mgr) { ... }            once, on the first main-loop millisecond
 *   --hook tick=hooks/x.c        OT_TICK(mgr) { ... }            every millisecond after the firmware's
 *                                                                own per-millisecond update
 *
 * Message hooks run inside MessageCommManager's request dispatcher, after the firmware has checked that a
 * session is active, and may send any number of replies (usually one). Boot and tick hooks run in the main
 * loop whether or not a host is connected. "Boot" is when the main loop starts: the startup splash and the
 * first main screen are drawn after that, so a hook that draws at boot is drawn over. To draw after
 * startup, use a tick hook that waits (hooks/boot-image.c).
 *
 * Rules:
 *  - Globals are fine: hook .data and .bss live in 28 KB of otherwise unused DTCM, set up at every boot
 *    before the main loop (runtime/hooks.ld). The link fails if they don't fit.
 *  - Keep helper functions and globals `static`; all hook files are linked together.
 *  - Don't block for long: everything (USB, controls, display, audio control) waits for the hook.
 *    Tick hooks run every millisecond, so keep them to a quick check most of the time.
 *  - Files next to the hook can be embedded with OT_INCBIN (below).
 *  - Addresses of firmware functions are found in the image by the patcher and passed to the linker, so
 *    the same source works on any firmware the patcher recognises.
 */
#ifndef OPENTONE_H
#define OPENTONE_H

#include <stdint.h>
#include <stddef.h>

/* Set by the patcher for each hook file (0 for boot and tick hooks). */
#ifndef OT_HOOK_FIELD
#error "OT_HOOK_FIELD is set by `opentone patch --hook FIELD=FILE`"
#endif
#ifndef OT_HOOK_DIR
#define OT_HOOK_DIR "."         /* the hook file's folder, for OT_INCBIN */
#endif

/* Size of the slot's sub-message (from the firmware's schema), and for a {1: bytes[n]} slot, n. */
#ifndef OT_SLOT_SIZE
#define OT_SLOT_SIZE 0
#endif
#ifndef OT_SLOT_BYTES_MAX
#define OT_SLOT_BYTES_MAX 0
#endif

#define OT_MESSAGE_SIZE 0xfb0   /* sizeof the firmware's top-level nanopb message */
#define OT_ENVELOPE_REQUEST 0
#define OT_ENVELOPE_RESPONSE 2

/* nanopb bytes field with 32-bit sizes: {u32 size; u8 data[n]}. */
typedef struct {
    uint32_t size;
    uint8_t data[];
} ot_bytes;

/*
 * The firmware's top-level message (nanopb 0.3, 32-bit descriptors): field 1 is the envelope, then a
 * oneof with one member per command. `which` is the field number of the member that is set; `sub` is
 * the member's struct. Layouts per field are listed in docs/mustang-lt25-usb-protocol.md (Schema).
 */
typedef struct {
    uint8_t envelope;
    uint8_t _pad[3];
    uint32_t which;
    union {
        uint8_t raw[OT_MESSAGE_SIZE - 8];
        ot_bytes bytes;              /* for {1: bytes[n]} slots such as 12 */
    } sub;
} ot_message;

_Static_assert(sizeof(ot_message) == OT_MESSAGE_SIZE, "ot_message must match the firmware's message size");

typedef struct ot_manager ot_manager;   /* MessageCommManager (opaque) */

/* ---- Firmware functions (addresses supplied by the patcher) ---- */

void *fw_new(size_t size);                                  /* operator new */
void fw_sized_delete(void *p, size_t size);                 /* operator delete(void*, size_t) */
void fw_send(ot_manager *mgr, ot_message **owned);          /* MessageCommManager::send(unique_ptr<Message>&) */

/* ---- Runtime helpers (hooks/runtime/runtime.c) ---- */

/* A zeroed response message with `which` set to `field`. Never NULL (operator new does not return NULL). */
ot_message *ot_reply_new(uint32_t field);

/* Sends a message built with ot_reply_new and frees it. */
void ot_send(ot_manager *mgr, ot_message *message);

void *memcpy(void *dst, const void *src, size_t n);
void *memset(void *dst, int c, size_t n);

/* ---- Defining a hook ---- */

#if defined(OT_HOOK_KIND_BOOT)
#define OT_BOOT(mgr) void OT_HOOK_NAME(ot_manager *mgr)
void OT_HOOK_NAME(ot_manager *mgr);
#elif defined(OT_HOOK_KIND_TICK)
#define OT_TICK(mgr) void OT_HOOK_NAME(ot_manager *mgr)
void OT_HOOK_NAME(ot_manager *mgr);
#else
#define OT_HOOK(mgr, request) void OT_HOOK_NAME(ot_manager *mgr, const ot_message *request)
void OT_HOOK_NAME(ot_manager *mgr, const ot_message *request);
#endif

/*
 * Embeds a file (path relative to the hook's folder) as a read-only byte array:
 *     OT_INCBIN(logo, "../assets/hello-red.bmp");     ->  const uint8_t logo[], logo_end[]
 * Hook code is placed after the stock image, where about 69 KB are free.
 */
#define OT_INCBIN(name, file)                                                                    \
    __asm__(".section .rodata." #name ",\"a\"\n"                                                 \
            ".balign 4\n"                                                                        \
            ".global " #name "\n" #name ":\n"                                                    \
            ".incbin \"" OT_HOOK_DIR "/" file "\"\n"                                             \
            ".global " #name "_end\n" #name "_end:\n"                                            \
            ".previous\n");                                                                      \
    extern const uint8_t name[], name##_end[]

/* Firmware functions and readable wrappers (time, GPIO, buttons, presets, data flash). */
#include "firmware.h"

static inline uint32_t ot_read32(const uint8_t *p) { return p[0] | p[1] << 8 | p[2] << 16 | (uint32_t)p[3] << 24; }
static inline uint16_t ot_read16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }
static inline void ot_write32(uint8_t *p, uint32_t v) { p[0] = v; p[1] = v >> 8; p[2] = v >> 16; p[3] = v >> 24; }

#endif
