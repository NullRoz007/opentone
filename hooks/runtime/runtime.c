/* Helpers and main-loop entry points shared by all hooks. Compiled once per patch. */
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

/* ---- Main-loop entry points (the patcher redirects two calls in the firmware's main loop here) ---- */

typedef void (*ot_loop_hook)(ot_manager *mgr);
extern const ot_loop_hook ot_boot_hooks[], ot_tick_hooks[];     /* generated, NULL-terminated */
extern uint32_t ot_data_load[], ot_data_start[], ot_data_end[], ot_bss_start[], ot_bss_end[];

void fw_display_start(void);
void fw_controller_tick(void *controller);
ot_manager *fw_controller_manager(void *controller);

static bool ot_booted;

/* Replaces the main loop's call to display_start, made once before the loop: sets up hook RAM first. */
void ot_boot_entry(void)
{
    for (uint32_t *s = ot_data_load, *d = ot_data_start; d < ot_data_end; ) *d++ = *s++;
    for (uint32_t *d = ot_bss_start; d < ot_bss_end; ) *d++ = 0;
    fw_display_start();
}

/* Replaces the main loop's once-per-millisecond controller_tick(controller) call. */
void ot_tick_entry(void *controller)
{
    fw_controller_tick(controller);
    ot_manager *mgr = fw_controller_manager(controller);
    if (!ot_booted) {
        ot_booted = true;
        for (const ot_loop_hook *h = ot_boot_hooks; *h; h++) (*h)(mgr);
    }
    for (const ot_loop_hook *h = ot_tick_hooks; *h; h++) (*h)(mgr);
}
