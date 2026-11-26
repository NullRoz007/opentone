/*
 * Debug hook: read memory, write memory and call firmware functions over USB.
 * Install on a {1: bytes[n]} slot:   opentone patch stock.bin out.bin --hook 12=hooks/debug.c
 * Use with:   opentone get mem <addr> [len] | set mem <addr> <hex> | call <fn> [r0..r3]
 *
 * Request bytes: [op][0][0][0][u32 address][args]
 *   1 PEEK  args: u16 length (<= OT_SLOT_BYTES_MAX - 4)          reply payload: the bytes
 *   2 POKE  args: u16 length, 2 zero bytes, data                  reply payload: none
 *   3 CALL  args: r0..r3 (u32 each); the Thumb bit is added       reply payload: r0, r1
 * Reply bytes: [op][status][0][0][payload]; status 0 ok, 1 bad length, 2 unknown op.
 * Word accesses are used when address and length are both 4-aligned, byte accesses otherwise.
 *
 * There is no address check: reading, writing or calling an invalid address faults and hangs the amp.
 */
#include "opentone.h"

#if OT_SLOT_BYTES_MAX < 16
#error "debug.c needs a {1: bytes[n]} slot such as 12"
#endif

enum { OP_PEEK = 1, OP_POKE = 2, OP_CALL = 3 };
enum { OK = 0, BAD_LENGTH = 1, UNKNOWN_OP = 2 };

static void copy(uint32_t dst, uint32_t src, uint32_t n)
{
    if (((dst | src | n) & 3) == 0) {
        for (; n; n -= 4, dst += 4, src += 4) *(volatile uint32_t *)dst = *(volatile const uint32_t *)src;
    } else {
        for (; n; n--, dst++, src++) *(volatile uint8_t *)dst = *(volatile const uint8_t *)src;
    }
}

typedef uint64_t (*call_fn)(uint32_t, uint32_t, uint32_t, uint32_t);

OT_HOOK(mgr, request)
{
    const ot_bytes *in = &request->sub.bytes;
    ot_message *reply = ot_reply_new(OT_HOOK_FIELD);
    ot_bytes *out = &reply->sub.bytes;

    uint8_t op = in->size > 0 ? in->data[0] : 0;
    uint32_t address = in->size >= 8 ? ot_read32(in->data + 4) : 0;
    uint8_t status = OK;
    out->data[0] = op;
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
        uint64_t r = fn(ot_read32(in->data + 8), ot_read32(in->data + 12), ot_read32(in->data + 16), ot_read32(in->data + 20));
        ot_write32(out->data + 4, (uint32_t)r);            /* r0 */
        ot_write32(out->data + 8, (uint32_t)(r >> 32));    /* r1 */
        out->size = 12;
        break;
    }
    default:
        status = UNKNOWN_OP;
    }

    out->data[1] = status;
    ot_send(mgr, reply);
}
