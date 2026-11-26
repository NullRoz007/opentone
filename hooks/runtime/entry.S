/*
 * Entry stub for OpenTone hooks. The patcher replaces the first 6 bytes of the dispatcher's default case
 * (ldr r0,[r4]; bl session_active) with `b.w ot_entry; nop`.
 *
 * On entry (inside MessageCommManager's request dispatcher):
 *   r4 = MessageCommManager*   sb = Message** (callee-saved, valid on every path into the default case)
 *
 * Symbols from the patcher (found in the image, not hard-coded):
 *   fw_session_active   the gate the default case calls first: returns 0 when no session is active
 *   fw_dispatch_exit    the dispatcher's epilogue
 *   fw_dispatch_resume  the default case after its gate check ("Unsupported Message" + status reply)
 */
    .syntax unified
    .thumb
    .section .text.ot_entry, "ax"
    .global ot_entry
    .type ot_entry, %function
    .thumb_func
ot_entry:
    ldr     r0, [r4]
    bl      fw_session_active
    cmp     r0, #0
    beq     .Lexit                  @ no session: same as the stock default case
    ldr.w   r1, [sb]                @ r1 = request Message*
    ldr     r3, [r1, #4]            @ r3 = field number (oneof `which`)
    ldr     r2, =ot_hooks
.Lnext:
    ldr     r0, [r2]                @ table entry: {u32 field; void (*hook)(mgr, request)}
    cbz     r0, .Lresume            @ end of table: not ours
    cmp     r0, r3
    beq     .Lfound
    adds    r2, #8
    b       .Lnext
.Lfound:
    ldr     r3, [r2, #4]
    mov     r0, r4
    blx     r3                      @ hook(mgr, request)
.Lexit:
    b.w     fw_dispatch_exit
.Lresume:
    b.w     fw_dispatch_resume
    .ltorg
    .size ot_entry, . - ot_entry
