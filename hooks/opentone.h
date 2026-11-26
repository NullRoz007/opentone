/*
 * OpenTone hook API for the Mustang LT25 firmware.
 *
 * A hook answers one protobuf field number (a "slot") that the stock firmware decodes but does not handle.
 * Install it with:   opentone patch stock.bin out.bin --hook 12=hooks/debug.c
 *
 * A hook file defines exactly one handler:
 *
 *     #include "opentone.h"
 *     OT_HOOK(mgr, request) { ... }
 *
 * The handler runs on the firmware's USB message task, inside MessageCommManager's request dispatcher,
 * after the firmware has checked that a session is active. It may send any number of replies (usually one).
 *
 * Rules:
 *  - No writable globals (.data/.bss): nothing initialises RAM for hook code, so the link fails if a hook
 *    has any. Use the stack, or memory the firmware already owns.
 *  - Keep helper functions `static`; all hook files are linked together.
 *  - Don't block for long: the amp's USB communication waits for the handler to return.
 *  - Addresses of firmware functions are found in the image by the patcher and passed to the linker, so
 *    the same source works on any firmware the patcher recognises.
 */
#ifndef OPENTONE_H
#define OPENTONE_H

#include <stdint.h>
#include <stddef.h>

/* Set by the patcher for each hook file. */
#ifndef OT_HOOK_FIELD
#error "OT_HOOK_FIELD is set by `opentone patch --hook FIELD=FILE`"
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

#define OT_HOOK(mgr, request) void OT_HOOK_NAME(ot_manager *mgr, const ot_message *request)
void OT_HOOK_NAME(ot_manager *mgr, const ot_message *request);

static inline uint32_t ot_read32(const uint8_t *p) { return p[0] | p[1] << 8 | p[2] << 16 | (uint32_t)p[3] << 24; }
static inline uint16_t ot_read16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }
static inline void ot_write32(uint8_t *p, uint32_t v) { p[0] = v; p[1] = v >> 8; p[2] = v >> 16; p[3] = v >> 24; }

#endif
