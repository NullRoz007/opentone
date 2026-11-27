/*
 * Debug hook: read memory, write memory and call firmware functions over USB.
 * Install on a {1: bytes[n]} slot:   opentone patch stock.bin out.bin --hook 12=hooks/debug.c
 * Use with:   opentone get mem <addr> [len] | set mem <addr> <hex> | call <fn> [r0..r3]
 *
 * Request bytes: [op][0][id lo][id hi][u32 address][args]   (id is echoed so the host can drop stale replies)
 *   1 PEEK  args: u16 length (<= OT_SLOT_BYTES_MAX - 4)          reply payload: the bytes
 *   2 POKE  args: u16 length, 2 zero bytes, data                  reply payload: none
 *   3 CALL  args: 4 or 8 u32 (r0-r3, then 4 on the stack); Thumb bit added   reply payload: r0, r1
 *   4 CONTEXT  no args                                           reply payload: manager, app pointers
 *   5 ALLOC    address = size                                    reply payload: pointer (0 if out of memory)
 *   6 FREE     address = pointer from ALLOC                      reply payload: none
 *   7 SLOT_WRITE  address = RAM; args: u32 slot, u32 length      reply payload: none (status 3 if it failed)
 *   8 SLOT_READ   address = RAM; args: u32 slot, u32 capacity    reply payload: u32 slot length
 * ALLOC, FREE and the slot ops let the host upload data-flash assets (opentone set asset).
 * Reply bytes: [op][status][id lo][id hi][payload]; status 0 ok, 1 bad length, 2 unknown op, 3 failed.
 * Word accesses are used when address and length are both 4-aligned, byte accesses otherwise.
 *
 * There is no address check: reading, writing or calling an invalid address faults and hangs the amp.
 */
#include "opentone.h"

#if OT_SLOT_BYTES_MAX < 16
#error "debug.c needs a {1: bytes[n]} slot such as 12"
#endif

enum { OP_PEEK = 1, OP_POKE = 2, OP_CALL = 3, OP_CONTEXT = 4,
       OP_ALLOC = 5, OP_FREE = 6, OP_SLOT_WRITE = 7, OP_SLOT_READ = 8 };
enum { OK = 0, BAD_LENGTH = 1, UNKNOWN_OP = 2, FAILED = 3 };

static void copy(uint32_t dst, uint32_t src, uint32_t n)
{
    if (((dst | src | n) & 3) == 0) {
        for (; n; n -= 4, dst += 4, src += 4) *(volatile uint32_t *)dst = *(volatile const uint32_t *)src;
    } else {
        for (; n; n--, dst++, src++) *(volatile uint8_t *)dst = *(volatile const uint8_t *)src;
    }
}

/* Up to 8 arguments: r0-r3, then 4 on the stack (AAPCS). Functions that take fewer ignore the rest. */
typedef uint64_t (*call_fn)(uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t);

OT_HOOK(mgr, request)
{
    const ot_bytes *in = &request->sub.bytes;
    ot_message *reply = ot_reply_new(OT_HOOK_FIELD);
    ot_bytes *out = &reply->sub.bytes;

    uint8_t op = in->size > 0 ? in->data[0] : 0;
    uint32_t address = in->size >= 8 ? ot_read32(in->data + 4) : 0;
    uint8_t status = OK;
    out->data[0] = op;
    if (in->size >= 4) { out->data[2] = in->data[2]; out->data[3] = in->data[3]; }   /* request ID */
    out->size = 4;

    switch (op) {
    case OP_PEEK: {
        uint32_t n = in->size >= 10 ? ot_read16(in->data + 8) : 0xffffffff;
        if (n > OT_SLOT_BYTES_MAX - 4) { status = BAD_LENGTH; break; }
        copy((uint32_t)(out->data + 4), address, n);
        out->size = 4 + n;
        break;
    }
    case OP_POKE: {
        uint32_t n = in->size >= 12 ? ot_read16(in->data + 8) : 0xffffffff;
        if (n > in->size - 12) { status = BAD_LENGTH; break; }
        copy(address, (uint32_t)(in->data + 12), n);
        break;
    }
    case OP_CALL: {
        if (in->size < 24) { status = BAD_LENGTH; break; }
        call_fn fn = (call_fn)(address | 1);
        uint32_t a[8] = { 0 };
        uint32_t count = in->size >= 40 ? 8 : 4;
        for (uint32_t i = 0; i < count; i++) a[i] = ot_read32(in->data + 8 + 4 * i);
        uint64_t r = fn(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
        ot_write32(out->data + 4, (uint32_t)r);            /* r0 */
        ot_write32(out->data + 8, (uint32_t)(r >> 32));    /* r1 */
        out->size = 12;
        break;
    }
    case OP_CONTEXT:        /* the objects firmware.h works on, for exploring with PEEK and CALL */
        ot_write32(out->data + 4, (uint32_t)mgr);
        ot_write32(out->data + 8, (uint32_t)ot_app_of(mgr));
        out->size = 12;
        break;
    case OP_ALLOC:
        ot_write32(out->data + 4, (uint32_t)ot_malloc(address));
        out->size = 8;
        break;
    case OP_FREE:
        ot_free((void *)address);
        break;
    case OP_SLOT_WRITE:
        if (in->size < 16) { status = BAD_LENGTH; break; }
        if (!ot_flash_write(mgr, ot_read32(in->data + 8), (const char *)address, ot_read32(in->data + 12))) status = FAILED;
        break;
    case OP_SLOT_READ:
        if (in->size < 16) { status = BAD_LENGTH; break; }
        ot_write32(out->data + 4, (uint32_t)ot_flash_read(mgr, ot_read32(in->data + 8), (char *)address, ot_read32(in->data + 12)));
        out->size = 8;
        break;
    default:
        status = UNKNOWN_OP;
    }

    out->data[1] = status;
    ot_send(mgr, reply);
}
