/* Helpers shared by all hooks. Compiled once per patch, without OT_HOOK_FIELD. */
#define OT_HOOK_FIELD 0
#define OT_HOOK_NAME ot_runtime_unused
#include "../opentone.h"

ot_message *ot_reply_new(uint32_t field)
{
    ot_message *m = fw_new(sizeof(ot_message));
    memset(m, 0, sizeof(ot_message));
    m->envelope = OT_ENVELOPE_RESPONSE;
    m->which = field;
    return m;
}

void ot_send(ot_manager *mgr, ot_message *message)
{
    ot_message *owned = message;
    fw_send(mgr, &owned);                   /* takes ownership by clearing `owned`, or leaves it to us */
    if (owned) fw_sized_delete(owned, sizeof(ot_message));
}

/* GCC may emit calls to these; the firmware's own copies are not exported by address. */
void *memcpy(void *dst, const void *src, size_t n)
{
    uint8_t *d = dst;
    const uint8_t *s = src;
    while (n--) *d++ = *s++;
    return dst;
}

void *memset(void *dst, int c, size_t n)
{
    uint8_t *d = dst;
    while (n--) *d++ = (uint8_t)c;
    return dst;
}
